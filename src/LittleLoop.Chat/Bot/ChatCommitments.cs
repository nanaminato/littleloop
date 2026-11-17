using System.Security.Claims;
using System.Text.Json;
using LittleLoop.Core;
using Microsoft.AspNetCore.SignalR;

// Opt-in, single-process synthetic executor. No tools, model providers or external jobs are dispatched.
sealed class ChatCommitments : BackgroundService
{
    public sealed record Work(string Id, int Version, string Goal, string Status, string Dispatch, string[] Messages, string? Result = null);
    public sealed record Anchor(string TaskId, int Version);
    public sealed class Conversation
    {
        public string Owner { get; set; } = "";
        public long Cursor { get; set; }
        public WorkCommitmentState Ledger { get; set; } = WorkCommitmentState.Empty;
        public Dictionary<string, Anchor> Anchors { get; set; } = [];
    }
    readonly ChatStore store;
    readonly IHubContext<ChatHub> hub;
    readonly string file;
    readonly HashSet<string> operators;
    readonly SemaphoreSlim gate = new(1);
    readonly Dictionary<string, CancellationTokenSource> executions = [];
    readonly HashSet<string> legacyRooms = [];
    Dictionary<string, Conversation> rooms = [];
    Account robot = null!;
    Account? legacyRobot;
    FileStream? lease;
    readonly int delay;
    readonly bool realBotEnabled;
    const int MaximumBytes = 4 * 1024 * 1024;
    public ChatCommitments(ChatStore store, IHubContext<ChatHub> hub, IConfiguration config)
    {
        this.store = store; this.hub = hub;
        file = Path.GetFullPath(config["ChatCommitments:State"] ?? throw new InvalidOperationException("Set ChatCommitments:State outside the checkout."));
        operators = (config.GetSection("ChatCommitments:Operators").Get<string[]>() ?? []).ToHashSet(StringComparer.Ordinal);
        if (operators.Count == 0) throw new InvalidOperationException("Set ChatCommitments:Operators to authenticated account IDs.");
        delay = Math.Clamp(config.GetValue("ChatCommitments:SyntheticDelayMs", 1000), 10, 60000);
        realBotEnabled = config["ChatBot:Policy"] is not null;
    }
    public override async Task StartAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > MaximumBytes) throw new InvalidOperationException("Commitment state exceeds capacity.");
            rooms = JsonSerializer.Deserialize<Dictionary<string, Conversation>>(await File.ReadAllTextAsync(file, token)) ?? [];
            if (rooms.Count > 32 || rooms.Values.Any(x => x.Ledger.Tasks.Length > 64 || x.Ledger.Events.Length > WorkCommitmentLedger.MaximumEvents || x.Anchors.Count > 2000))
                throw new InvalidOperationException("Commitment state exceeds capacity.");
            // This restart rule is valid ONLY for the in-process, side-effect-free synthetic executor.
            foreach (var room in rooms.Values)
                room.Ledger = room.Ledger with { Tasks = room.Ledger.Tasks.Select(t => t.ExecutionId is not null || t.Status == "queued"
                    ? t with { Status = t.Status is "cancelled" or "replaced" ? t.Status : "paused", Version = t.Version + 1, ExecutionId = null, ExecutionVersion = null } : t).ToArray() };
        }
        robot = await store.GetRobot(ChatStore.CommitmentBotName);
        foreach (var room in rooms.Keys)
            if (!await store.RobotIsMember(room, robot.Id)) legacyRooms.Add(room);
        if (realBotEnabled && legacyRooms.Count > 0)
            throw new InvalidOperationException("Legacy synthetic work rooms share the real robot identity. Keep the old ledger for synthetic-only runs or configure a separate state file to enroll new synthetic rooms.");
        if (legacyRooms.Count > 0) legacyRobot = await store.GetRobot(ChatStore.BotName);
        await Save();
        await base.StartAsync(token);
    }
    async Task Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rooms);
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Commitment state capacity reached; archive offline before continuing.");
        var temp = file + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, file, true);
    }
    public bool Allowed(string owner) => operators.Contains(owner);
    public async Task<string?> Enroll(Account user)
    {
        if (!Allowed(user.Id)) return null;
        await gate.WaitAsync();
        try
        {
            var existing = rooms.FirstOrDefault(r => r.Value.Owner == user.Id);
            if (existing.Key is not null) return existing.Key;
            if (rooms.Count >= 32) return null;
            var room = await store.CreateRoom(user, new("direct", "", [robot.Name]));
            var history = await store.History(user.Id, room.Id, null, null);
            rooms[room.Id] = new() { Owner = user.Id, Cursor = history?.LastOrDefault()?.Sequence ?? 0 };
            try { await Save(); } catch { rooms.Remove(room.Id); throw; }
            return room.Id;
        }
        finally { gate.Release(); }
    }
    public async Task<Work[]?> Read(string owner, string room)
    {
        await gate.WaitAsync();
        try { return Allowed(owner) && rooms.TryGetValue(room, out var state) && state.Owner == owner ? state.Ledger.Tasks.Select(t => new Work(t.Id, t.Version, t.Goal, t.Status, t.ExecutionId ?? "", t.MessageIds)).ToArray() : null; }
        finally { gate.Release(); }
    }
    public async Task Handle(string owner, ChatMessage message)
    {
        if (!Allowed(owner)) return;
        await gate.WaitAsync();
        try
        {
            if (!rooms.TryGetValue(message.RoomId, out var room) || room.Owner != owner) return;
            await Drain(message.RoomId, room);
        }
        finally { gate.Release(); }
    }
    async Task Drain(string roomId, Conversation room)
    {
        // Ascending persisted history, not HTTP arrival order. Never skip a lower sequence on concurrent sends.
        // Bounded batch per pass; the background loop continues draining before any dispatch.
        var sources = await store.History(room.Owner, roomId, room.Cursor, null);
        if (sources is null) return;
        foreach (var source in sources)
        {
            string? response = null;
            string? target = null;
            var before = room.Ledger;
            var cursor = room.Cursor;
            if (!source.Deleted && !source.Edited && source.ForwardedFrom is null &&
                await store.IsCommitmentAuthor(roomId, source.Sequence, room.Owner))
            {
                var reply = source.ReplyTo?.ToString();
                var anchor = reply is not null ? room.Anchors.GetValueOrDefault(reply) : null;
                var input = new WorkUtterance(source.Sequence.ToString(), source.Text, anchor is null ? reply : null, anchor?.TaskId, anchor?.Version);
                var interpretation = WorkConversationPolicy.Interpret(input, WorkCommitmentLedger.References(before));
                if (interpretation.Intent != WorkIntent.None)
                {
                    try
                    {
                        var transition = WorkCommitmentLedger.Apply(before, source.Sequence.ToString(), interpretation, Guid.NewGuid().ToString());
                        room.Ledger = transition.State; target = transition.TaskId;
                        response = interpretation.Intent == WorkIntent.Clarify ? "请明确目标任务或当前要求。" + transition.Detail
                            : $"模拟任务 {target}：{transition.Detail} 仅测试任务流，不调用真实工具。";
                    }
                    catch (InvalidOperationException) { response = "任务或记录容量已满；原任务未改变，请先离线归档。"; }
                }
            }
            room.Cursor = source.Sequence;
            try { await Save(); } catch { room.Ledger = before; room.Cursor = cursor; throw; }
            // Signal only after the revised state is durable. Cancellation is a request, not proof of stop.
            foreach (var old in before.Tasks.Where(t => t.ExecutionId is not null))
            {
                var current = room.Ledger.Tasks.First(t => t.Id == old.Id);
                if (current.Version != old.Version && executions.TryGetValue(old.ExecutionId!, out var cancellation)) cancellation.Cancel();
            }
            if (response is not null)
            {
                var sequence = await Say(roomId, response, source.Sequence);
                if (target is not null && room.Ledger.Tasks.FirstOrDefault(t => t.Id == target) is { } task && room.Anchors.Count < 1998)
                {
                    room.Anchors[source.Sequence.ToString()] = new(task.Id, task.Version);
                    if (sequence is { } sent) room.Anchors[sent.ToString()] = new(task.Id, task.Version);
                    await Save();
                }
            }
        }
    }
    async Task<long?> Say(string room, string text, long? reply = null)
    {
        var sender = legacyRooms.Contains(room) ? legacyRobot! : robot;
        var sent = await store.Send(sender.Id, sender.Name, room, new(Guid.NewGuid().ToString(), text, reply));
        if (sent is not null) await hub.Clients.Users(sent.Recipients).SendAsync("roomChanged", room);
        return sent?.Message.Sequence;
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await gate.WaitAsync(token);
            var pending = new List<(string Room, WorkCommitment Work, CancellationTokenSource Stop)>();
            try
            {
                foreach (var (room, state) in rooms)
                {
                    if (!Allowed(state.Owner)) continue;
                    await Drain(room, state);
                    // If a batch remains, process that next pass before authorizing any dispatch.
                    if ((await store.History(state.Owner, room, state.Cursor, null))?.Length > 0) continue;
                    if (state.Ledger.Tasks.Any(t => t.ExecutionId is not null)) continue;
                    var task = state.Ledger.Tasks.FirstOrDefault(w => w.Status == "queued");
                    if (task is null) continue;
                    var before = state.Ledger;
                    var execution = Guid.NewGuid().ToString();
                    state.Ledger = WorkCommitmentLedger.Dispatch(before, task.Id, task.Version, execution);
                    try { await Save(); } catch { state.Ledger = before; throw; }
                    var work = state.Ledger.Tasks.Single(t => t.Id == task.Id);
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    executions[execution] = stop; pending.Add((room, work, stop));
                }
            }
            finally { gate.Release(); }
            await Task.WhenAll(pending.Select(x => Run(x.Room, x.Work, x.Stop, token)));
            await Task.Delay(250, token);
        }
    }
    async Task Run(string room, WorkCommitment work, CancellationTokenSource stop, CancellationToken shutdown)
    {
        bool cancelled = false;
        try { await Task.Delay(delay, stop.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            executions.Remove(work.ExecutionId!); stop.Dispose();
            var state = rooms[room];
            var before = state.Ledger;
            state.Ledger = WorkCommitmentLedger.Receive(before, work.Id, work.Version, work.ExecutionId!, cancelled ? "stopped" : "completed");
            try { await Save(); } catch { state.Ledger = before; throw; }
            var receipt = state.Ledger.Receipts.Last();
            if (!shutdown.IsCancellationRequested && !receipt.Late)
                await Say(room, $"模拟任务 {work.Id} v{work.Version}：{receipt.Outcome}。未调用外部工具。");
        }
        finally { gate.Release(); }
    }
    public override void Dispose() { lease?.Dispose(); base.Dispose(); }
    public static void Map(RouteGroupBuilder secured, ChatCommitments? host)
    {
        if (host is null) return;
        secured.MapPost("/commitments/direct", async (ClaimsPrincipal user) =>
        {
            var room = await host.Enroll(new(user.FindFirstValue(ClaimTypes.NameIdentifier)!, user.Identity!.Name!, ""));
            return room is null ? Results.StatusCode(403) : Results.Ok(new { id = room, executor = "synthetic" });
        });
        secured.MapGet("/commitments/rooms/{room:guid}", async (Guid room, ClaimsPrincipal user) =>
        {
            var works = await host.Read(user.FindFirstValue(ClaimTypes.NameIdentifier)!, room.ToString());
            return works is null ? Results.NotFound() : Results.Ok(works);
        });
    }
}
