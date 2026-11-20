using System.Collections.Concurrent;
using System.Threading.Channels;
using LittleLoop.Core;
using LittleLoop.Tools;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorkerChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task Run(Action<bool, string> check)
    {
        await BotRecoveryChecks.Run(check);
        using var fixture = new TemporaryChatDatabase();
        ChatStore Open() => new(fixture.Connection, false, "Sqlite");
        var store = Open();
        await store.Initialize();
        await store.InitializeChannelAdministration();
        await store.InitializeGroupModeration();
        await store.InitializeChannelDiscussions();
        var owner = new Account(Guid.NewGuid().ToString(), "decision_owner", "!fixture");
        await store.Register(owner);
        var robot = await store.GetRobot(ChatStore.BotName);
        var room = await store.CreateRoom(owner, new("direct", null, [robot.Name]));
        await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);

        string root = Directory.CreateTempSubdirectory("littleloop-decisions-").FullName;
        string project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var grant = new DevelopmentPolicy(project, ["code.txt"],
            [new("verify", Environment.ProcessPath!, ["--verify-fixture"], true, ["verify.txt"])], [".txt"]);
        File.WriteAllText(Path.Combine(project, "code.txt"), "fixture");
        File.WriteAllText(Path.Combine(project, "verify.txt"), "fixture");
        string policy = Path.Combine(root, "policy.json");
        var originalPolicy = new AgentHostPolicy("unused", project, Development: grant);
        File.WriteAllText(policy, ChatStore.ToJson(originalPolicy));
        string savedFolder = Path.Combine(root, "previous-task");
        var savedHost = new AgentDelegationHost(savedFolder);
        savedHost.Create(originalPolicy);
        string stateFile = Path.Combine(savedFolder, "agent.json");
        string suspended = ChatStore.ToJson(savedHost.Read() with { Status = "suspended", ThreadId = "old-thread" });
        File.WriteAllText(stateFile, suspended);
        foreach (var replacement in new[] { grant with { WritableFiles = [] },
            grant with { Commands = [grant.Commands[0] with { Arguments = ["--new-verifier"] }] },
            grant with { Root = Directory.CreateDirectory(Path.Combine(root, "replacement-project")).FullName } })
        {
            var primary = new LittleLoopChatAdapter(originalPolicy with { Project = replacement.Root, Development = replacement },
                root, _ => Task.FromResult(true));
            var outcome = await primary.Run(new(Guid.NewGuid(), room.Id, owner.Id, 1, "task", "running", new("继续")),
                savedFolder, (_, _) => throw new Exception("Stale grant requested approval"), CancellationToken.None);
            check(outcome.Status == "needs-reconciliation" && outcome.Report!.Contains("开发授权已变更") &&
                File.ReadAllText(stateFile) == suspended && File.ReadAllText(Path.Combine(project, "code.txt")) == "fixture",
                "old task cannot start its agent or execute when the current development grant changes");
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChatBot:Policy"] = policy, ["ChatBot:Data"] = Path.Combine(root, "worker"),
            ["ChatBot:Operators"] = owner.Name, ["ChatBot:MaxHarnessTurns"] = "3"
        }).Build();

        var deepseek = new ControlledHarness("deepseek", project);
        var codex = new ControlledHarness("codex", project);
        var engine = new ControlledEngine();
        ChatBot Create() => new(store, new QuietHub(), config, NullLogger<ChatBot>.Instance,
            decisionEngine: new LocalBotCognition(engine), configuredHarnesses: [codex, deepseek]);
        var worker = Create();
        async Task Restart()
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            store = Open();
            engine = new ControlledEngine();
            worker = Create();
            await worker.StartAsync(CancellationToken.None);
        }
        async Task<ChatMessage> Say(string body, string? client = null)
        {
            var sent = await store.Send(owner.Id, owner.Name, room.Id,
                new(client ?? Guid.NewGuid().ToString(), body, null), goalRobot: robot.Id);
            return sent?.Message ?? throw new Exception("Could not enqueue user message.");
        }
        async Task<ChatMessage> Notice(string expected)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            while (true)
            {
                var history = await store.History(owner.Id, room.Id, null, null);
                if (history?.FirstOrDefault(m => m.Sender == robot.Name && m.Text.Contains(expected)) is { } match)
                    return match;
                await Task.Delay(25, deadline.Token);
            }
        }
        async Task UntilStatus(Guid id, string status)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            while ((await store.ReadBotWork(id, owner.Id))?.Status != status)
                await Task.Delay(25, deadline.Token);
        }

        await worker.StartAsync(CancellationToken.None);
        try
        {
            const string discussion = "你觉得现在这个项目的问题在哪里？";
            var user = await Say(discussion);
            var question = await engine.Next();
            check(question.Context.Trigger.Kind == BotEventKind.UserMessage && question.Context.Trigger.Text == discussion &&
                question.Context.Goals.Length == 0 && question.Context.Harnesses.Contains("codex"),
                "A: unprefixed question reaches the decision engine with available capabilities");
            question.Choose(new(BotAction.Reply, Message: "我建议先讨论记忆检索。"));
            var answer = await Notice("先讨论记忆检索");
            check(answer.ReplyTo == user.Sequence && answer.Card is null && codex.Calls.IsEmpty && deepseek.Calls.IsEmpty,
                "A: Bot's direct answer creates neither a goal nor a Harness turn");

            const string request = "这周想把记忆模块的检索速度提上去。";
            var start = await Say(request);
            var plan = await engine.Next();
            check(plan.Context.Trigger.Text == request && plan.Context.History.Any(item => item.Text.Contains("记忆检索")),
                "B: an unprefixed new goal sees the preceding discussion");
            plan.Choose(new(BotAction.Delegate, Message: "我先检查检索耗时，再决定如何修改。", Objective: request,
                Instruction: "先分析检索耗时，列出慢查询。", Harness: "deepseek"));
            var analysis = await deepseek.Next();
            var announcement = await Notice("先检查检索耗时");
            var linked = await store.FindMessage(room.Id, start.Sequence, owner.Id);
            Guid id = linked?.Card?.TaskId ?? announcement.Card?.TaskId ?? throw new Exception("Goal card is missing");
            check(announcement.ReplyTo == start.Sequence && analysis.Instruction.Contains("检索耗时") &&
                (await store.ReadBotWork(id, owner.Id))?.Goal?.Objective == request,
                "B: the plan and goal are available before the first turn returns");

            const string finding = "发现缓存键反复编码";
            analysis.Return(finding, new HarnessToolResult("read", "readFile", "completed", "读取缓存实现"));
            var review = await engine.Next();
            check(review.Context.Trigger.Kind == BotEventKind.HarnessReceipt && review.Context.Trigger.GoalId == id &&
                review.Context.Trigger.Text.Contains(finding) && review.Context.Goals.Single(g => g.Id == id).RemainingTurns == 2,
                "E: analysis returns to the Bot with its goal and remaining budget");
            review.Choose(new(BotAction.Delegate, id, Instruction: finding + "，修改并运行测试。", Harness: "codex"));
            var firstEdit = await codex.Next();
            check(firstEdit.Instruction.Contains(finding) && firstEdit.Directory != analysis.Directory,
                "E: the next Harness receives the Bot's instruction informed by the other Harness");
            firstEdit.Return("我修改了实现。", new HarnessToolResult("verify", "test", "failed", "memory_test: expected one allocation"));
            var evaluate = await engine.Next();
            check(evaluate.Context.Trigger.Kind == BotEventKind.HarnessReceipt &&
                evaluate.Context.History.Any(e => e.Kind == BotEventKind.ToolResult && e.Text.Contains("expected one allocation")) &&
                evaluate.Context.History.Any(e => e.Kind == BotEventKind.BotDecision) &&
                (await store.ReadBotWork(id, owner.Id))?.Status != "completed",
                "C: a returned turn and its failed test are distinct observations before goal completion");
            evaluate.Choose(new(BotAction.Delegate, id, Instruction: "修复 memory_test，重新验证 allocation。", Harness: "codex"));
            var repair = await codex.Next();
            check(repair.Directory == firstEdit.Directory && repair.Dispatch != firstEdit.Dispatch &&
                repair.Instruction.Contains("memory_test"), "C: the repair is a new dispatch in the original Codex session");
            repair.Return("TOOL-PRIVATE-RECEIPT", new HarnessToolResult("verify2", "test", "completed", "memory_test passed"));
            var final = await engine.Next();
            check(final.Context.Trigger.GoalId == id && final.Context.Goals.Single(g => g.Id == id).RemainingTurns == 0,
                "C: the last budgeted receipt still receives a Bot decision");
            final.Choose(new(BotAction.Complete, id, Message: "已修复重复编码，记忆模块测试通过。"));
            await Notice("记忆模块测试通过");
            await UntilStatus(id, "completed");
            var transcript = await store.History(owner.Id, room.Id, null, null);
            check(!transcript!.Any(m => m.Sender == robot.Name && m.Text.Contains("TOOL-PRIVATE-RECEIPT")),
                "C: only the Bot's chosen conclusion appears as the final report");
            await Say(request, start.ClientId);

            const string nextRequest = "接着整理缓存接口。";
            await Say(nextRequest);
            var other = await engine.Next();
            check(other.Context.Trigger.Text == nextRequest, "a repeated client message is consumed once");
            other.Choose(new(BotAction.Delegate, Message: "我先查看缓存入口。", Objective: nextRequest,
                Instruction: "检查缓存入口。", Harness: "codex"));
            var pending = await codex.Next();
            var notice = await Notice("先查看缓存入口");
            Guid otherId = notice.Card?.TaskId ?? throw new Exception("Goal plan lacks a card.");
            const string restriction = "数据库那部分暂时不要改。";
            await Say(restriction);
            var correction = await engine.Next();
            check(correction.Context.Trigger.Text == restriction && correction.Context.Goals.Any(g => g.Id == otherId),
                "D: a fresh message reaches cognition while the Harness turn is blocked");
            correction.Choose(new(BotAction.Revise, otherId, Message: "我会保留数据库实现。", Instruction: restriction));
            var replan = await engine.Next();
            check(replan.Context.Trigger.GoalId == otherId &&
                replan.Context.Goals.Single(g => g.Id == otherId).Goal.Requirements.Contains(restriction),
                "D: revised requirements are durable and reach the Bot before the next instruction");
            replan.Choose(new(BotAction.Delegate, otherId, Instruction: restriction + "只检查缓存接口。", Harness: "codex"));
            var constrained = await codex.Next();
            check(constrained.Directory == pending.Directory && constrained.Instruction.Contains(restriction),
                "D: the same goal continues in its original session with the updated constraint");
            constrained.Return("缓存接口已检查。");
            var wait = await engine.Next();
            wait.Choose(new(BotAction.Wait, otherId, Message: "请确认需要整理的接口清单。"));
            await Notice("确认需要整理的接口清单");

            await Restart();
            const string wake = "接口清单确认，只看读取接口。";
            await Say(wake);
            var afterRestart = await engine.Next();
            check(afterRestart.Context.Trigger.Text == wake && afterRestart.Context.Goals.Any(g => g.Id == otherId &&
                g.Goal.Requirements.Contains(restriction)) && afterRestart.Context.History.Any(x =>
                    x.Kind == BotEventKind.HarnessReceipt && x.Text.Contains("缓存接口已检查")),
                "F: a replacement local engine receives persisted state and receipts after worker and store recreation");
            afterRestart.Choose(new(BotAction.Complete, otherId, Message: "接口范围已确认。"));
            await UntilStatus(otherId, "completed");

            await Say("以后工作做完不用单独给我总结。");
            var preference = await engine.Next();
            preference.Choose(new(BotAction.SetFinalReport, Enabled: false, Message: "已保存通知偏好。"));
            await Notice("已保存通知偏好");
            await Restart();
            await Say("再核对一次变更。");
            var quiet = await engine.Next();
            check(!quiet.Context.AutoFinalReport, "the owner's automatic final-report preference survives restart");
            quiet.Choose(new(BotAction.Delegate, Message: "我先核对这次变更。", Objective: "核对变更",
                Instruction: "查看变更摘要。", Harness: "codex"));
            var verification = await codex.Next();
            var quietNotice = await Notice("先核对这次变更");
            verification.Return("变更摘要已读取。");
            var quietEnd = await engine.Next();
            quietEnd.Choose(new(BotAction.Complete, quietEnd.Context.Trigger.GoalId, Message: "SILENT-REPORT"));
            await UntilStatus(quietNotice.Card!.TaskId!.Value, "completed");
            await Say("这次进展如何？");
            var later = await engine.Next();
            later.Choose(new(BotAction.Reply, Message: "这次已经核对。"));
            await Notice("这次已经核对");
            check(!(await store.History(owner.Id, room.Id, null, null))!.Any(m => m.Text.Contains("SILENT-REPORT")),
                "completion remains durable when the user disables automatic final reports");

            const string originalPreference = "摘要用英文";
            const string correctedPreference = "摘要用中文";
            var save = await Say("以后给我的摘要先写英文。");
            (await engine.Next()).Choose(new(BotAction.Remember, Instruction: originalPreference, Message: "已保存摘要语言。"));
            await Notice("已保存摘要语言");
            await Restart();
            var change = await Say("上次的摘要语言改成中文吧。");
            var recalled = await engine.Next();
            check(recalled.Context.Memory.Any(m => m.EndsWith($"#{save.Sequence} {originalPreference}", StringComparison.Ordinal)),
                "a sourced private memory reaches the next decision after restart");
            recalled.Choose(new(BotAction.Remember, Instruction: correctedPreference, MemorySource: save.Sequence,
                Message: "已更新摘要语言。"));
            await Notice("已更新摘要语言");
            await Say("忘了刚才那条摘要语言偏好吧。");
            var forget = await engine.Next();
            check(forget.Context.Memory.Any(m => m.EndsWith($"#{change.Sequence} {correctedPreference}", StringComparison.Ordinal)) &&
                !forget.Context.Memory.Any(m => m.Contains(originalPreference)),
                "a correction replaces the original memory and carries the correcting message as its source");
            forget.Choose(new(BotAction.Forget, MemorySource: change.Sequence, Message: "已删除摘要语言偏好。"));
            await Notice("已删除摘要语言偏好");

            var lookup = await Say("查看上下文");
            var manage = await engine.Next();
            check(!manage.Context.Memory.Any(m => m.Contains(originalPreference) || m.Contains(correctedPreference)),
                "forget removes the corrected preference from the next decision context");
            manage.Choose(new(BotAction.ManageMemory));
            await Notice("还没有保存上下文");
            var replies = (await store.History(owner.Id, room.Id, null, null))!
                .Where(m => m.ReplyTo == lookup.Sequence && m.Sender == robot.Name).ToArray();
            check(replies is [{ Text: "这段私聊还没有保存上下文。" }],
                "the Bot answers a committed memory lookup once without invoking its fallback again");

            var verify = await Say("请复核这次修改。");
            (await engine.Next()).Choose(new(BotAction.Delegate, Message: "我会核对本轮修改。", Objective: "核对本轮修改",
                Instruction: "检查修改。", Harness: "codex"));
            var turn = await codex.Next();
            turn.Result.SetResult(new("uncertain", "session", "工具执行后连接断开", []));
            await Notice("请核对 codex");
            var card = (await store.FindMessage(room.Id, verify.Sequence, owner.Id))!.Card!;
            await UntilStatus(card.TaskId!.Value, "needs-reconciliation");

            var auditConnection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(fixture.Connection) { Pooling = false };
            await using var db = new Microsoft.Data.Sqlite.SqliteConnection(auditConnection.ConnectionString);
            await db.OpenAsync();
            await using var sources = db.CreateCommand();
            sources.CommandText = """
                SELECT json_extract(detail,'$.action'),json_extract(detail,'$.cognitionSource.engine')
                FROM chat_bot_events WHERE room_id=$room AND seq IN ($memory,$uncertain) AND kind='BotDecision'
                ORDER BY id
                """;
            sources.Parameters.AddWithValue("$room", room.Id);
            sources.Parameters.AddWithValue("$memory", lookup.Sequence);
            sources.Parameters.AddWithValue("$uncertain", verify.Sequence);
            var audit = new List<(string Action, string Source)>();
            await using var reader = await sources.ExecuteReaderAsync();
            while (await reader.ReadAsync()) audit.Add((reader.GetString(0), reader.GetString(1)));
            check(audit.SequenceEqual([("ManageMemory", "ControlledEngine"), ("Reply", "local/memory-result/1"),
                ("Delegate", "ControlledEngine"), ("Wait", "local/reconciliation/1")]),
                "SQLite preserves each decision action and its actual source across the two feedback loops");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Request(BotDecisionContext Context, TaskCompletionSource<BotDecision> Result)
    {
        public void Choose(BotDecision decision) => Result.SetResult(decision);
    }

    private sealed class ControlledEngine : IBotDecisionEngine
    {
        private readonly Channel<Request> requests = Channel.CreateUnbounded<Request>();
        public Task<Request> Next() => requests.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        public async Task<BotDecision> DecideAsync(BotDecisionContext context, CancellationToken token)
        {
            var result = new TaskCompletionSource<BotDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            await requests.Writer.WriteAsync(new(context, result), token);
            return await result.Task.WaitAsync(token);
        }
    }

    private sealed record Turn(string Directory, Guid Dispatch, string Instruction, TaskCompletionSource<HarnessReceipt> Result)
    {
        public void Return(string report, params HarnessToolResult[] tools) =>
            Result.TrySetResult(new("returned", Directory, report, tools));
        public void Interrupt() => Result.TrySetResult(new("interrupted", Directory, "取消旧版指令。", []));
    }

    private sealed class ControlledHarness(string name, string project) : IBotHarness
    {
        private readonly Channel<Turn> turns = Channel.CreateUnbounded<Turn>();
        public ConcurrentDictionary<Guid, Turn> Calls { get; } = new();
        public string Name => name;
        public string? ProjectRoot => project;
        public Task<Turn> Next() => turns.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        public HarnessRunState GetStatus(string sessionDirectory, Guid dispatchId) =>
            Calls.TryGetValue(dispatchId, out var turn)
                ? new(turn.Result.Task.IsCompletedSuccessfully ? turn.Result.Task.Result.Status : "running", sessionDirectory)
                : new("idle");
        public void Interrupt(string sessionDirectory, Guid dispatchId)
        {
            if (Calls.TryGetValue(dispatchId, out var turn)) turn.Interrupt();
        }
        public async Task<HarnessReceipt> SendAsync(string sessionDirectory, Guid dispatchId, string instruction,
            Func<CancellationToken, Task<bool>>? shouldInterrupt, CancellationToken token, string? workspace = null)
        {
            var turn = new Turn(sessionDirectory, dispatchId, instruction,
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!Calls.TryAdd(dispatchId, turn)) throw new Exception("Repeated dispatch");
            await turns.Writer.WriteAsync(turn, token);
            while (!turn.Result.Task.IsCompleted)
            {
                if (shouldInterrupt is not null && await shouldInterrupt(token)) turn.Interrupt();
                await Task.WhenAny(turn.Result.Task, Task.Delay(25, token));
                token.ThrowIfCancellationRequested();
            }
            return await turn.Result.Task;
        }
    }


    private sealed class QuietHub : IHubContext<ChatHub>
    {
        public IHubClients Clients { get; } = new QuietClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private sealed class QuietClients : IHubClients, IClientProxy
    {
        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Client(string connectionId) => this;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy Group(string groupName) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
        public IClientProxy User(string userId) => this;
        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
