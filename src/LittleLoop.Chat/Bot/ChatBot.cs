using LittleLoop.Tools;
using Microsoft.AspNetCore.SignalR;

sealed class ChatBot : BackgroundService
{
    private readonly ChatStore store;
    private readonly IHubContext<ChatHub> hub;
    private readonly ILogger<ChatBot> log;
    private readonly IConfiguration config;
    private readonly AgentHostPolicy policy;
    private readonly ProjectFiles files;
    private readonly HashSet<string> operators;
    private readonly HashSet<string> approvers;
    private readonly HashSet<string> operatorIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> approverIds = new(StringComparer.Ordinal);
    private readonly string directory;
    private readonly string? legacyDirectory;
    private readonly Dictionary<string, IChatRobotAdapter> adapters;
    private readonly Dictionary<string, Account> robots = new(StringComparer.Ordinal);
    public string[] Providers => adapters.Keys.ToArray();
    public Account Robot(string? provider) => robots[provider ?? ChatStore.BotName];
    public bool CanUse(string? provider) => adapters.ContainsKey(provider ?? ChatStore.BotName);
    public bool SupportsApproval(string? provider) => CanUse(provider) && adapters[provider ?? ChatStore.BotName].SupportsApproval;

    public ChatBot(ChatStore store, IHubContext<ChatHub> hub, IConfiguration config, ILogger<ChatBot> log,
        IChatRobotAdapter[]? configuredAdapters = null)
    {
        this.store = store;
        this.hub = hub;
        this.log = log;
        this.config = config;
        var original = ChatStore.FromJson<AgentHostPolicy>(File.ReadAllText(config["ChatBot:Policy"]!));
        policy = original with { ApprovalMode = "user", OutsideRule = "decline", Commands = [], AttachDocumentation = false, HostToolsOnly = true };
        files = new ProjectFiles(policy.Development ?? throw new InvalidOperationException("Bot requires a bounded development policy."));
        if (!ProjectFiles.Same(files.Root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(policy.Project)))) throw new InvalidOperationException("Development and chat bot projects must match.");
        directory = Path.GetFullPath(config["ChatBot:Data"] ?? throw new InvalidOperationException("Set ChatBot__Data"));
        legacyDirectory = config["ChatBot:LegacyData"] is { } legacy ? Path.GetFullPath(legacy) : null;
        var relative = Path.GetRelativePath(files.Root, directory);
        if (!Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Keep agent state outside project.");
        Directory.CreateDirectory(directory);
        IChatRobotAdapter[] configured = configuredAdapters ?? [new LittleLoopChatAdapter(policy, directory), new SourceReviewChatAdapter()];
        if (configuredAdapters is null && config["ChatBot:DeepSeek:ReadOnlyLauncher"] is { Length: > 0 } launcher)
        {
            var project = config["ChatBot:DeepSeek:Project"];
            if (!Path.IsPathFullyQualified(launcher) || !File.Exists(launcher) || project is null ||
                !Path.IsPathFullyQualified(project) || !Directory.Exists(project))
                throw new InvalidOperationException("DeepSeek requires an absolute read-only ACP launcher and isolated project directory.");
            var options = config.GetSection("ChatBot:DeepSeek");
            var arguments = options.GetSection("Arguments").Get<string[]>() ?? [];
            string? harnessHome = options["Home"];
            if (harnessHome is not null && !Path.IsPathFullyQualified(harnessHome))
                throw new InvalidOperationException("DeepSeek home must be absolute.");
            bool electron = options.GetValue<bool>("ElectronRunAsNode");
            var timeout = TimeSpan.FromSeconds(policy.TimeoutSeconds);
            configured = [.. configured, new DeepSeekChatAdapter(launcher, arguments, harnessHome, electron, project, timeout)];
            var roots = new List<string>();
            if (options["TestDevelopmentPolicy"] is { Length: > 0 } grantPath)
                AddProject(ChatStore.DeepSeekTestBotName, grantPath, legacy: true);
            foreach (var entry in options.GetSection("DevelopmentProjects").GetChildren())
            {
                if (entry.Key.Length is < 1 or > 15 || entry.Key.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
                    entry["Policy"] is not { Length: > 0 } path)
                    throw new InvalidOperationException("DeepSeek project IDs require 1–15 lowercase letters, digits or underscores and a policy path.");
                AddProject(ChatStore.DeepSeekProjectPrefix + entry.Key, path, legacy: false);
            }

            void AddProject(string provider, string grantPath, bool legacy)
            {
                if (!Path.IsPathFullyQualified(grantPath)) throw new InvalidOperationException("DeepSeek policy path must be absolute.");
                var grant = ChatStore.FromJson<DevelopmentPolicy>(File.ReadAllText(grantPath));
                var scoped = new ProjectFiles(grant);
                if (!Path.IsPathFullyQualified(grant.Root) ||
                    Overlaps(scoped.Root, directory) || Overlaps(scoped.Root, project) ||
                    harnessHome is not null && Overlaps(scoped.Root, harnessHome) ||
                    legacyDirectory is not null && Overlaps(scoped.Root, legacyDirectory) ||
                    roots.Any(root => Overlaps(root, scoped.Root)) ||
                    Overlaps(scoped.Root, files.Root) && (legacy || !ProjectFiles.Same(scoped.Root, files.Root)) ||
                    scoped.Policy.Commands.Any(command => !command.IsTest))
                    throw new InvalidOperationException("DeepSeek grant requires a distinct, bounded project and fixed tests; host state must stay outside it.");
                roots.Add(scoped.Root);
                var development = policy with { Project = scoped.Root, Development = scoped.Policy };
                configured = [.. configured, new DeepSeekChatAdapter(launcher, arguments, harnessHome, electron,
                    scoped.Root, timeout, development, provider)];
            }
        }
        adapters = configured.ToDictionary(adapter => adapter.Name, StringComparer.Ordinal);
        operators = Names(config["ChatBot:Operators"]);
        approvers = Names(config["ChatBot:Approvers"]);
        if (operators.Count == 0 || approvers.Count == 0) throw new InvalidOperationException("Configure bot operators and approvers.");
    }

