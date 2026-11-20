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
    private readonly HttpSemanticModelClient? memorySemantics;
    private readonly HttpSemanticModelClient? conversationModel;
    private readonly HashSet<string> operators;
    private readonly HashSet<string> approvers;
    private readonly HashSet<string> accessAdmins;
    private readonly HashSet<string> operatorIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> approverIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> accessAdminIds = new(StringComparer.Ordinal);
    private readonly string directory;
    private readonly string? legacyDirectory;
    private readonly Dictionary<string, IChatRobotAdapter> adapters;
    private readonly IChatRobotAdapter conversation;
    private readonly bool nativeConversation;
    private readonly Dictionary<string, Account> robots = new(StringComparer.Ordinal);
    private readonly int maxConcurrentTasks;
    private readonly int maxConcurrentQuestions;
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
        maxConcurrentTasks = ReadConcurrency(config, "MaxConcurrentTasks", 2);
        maxConcurrentQuestions = ReadConcurrency(config, "MaxConcurrentQuestions", 1);
        var original = ChatStore.FromJson<AgentHostPolicy>(File.ReadAllText(config["ChatBot:Policy"]!));
        if (original.ApprovalMode is not ("auto" or "user" or "wait" or "decline"))
            throw new ArgumentException("Unknown approvalMode in ChatBot policy.");
        policy = original with { OutsideRule = "decline", Commands = [], AttachDocumentation = false, HostToolsOnly = true };
        files = new ProjectFiles(policy.Development ?? throw new InvalidOperationException("Bot requires a bounded development policy."));
        if (!ProjectFiles.Same(files.Root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(policy.Project)))) throw new InvalidOperationException("Development and chat bot projects must match.");
        directory = Path.GetFullPath(config["ChatBot:Data"] ?? throw new InvalidOperationException("Set ChatBot__Data"));
        legacyDirectory = config["ChatBot:LegacyData"] is { } legacy ? Path.GetFullPath(legacy) : null;
        var relative = Path.GetRelativePath(files.Root, directory);
        if (!Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Keep agent state outside project.");
        Directory.CreateDirectory(directory);
        var localModel = ChatProcedureRecall.ReadConfig(config["ChatBot:MemorySemantics"]);
        memorySemantics = ChatProcedureRecall.CreateClient(localModel);
        string? harness = config["ChatBot:ConversationHarness"]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(harness) && configuredAdapters is null &&
            config["ChatBot:ConversationModel"] is { Length: > 0 } modelPath)
        {
            // An explicitly configured text endpoint remains available separately from native harnesses.
            conversationModel = new(ChatStore.FromJson<SemanticModelConfig>(File.ReadAllText(modelPath)));
        }
        IChatRobotAdapter[] configured = configuredAdapters ?? [new LittleLoopChatAdapter(policy, directory, IsPrivateOperator, conversationModel), new SourceReviewChatAdapter()];
        IChatRobotAdapter? deepSeekConversation = configuredAdapters?.SingleOrDefault(adapter =>
            adapter.Name == ChatStore.DeepSeekBotName && !adapter.SupportsApproval);
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
            var readOnly = new DeepSeekChatAdapter(launcher, arguments, harnessHome, electron, project, timeout);
            configured = [.. configured, readOnly];
            if (harness == "deepseek")
                deepSeekConversation = new DeepSeekChatAdapter(launcher, arguments, harnessHome, electron, project, timeout)
                {
                    ConversationWorkspaceFiles = options.GetValue("ConversationWorkspaceFiles", true)
                };
            var reviewedPolicy = policy.ApprovalMode == "auto" ? policy with { ApprovalMode = "user" } : policy;
            if (options.GetValue<bool>("ReviewCodexChanges"))
                configured = [.. configured, new CollaborationChatAdapter(reviewedPolicy, new LittleLoopChatAdapter(reviewedPolicy, directory), readOnly)];
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
                var development = reviewedPolicy with { Project = scoped.Root, Development = scoped.Policy };
                configured = [.. configured, new DeepSeekChatAdapter(launcher, arguments, harnessHome, electron,
                    scoped.Root, timeout, development, provider)];
            }
        }
        adapters = configured.ToDictionary(adapter => adapter.Name, StringComparer.Ordinal);
        conversation = harness switch
        {
            null or "" or "codex" => adapters[ChatStore.BotName],
            "deepseek" => deepSeekConversation ??
                throw new InvalidOperationException("DeepSeek conversation requires the configured read-only ACP launcher."),
            _ => throw new ArgumentException("ChatBot:ConversationHarness must be codex or deepseek.")
        };
        nativeConversation = configuredAdapters is null && conversationModel is null;
        operators = Names(config["ChatBot:Operators"]);
        approvers = Names(config["ChatBot:Approvers"]);
        accessAdmins = Names(config["ChatBot:AccessAdmins"] ?? config["ChatBot:Approvers"]);
        if (operators.Count == 0 || approvers.Count == 0) throw new InvalidOperationException("Configure bot operators and approvers.");
    }

    public override void Dispose()
    {
        conversationModel?.Dispose();
        memorySemantics?.Dispose();
        base.Dispose();
    }

    private static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    private static int ReadConcurrency(IConfiguration config, string key, int fallback)
    {
        int value = config.GetValue("ChatBot:" + key, fallback);
        if (value is < 1 or > 16) throw new InvalidOperationException($"ChatBot:{key} must be between 1 and 16.");
        return value;
    }

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
    public bool CanManageAccess(string user) => accessAdminIds.Contains(user);

    internal async Task<bool> IsPrivateOperator(BotWork work) =>
        work.Input.Provider is (null or ChatStore.BotName) && CanOperate(work.Owner) &&
        await store.BotRoom(work.Room, work.Owner) is { Member: true, Installed: true, Type: "direct" };

    public async Task<bool> CanOperateRoom(string user, string room, string? provider = null)
    {
        if (provider is not (null or ChatStore.BotName)) return CanOperate(user);
        var access = await store.ReadBotFriendAccess(user, Robot(null).Id, room);
        return access == BotFriendAccessState.Active || access == BotFriendAccessState.Unmanaged && CanOperate(user);
    }

    public async Task NotifyFriendAccess(BotFriendAccessChange change)
    {
        await hub.Clients.Users(accessAdminIds.Append(change.UserId).Distinct().ToArray()).SendAsync("socialChanged", "");
        if (change.RoomId is not null) await Notify([change.UserId], change.RoomId);
    }
    public string[] Sources() => files.List();
    public SourceCitation Cite(string path, int start, int end) => AgentConversationSupport.Cite(files, path, start, end);
    public string Folder(BotWork work) => Path.Combine(work.Kind == "legacy" ? legacyDirectory ?? directory : directory, work.Id.ToString("N"));
    public bool HasLegacyEvidence => legacyDirectory is not null;
    public async Task<bool> IsPendingFcmApproval(FcmDelivery notice)
    {
        if (notice.Work is not { } id || notice.Nonce is not { } nonce || !CanApprove(notice.User)) return false;
        var work = await store.ReadBotWork(id, notice.User);
        return work is { Kind: "task", Status: "running" } && SupportsApproval(work.Input.Provider) &&
            await CanOperateRoom(work.Owner, work.Room, work.Input.Provider) &&
            AgentConversationSupport.IsApprovalPending(Folder(work), nonce);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var name in Providers) robots.Add(name, await store.GetRobot(name));
        foreach (var name in operators)
            if (await store.ResolveConfiguredAccount(name) is { } account && !ChatStore.IsRobot(account.Name)) operatorIds.Add(account.Id);
        foreach (var name in approvers)
            if (await store.ResolveConfiguredAccount(name) is { } account && !ChatStore.IsRobot(account.Name)) approverIds.Add(account.Id);
        foreach (var name in accessAdmins)
            if (await store.ResolveConfiguredAccount(name) is { } account && !ChatStore.IsRobot(account.Name)) accessAdminIds.Add(account.Id);
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
        }, work =>
        {
            if (work.Input.Provider != ChatStore.CollaborationBotName ||
                !adapters.TryGetValue(ChatStore.CollaborationBotName, out var adapter) || adapter is not CollaborationChatAdapter collaboration)
                return null;
            try { return collaboration.CanRecoverReview(work, Folder(work)); }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
            {
                log.LogWarning(error, "Could not verify collaboration review checkpoint for task {Task}", work.Id);
                return false;
            }
        });
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(600));
        var active = new List<(BotWork Work, string? Root, Task Execution)>();
        string projectGrant = files.PolicyHash;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                for (int i = 0; i < 32 && await store.ProcessGoalMessage(Robot(null).Id, CanOperate, projectGrant,
                    nativePrivateChat: nativeConversation); i++) { }
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
                foreach (var finished in active.Where(item => item.Execution.IsCompleted).ToArray())
                {
                    await finished.Execution;
                    active.Remove(finished);
                }

                while (!token.IsCancellationRequested)
                {
                    int questions = active.Count(item => item.Work.IsAnswer);
                    bool canAsk = questions < maxConcurrentQuestions;
                    bool canRun = active.Count - questions < maxConcurrentTasks;
                    if (!canAsk && !canRun) break;
                    var next = await store.ClaimBotWork(lease, candidate =>
                    {
                        if (candidate.IsAnswer ? !canAsk : !canRun) return false;
                        string? root = Workspace(candidate);
                        return root is null || !active.Any(item => item.Root is { } occupied && Overlaps(root, occupied));
                    });
                    if (next is null) break;
                    active.Add((next, Workspace(next), ExecuteWork(next)));
                }
            }
        }
        finally { await Task.WhenAll(active.Select(item => item.Execution)); }

        string? Workspace(BotWork work)
        {
            if (work.Kind == "chat") return null; // Per-user private workspace; same-room chat jobs are ordered by the store.
            var adapter = adapters.GetValueOrDefault(work.Input.Provider ?? ChatStore.BotName);
            // The default question path uses a frozen citation; ACP answers have a working directory.
            return adapter?.ProjectRoot ?? (work.IsAnswer ? null : files.Root);
        }

        async Task ExecuteWork(BotWork work)
        {
            try
            {
                var provider = work.Input.Provider ?? ChatStore.BotName;
                var robot = Robot(provider);
                var adapter = adapters[provider];
                var room = await store.BotRoom(work.Room, work.Owner);
                if (!room.Installed || !room.Member || !await store.RobotIsMember(work.Room, robot.Id) ||
                    !await CanOperateRoom(work.Owner, work.Room, work.Input.Provider))
                {
                    await store.SetBotStatus(work, "disabled");
                    return;
                }
                if (work.Kind == "chat")
                {
                    long? revision = await store.PrivateConversationRevision(work);
                    if (revision is null)
                    {
                        await store.SetBotStatus(work, "disabled");
                        return;
                    }
                    // Use DB-assigned IDs only. Neither paths nor provider/thread IDs come from messages.
                    var privateRoom = Path.Combine(directory, "private-conversations", conversation.Name,
                        Guid.Parse(work.Room).ToString("N"), Guid.Parse(work.Owner).ToString("N"));
                    var input = work.Input with
                    {
                        History = await store.ConversationHistory(work, robot.Id),
                        ConversationStyle = ChatConversation.Style(config["ChatBot:ConversationStyle"]),
                        ConversationNote = await store.ReadConversationNote(work),
                        ConversationWorkId = work.Id,
                        ConversationWorkspace = Path.Combine(privateRoom, "workspace")
                    };
                    if (revision != await store.PrivateConversationRevision(work))
                    {
                        await store.SetBotStatus(work, "disabled");
                        return;
                    }
                    string answer;
                    string status = "completed";
                    try
                    {
                        string session = Path.Combine(privateRoom, "sessions", revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        answer = (nativeConversation, conversation) switch
                        {
                            (true, DeepSeekChatAdapter deepSeek) => await deepSeek.AnswerConversation(input, session, token),
                            (true, _) => await CodexPrivateConversation.Answer(input, policy.Executable, session, token),
                            _ => await conversation.Answer(input, token)
                        };
                    }
                    catch (Exception e) when (!token.IsCancellationRequested)
                    {
                        var failure = ChatConversation.ProviderFailure(e);
                        log.LogWarning("Conversation adapter {Provider}, work {Id}: {Reason} ({ErrorType})",
                            conversation.Name, work.Id, failure, e.GetType().Name);
                        answer = ChatConversation.FailureReply(failure);
                        status = failure == ConversationFailure.Interrupted ? "needs-reconciliation" : "failed";
                    }
                    if (!await CanOperateRoom(work.Owner, work.Room, work.Input.Provider))
                    {
                        await store.SetBotStatus(work, "disabled");
                        return;
                    }
                    var result = await store.Send(robot.Id, robot.Name, work.Room,
                        new(work.Id.ToString(), answer[..Math.Min(answer.Length, 12000)], work.Sequence),
                        answeredWork: work, answerStatus: status, answerRevision: revision);
                    if (result is null) await store.SetBotStatus(work, nativeConversation ? "needs-reconciliation" : "disabled");
                    else await Notify(result.Recipients, work.Room);
                    return;
                }
                if (work.Kind == "question")
                {
                    var source = work.Input.Source ?? throw new InvalidDataException("Question has no citation.");
                    var text = await adapter.Answer(work.Input, token);
                    if (!await CanOperateRoom(work.Owner, work.Room, work.Input.Provider) ||
                        (await store.ReadBotWork(work.Id, work.Owner))?.Status != "running")
                    {
                        await store.SetBotStatus(work, "disabled");
                        return;
                    }
                    var result = await store.Send(robot.Id, robot.Name, work.Room,
                        new(work.Id.ToString(), text[..Math.Min(text.Length, 12000)], work.Sequence),
                        card: new("citation", source));
                    if (result is null) throw new InvalidOperationException("Robot reply was not stored.");
                    await Notify(result.Recipients, work.Room);
                    await store.SetBotStatus(work, "completed");
                    return;
                }

                if (work.Input.Source is { } cited && !File.Exists(Path.Combine(Folder(work), "agent.json")) &&
                    Cite(cited.Path, cited.Start, cited.End).Sha256 != cited.Sha256)
                {
                    await store.SetBotStatus(work, "stale-source", "文件已变化，请重新引用后安排任务。");
                    return;
                }
                bool learns = adapter is LittleLoopChatAdapter && ChatStore.SupportsGoalExperience(work);
                if (learns && !File.Exists(Path.Combine(Folder(work), "agent.json")))
                    work = await store.PrepareGoalMemory(work, files, memorySemantics, token);
                var outcome = await adapter.Run(work, Folder(work), async (request, _) =>
                {
                    try { await store.EnqueueFcmApproval(work, request.Nonce, approverIds); }
                    catch (System.Data.Common.DbException e) { log.LogWarning(e, "Could not queue approval notification."); }
                }, token, async _ => !await CanOperateRoom(work.Owner, work.Room, work.Input.Provider) ||
                    work.Goal is not null && await store.GoalShouldYield(work));
                VerifiedGoalExperience? experience = null;
                if (learns && outcome.Status == "completed")
                {
                    try
                    {
                        var verifiedPolicy = new AgentDelegationHost(Folder(work)).Read().Policy.Development;
                        if (verifiedPolicy is not null)
                        {
                            var receipts = new LittleLoop.Core.LearningSession(Path.Combine(Folder(work), "tools-learning.json")).Read().Episodes;
                            experience = VerifiedGoalExperience.FromReceipts(new ProjectFiles(verifiedPolicy), receipts);
                        }
                    }
                    catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
                    {
                        log.LogWarning(error, "Could not derive experience for completed task {Task}", work.Id);
                    }
                }
                await store.SetBotStatus(work, outcome.Status == "suspended" ? "disabled" : outcome.Status,
                    output: outcome.Report, experience: experience);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                log.LogError(e, "Chat bot work {Id} interrupted", work.Id);
                if (work.Kind == "question")
                {
                    await store.SetBotStatus(work, "failed", "源码问答未完成。");
                    return;
                }
                await store.SetBotStatus(work, "needs-reconciliation", "任务已停止，请核对执行记录。");
            }
        }

    }

    public async Task<ChatMessage?> Queue(string room, string user, string name, Guid id, long? anchor, string kind, BotInput input)
    {
        var goal = kind == "task" && ((input.Provider is null or ChatStore.BotName or ChatStore.DeepSeekBotName or ChatStore.DeepSeekTestBotName) ||
            ChatStore.IsProjectTaskOnly(input.Provider))
            ? LittleLoop.Core.ContinuousGoal.Create(input.Text) : null;
        var work = new BotWork(id, room, user, 0, kind, goal?.Status ?? "queued", input, Goal: goal);
        var result = await store.Send(user, name, room, new(id.ToString(), input.Text, anchor),
            card: new(kind, kind == "question" ? input.Source : null, id), job: work,
            goalRobot: Robot(null).Id, legacyBotOperator: CanOperate(user));
        if (result is not null && result.IsNew) await Notify(result.Recipients, room);
        return result?.Message;
    }

    public Task Notify(string[] recipients, string room) => hub.Clients.Users(recipients).SendAsync("roomChanged", room);
}