    private static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    private static bool Contains(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static HashSet<string> Names(string? names) => (names ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public bool CanOperate(string? user) => user is not null && operatorIds.Contains(user);
    public bool CanApprove(string? user) => user is not null && approverIds.Contains(user);
    public string[] Sources() => files.List();
    public SourceCitation Cite(string path, int start, int end) => AgentConversationSupport.Cite(files, path, start, end);
    public string Folder(BotWork work) => Path.Combine(work.Kind == "legacy" ? legacyDirectory ?? directory : directory, work.Id.ToString("N"));
    public bool HasLegacyEvidence => legacyDirectory is not null;
    public async Task<bool> IsPendingFcmApproval(FcmDelivery notice)
    {
        if (notice.Work is not { } id || notice.Nonce is not { } nonce || !CanApprove(notice.User)) return false;
        var work = await store.ReadBotWork(id, notice.User);
        return work is { Kind: "task", Status: "running" } && SupportsApproval(work.Input.Provider) &&
            AgentConversationSupport.IsApprovalPending(Folder(work), nonce);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var name in Providers) robots.Add(name, await store.GetRobot(name));
        foreach (var name in operators)
            if (await store.ResolveConfiguredAccount(name) is { } account && !ChatStore.IsRobot(account.Name)) operatorIds.Add(account.Id);
        foreach (var name in approvers)
            if (await store.ResolveConfiguredAccount(name) is { } account && !ChatStore.IsRobot(account.Name)) approverIds.Add(account.Id);
        await LegacyWorkbenchImport.Import(store, Robot(null), config);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await using var lease = await store.AcquireBotLease();
        await using var workspace = new FileStream(Path.Combine(directory, "chat-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await store.RecoverBotWork(lease);
        await store.RecoverContinuousGoals(work =>
        {
            if (!SupportsApproval(work.Input.Provider)) return null;
            try { return new AgentDelegationHost(Folder(work)).RecoverApprovalBoundary(); }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                log.LogWarning(error, "Could not verify approval boundary for task {Task}", work.Id);
                return false;
            }
        });
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(600));
        Task? active = null;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                for (int i = 0; i < 32 && await store.ProcessGoalMessage(Robot(null).Id, CanOperate); i++) { }
                for (int i = 0; i < 32 && await store.NextGoalNotice() is { } notice; i++)
                {
                    var robot = Robot(null);
                    try
                    {
                        var sent = await store.Send(robot.Id, robot.Name, notice.Room, new(notice.Id, notice.Text, notice.ReplyTo),
                            card: notice.TaskId is null ? null : new("task", TaskId: notice.TaskId));
                        if (sent is not null) await Notify(sent.Recipients, notice.Room);
                    }
                    catch (Exception e) when (e is DirectMessageBlockedException or GroupSilencedException or ChannelWriteDeniedException)
                    { log.LogInformation("Goal notice {Notice} suppressed by conversation permissions.", notice.Id); }
                    await store.CompleteGoalNotice(notice.Id);
                }
                if (active is { IsCompleted: false }) continue;
                if (active is not null) await active;
                var next = await store.ClaimBotWork(lease);
                active = next is null ? null : ExecuteWork(next);
            }
        }
        finally { if (active is not null) await active; }

        async Task ExecuteWork(BotWork work)
        {
            try
            {
                var provider = work.Input.Provider ?? ChatStore.BotName;
                var robot = Robot(provider);
                var adapter = adapters[provider];
                if (!(await store.BotRoom(work.Room, work.Owner)).Installed || !await store.RobotIsMember(work.Room, robot.Id))
                {
                    await store.SetBotStatus(lease, work, "disabled");
                    return;
                }
                if (work.Kind == "question")
                {
                    var source = work.Input.Source ?? throw new InvalidDataException("Question has no citation.");
                    var text = await adapter.Answer(work.Input, token);
                    var result = await store.Send(robot.Id, robot.Name, work.Room,
                        new(work.Id.ToString(), text[..Math.Min(text.Length, 12000)], work.Sequence),
                        card: new("citation", source));
                    if (result is null) throw new InvalidOperationException("Robot reply was not stored.");
                    await Notify(result.Recipients, work.Room);
                    await store.SetBotStatus(lease, work, "completed");
                    return;
                }

                if (work.Input.Source is { } cited && !File.Exists(Path.Combine(Folder(work), "agent.json")) &&
                    Cite(cited.Path, cited.Start, cited.End).Sha256 != cited.Sha256)
                {
                    await store.SetBotStatus(lease, work, "stale-source", "文件已变化，请重新引用后安排任务。");
                    return;
                }
                var outcome = await adapter.Run(work, Folder(work), async (request, _) =>
                {
                    try { await store.EnqueueFcmApproval(work, request.Nonce, approverIds); }
                    catch (MySqlConnector.MySqlException e) { log.LogWarning("Could not queue approval notification, MySQL error {Error}.", e.Number); }
                }, token, work.Goal is null ? null : _ => store.GoalShouldYield(work));
                await store.SetBotStatus(lease, work, outcome.Status == "suspended" ? "disabled" : outcome.Status, output: outcome.Report);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                log.LogError(e, "Chat bot work {Id} interrupted", work.Id);
                if (work.Kind == "question")
                {
                    await store.SetBotStatus(lease, work, "failed", "源码问答未完成。");
                    return;
                }
                await store.SetBotStatus(lease, work, "needs-reconciliation", "任务已停止，请核对执行记录。");
            }
        }
    }

    public async Task<ChatMessage?> Queue(string room, string user, string name, Guid id, long? anchor, string kind, BotInput input)
    {
        var goal = kind == "task" && ((input.Provider is null or ChatStore.BotName or ChatStore.DeepSeekBotName or ChatStore.DeepSeekTestBotName) ||
            ChatStore.IsDeepSeekProject(input.Provider))
            ? LittleLoop.Core.ContinuousGoal.Create(input.Text) : null;
        var work = new BotWork(id, room, user, 0, kind, goal?.Status ?? "queued", input, Goal: goal);
        var result = await store.Send(user, name, room, new(id.ToString(), input.Text, anchor),
            card: new(kind, kind == "question" ? input.Source : null, id), job: work);
        if (result is not null && result.IsNew) await Notify(result.Recipients, room);
        return result?.Message;
    }

    public Task Notify(string[] recipients, string room) => hub.Clients.Users(recipients).SendAsync("roomChanged", room);
}
