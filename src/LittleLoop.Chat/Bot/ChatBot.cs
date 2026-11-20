using LittleLoop.Core;
using LittleLoop.Tools;
using Microsoft.AspNetCore.SignalR;

sealed class ChatBot : BackgroundService
{
    private readonly ChatStore store;
    private readonly IHubContext<ChatHub> hub;
    private readonly ILogger<ChatBot> log;
    private readonly IConfiguration config;
    private readonly ProjectFiles files;
    private readonly HttpSemanticModelClient? memorySemantics;
    private readonly IBotDecisionEngine engine;
    private readonly Dictionary<string, IBotHarness> harnesses;
    private readonly int maxTurns;
    private readonly HashSet<string> operators;
    private readonly HashSet<string> approvers;
    private readonly HashSet<string> accessAdmins;
    private readonly HashSet<string> operatorIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> approverIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> accessAdminIds = new(StringComparer.Ordinal);
    private readonly string directory;
    private readonly string? legacyDirectory;
    private readonly SourceReviewChatAdapter sourceReview = new();
    private readonly Dictionary<string, Account> robots = new(StringComparer.Ordinal);
    private readonly int maxConcurrentTasks;
    private readonly int maxConcurrentQuestions;
    public string[] Providers => [ChatStore.BotName, ChatStore.ReviewBotName];
    public Account Robot(string? provider) => robots[provider ?? ChatStore.BotName];
    public bool CanUse(string? provider) => provider is null or ChatStore.BotName or ChatStore.ReviewBotName;

    public ChatBot(ChatStore store, IHubContext<ChatHub> hub, IConfiguration config, ILogger<ChatBot> log,
        IBotDecisionEngine decisionEngine, IBotHarness[]? configuredHarnesses = null)
    {
        this.store = store;
        this.hub = hub;
        this.log = log;
        this.config = config;
        maxConcurrentTasks = ReadConcurrency(config, "MaxConcurrentTasks", 2);
        maxConcurrentQuestions = ReadConcurrency(config, "MaxConcurrentQuestions", 1);
        var policy = ChatStore.FromJson<AgentHostPolicy>(File.ReadAllText(config["ChatBot:Policy"]!));
        files = new ProjectFiles(policy.Development ?? new DevelopmentPolicy(policy.Project, [], [], [".cs", ".md", ".txt"]), requireTests: false);
        if (!ProjectFiles.Same(files.Root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(policy.Project)))) throw new InvalidOperationException("Development and chat bot projects must match.");
        directory = Path.GetFullPath(config["ChatBot:Data"] ?? throw new InvalidOperationException("Set ChatBot__Data"));
        legacyDirectory = config["ChatBot:LegacyData"] is { } legacy ? Path.GetFullPath(legacy) : null;
        var relative = Path.GetRelativePath(files.Root, directory);
        if (!Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Keep agent state outside project.");
        Directory.CreateDirectory(directory);
        var localModel = ChatProcedureRecall.ReadConfig(config["ChatBot:MemorySemantics"]);
        memorySemantics = ChatProcedureRecall.CreateClient(localModel);
        maxTurns = config.GetValue("ChatBot:MaxHarnessTurns", 8);
        if (maxTurns is < 1 or > 128) throw new ArgumentException("ChatBot:MaxHarnessTurns must be between 1 and 128.");
        engine = decisionEngine;
        var native = new List<IBotHarness> { new CodexBotHarness(policy.Executable, policy.Project, TimeSpan.FromSeconds(policy.TimeoutSeconds)) };
        if ((config["ChatBot:DeepSeek:ReadOnlyLauncher"] ?? config["ChatBot:DeepSeek:Launcher"]) is { Length: > 0 } launcher)
        {
            var project = config["ChatBot:DeepSeek:Project"];
            if (!Path.IsPathFullyQualified(launcher) || !File.Exists(launcher) || project is null ||
                !Path.IsPathFullyQualified(project) || !Directory.Exists(project))
                throw new InvalidOperationException("DeepSeek requires an absolute ACP launcher and project directory.");
            var options = config.GetSection("ChatBot:DeepSeek");
            var arguments = options.GetSection("Arguments").Get<string[]>() ?? [];
            string? harnessHome = options["Home"];
            if (harnessHome is not null && !Path.IsPathFullyQualified(harnessHome))
                throw new InvalidOperationException("DeepSeek home must be absolute.");
            bool electron = options.GetValue<bool>("ElectronRunAsNode");
            var timeout = TimeSpan.FromSeconds(policy.TimeoutSeconds);
            native.Add(new DeepSeekBotHarness(new DeepSeekAcpClient(launcher, arguments, harnessHome, electron, project, timeout)
            {
                ConversationWorkspaceFiles = options.GetValue("ConversationWorkspaceFiles", true),
                UseLauncherPermissions = config["ChatBot:DeepSeek:ReadOnlyLauncher"] is null
            }));
        }
        harnesses = (configuredHarnesses ?? native.ToArray()).ToDictionary(h => h.Name, StringComparer.Ordinal);
        operators = Names(config["ChatBot:Operators"]);
        approvers = Names(config["ChatBot:Approvers"]);
        accessAdmins = Names(config["ChatBot:AccessAdmins"] ?? config["ChatBot:Approvers"]);
    }

    public override void Dispose()
    {
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
    public bool CanReadHistoricalEvidence(string? user) => user is not null && approverIds.Contains(user);
    public bool CanManageAccess(string user) => accessAdminIds.Contains(user);

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
        await store.RecoverContinuousGoals();
        await store.MigrateBotQueue(maxTurns);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(600));
        var active = new List<(BotWork Work, string? Root, Task Execution)>();
        string projectGrant = files.PolicyHash;
        foreach (var work in await store.InterruptedDispatches())
        {
            var dispatch = work.Input.Loop!.Dispatch!;
            var recovery = await RecoverDispatch(dispatch, Session(work, dispatch), token);
            if (recovery.Receipt is { } receipt) await store.SaveHarnessReceipt(work, receipt);
            else if (await store.GoalShouldYield(work))
                await store.SaveHarnessReceipt(work, new("interrupted", null, "本轮指令尚未发送，目标已变更。", []));
            else active.Add((work, Workspace(work), ExecuteWork(work)));
        }
        Task cognition = Task.CompletedTask;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (cognition.IsCompleted)
                {
                    await cognition;
                    cognition = ProcessDecisions();
                }
                for (int i = 0; i < 32 && await store.NextGoalNotice() is { } notice; i++)
                {
                    var robot = Robot(null);
                    try
                    {
                        var sent = await store.Send(robot.Id, robot.Name, notice.Room, new(notice.Id, notice.Text, notice.ReplyTo),
                            card: notice.TaskId is null ? null : new("task", TaskId: notice.TaskId), noticeOperator: CanOperate);
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
                        if (active.Any(item => item.Work.Id == candidate.Id)) return false;
                        if (candidate.IsAnswer ? !canAsk : !canRun) return false;
                        string? root = Workspace(candidate);
                        return root is null || !active.Any(item => item.Root is { } occupied && Overlaps(root, occupied));
                    }, name => harnesses.GetValueOrDefault(name)?.RecoveryKey);
                    if (next is null) break;
                    active.Add((next, Workspace(next), ExecuteWork(next)));
                }
            }
        }
        finally { await Task.WhenAll(active.Select(item => item.Execution).Append(cognition)); }

        async Task ProcessDecisions()
        {
            await DecideNext(goal: false);
            await DecideNext(goal: true);
        }

        string Session(BotWork work, BotDispatch dispatch) => Path.Combine(Folder(work), "harnesses", dispatch.Harness);

        string? Workspace(BotWork work)
        {
            if (work.Input.Loop?.Dispatch is { } dispatch)
                return PrivateWorkspace(work) ?? harnesses.GetValueOrDefault(dispatch.Harness)?.ProjectRoot;
            return null; // Independent source reviews use the frozen citation.
        }

        string? PrivateWorkspace(BotWork work)
        {
            if (CanOperate(work.Owner)) return null;
            var root = Path.Combine(directory, "private-conversations");
            var suffix = Path.Combine(Guid.Parse(work.Room).ToString("N"), Guid.Parse(work.Owner).ToString("N"), "workspace");
            var shared = Path.Combine(root, suffix);
            if (Directory.Exists(shared)) return shared;
            // Retain the account's existing files when upgrading a private conversation.
            bool deepSeekFirst = string.Equals(config["ChatBot:ConversationHarness"], "deepseek", StringComparison.OrdinalIgnoreCase);
            var previous = Path.Combine(root, deepSeekFirst ? ChatStore.DeepSeekBotName : ChatStore.BotName, suffix);
            var alternate = Path.Combine(root, deepSeekFirst ? ChatStore.BotName : ChatStore.DeepSeekBotName, suffix);
            return Directory.Exists(previous) ? previous : Directory.Exists(alternate) ? alternate : shared;
        }

        async Task DecideNext(bool goal)
        {
            var input = await store.NextBotInput(goal);
            if (input is null) return;
            bool operatorAccess = CanOperate(input.Owner);
            var harnessOptions = harnesses.Values.Select(h => h.Describe(!operatorAccess,
                !operatorAccess ? "private-workspace" :
                    ProjectFiles.Same(h.ProjectRoot ?? "", files.Root) ? "main-project" : "deployment-workspace")).ToArray();
            var available = harnessOptions.Where(option => option.CanQueue).ToArray();
            var snapshot = await store.DecisionContext(input, Robot(null).Id, operatorAccess, projectGrant,
                available.Select(option => option.Id).ToArray(), maxTurns, config.GetValue("ChatBot:AutoFinalReport", true),
                ChatConversation.Style(config["ChatBot:ConversationStyle"]));
            if (snapshot is null) return;
            var observed = snapshot;
            var capabilities = available.ToDictionary(option => option.Id, option => option.Description, StringComparer.Ordinal);
            var modelCalls = new List<BotModelCall>();
            snapshot = snapshot with { Context = snapshot.Context with
            {
                Capabilities = capabilities,
                HarnessOptions = harnessOptions,
                ObserveModelCall = modelCalls.Add,
                CheckFreshness = _ => store.DecisionFreshness(observed, Robot(null).Id, CanOperate(input.Owner))
            } };
            try
            {
                var result = await engine.DecideWithSourceAsync(snapshot.Context, token);
                var source = result.Source with { ModelCalls = modelCalls.ToArray() };
                if (result.Decision.Check(snapshot.Context) is { } invalid)
                {
                    await ReportDecisionFailure(invalid, allowDelayedRetry: false);
                    return;
                }
                var rejected = await store.ApplyDecision(snapshot, result.Decision, Robot(null).Id,
                    CanOperate(input.Owner), maxTurns, projectGrant, cognitionSource: source);
                if (rejected is not null) await store.RecordDecisionAudit(snapshot, source, rejected);
            }
            catch (BotDecisionSupersededException superseded)
            {
                await store.RecordDecisionAudit(snapshot, new("host/superseded") { ModelCalls = modelCalls.ToArray() }, superseded.Error);
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is not (System.Data.Common.DbException or OutOfMemoryException))
            {
                log.LogWarning(error, "Decision failed for room {Room}, message {Sequence}", input.Room, input.Sequence);
                var lastError = modelCalls.LastOrDefault()?.Error;
                await ReportDecisionFailure(lastError ?? new("decision-failed", "field", error.Message),
                    error is SemanticModelException provider && IsTemporaryCognitionFailure(provider.Code));
            }

            async Task ReportDecisionFailure(BotDecisionError error, bool allowDelayedRetry)
            {
                if (error.Category is "permission-denied" or "goal-version-conflict")
                {
                    await store.RecordDecisionAudit(snapshot, new("host/superseded") { ModelCalls = modelCalls.ToArray() }, error);
                    return;
                }
                string reason = "本次决策未能执行：" + error.Message;
                if (snapshot.Context.Goals.Any(g => g.Id == input.Trigger.GoalId && g.RemainingTurns == 0))
                    reason = $"目标的 Harness 调用预算已用尽。{reason}";
                if (reason.Length > 600) reason = reason[..600];
                bool retry = allowDelayedRetry && input.Trigger.Status != "decision-retry" &&
                    snapshot.Context.Goals.Any(g => g.Id == input.Trigger.GoalId &&
                        g.Goal.Status is "deciding" or "waiting-input" && g.RemainingWakeups > 0);
                if (retry) reason = "认知服务暂不可用，约 30 秒后重新评估。";
                var fallback = input.Trigger.GoalId is Guid id
                    ? new BotDecision(BotAction.Wait, id, reason, WakeAfterSeconds: retry ? 30 : null)
                    : new BotDecision(BotAction.Reply, Message: reason);
                var source = new BotDecisionSource(retry ? "host/decision-retry" : "host/decision-error")
                { ModelCalls = modelCalls.ToArray(), Error = error };
                var rejected = await store.ApplyDecision(snapshot, fallback, Robot(null).Id, CanOperate(input.Owner), maxTurns,
                    projectGrant, retry ? null : reason, source, retryCognition: retry);
                if (rejected is not null) await store.RecordDecisionAudit(snapshot, source, rejected);
            }
        }

        async Task ExecuteWork(BotWork work)
        {
            try
            {
                var provider = work.Input.Provider ?? ChatStore.BotName;
                var robot = Robot(provider);
                var room = await store.BotRoom(work.Room, work.Owner);
                if (!room.Installed || !room.Member || !await store.RobotIsMember(work.Room, robot.Id) ||
                    !await CanOperateRoom(work.Owner, work.Room, work.Input.Provider))
                {
                    if (work.Input.Loop?.Dispatch is not null)
                        await store.SaveHarnessReceipt(work, new("interrupted", null, "会话或账号已停止使用此 Harness。", []));
                    else await store.SetBotStatus(work, "disabled");
                    return;
                }
                if (work.Input.Loop?.Dispatch is { } dispatch)
                {
                    HarnessReceipt receipt;
                    bool sending = false;
                    try
                    {
                        if (!harnesses.TryGetValue(dispatch.Harness, out var harness))
                            throw new InvalidOperationException($"部署中找不到 Harness {dispatch.Harness}。");
                        string session = Session(work, dispatch);
                        var instruction = dispatch.Instruction + "\n\n当前目标及用户约束\n" + work.Goal!.Prompt;
                        if (CanOperate(work.Owner) && ProjectFiles.Same(harness.ProjectRoot ?? "", files.Root))
                        {
                            var recalled = await store.PrepareGoalMemory(work, files, memorySemantics, token);
                            if (recalled.LessonHints.Length > 0 || recalled.ExperienceHint is not null)
                                instruction += "\n来自用户的历史做法及已验证文件线索（当前要求优先）\n" +
                                    ChatStore.ToJson(new { recalled.LessonHints, recalled.ExperienceHint });
                        }
                        sending = true;
                        receipt = await harness.SendAsync(session, dispatch.Id, instruction,
                            async _ => await store.GoalShouldYield(work) || !await CanOperateRoom(work.Owner, work.Room),
                            token, PrivateWorkspace(work));
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && e is not OutOfMemoryException)
                    {
                        receipt = sending ? new("uncertain", null, "Harness 调用中断，执行结果待核对：" + e.Message, [])
                            : new("failed", null, e.Message, []);
                    }
                    await store.SaveHarnessReceipt(work, receipt);
                    return;
                }
                if (provider != ChatStore.ReviewBotName) throw new InvalidOperationException("任务没有可派发的 Bot 决策。");
                var report = await sourceReview.Answer(work.Input, token);
                var review = await store.Send(robot.Id, robot.Name, work.Room,
                    new(work.Id.ToString(), report, work.Sequence), card: new("citation", work.Input.Source));
                if (review is not null) await Notify(review.Recipients, work.Room);
                await store.SetBotStatus(work, "completed", output: report);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                log.LogError(e, "Chat bot work {Id} interrupted", work.Id);
                if (work.Input.Loop is not null) throw; // Restart collects the same durable dispatch.
                if (work.Kind == "question")
                {
                    await store.SetBotStatus(work, "failed", "源码问答未完成。");
                    return;
                }
                await store.SetBotStatus(work, "needs-reconciliation", "任务已停止，请核对执行记录。");
            }
        }

    }

    private static bool IsTemporaryCognitionFailure(string code) =>
        code is "timeout" or "transport-unavailable" or "http-429" or "http-500" or "http-502" or "http-503" or "http-504";

    private async Task<HarnessRecovery> RecoverDispatch(BotDispatch dispatch, string session, CancellationToken token)
    {
        harnesses.TryGetValue(dispatch.Harness, out var harness);
        if (harness is not null && dispatch.RecoveryKey == harness.RecoveryKey)
        {
            try { return await harness.RecoverAsync(session, dispatch.Id, token); }
            catch (Exception error) when (!token.IsCancellationRequested && error is not OutOfMemoryException)
            {
                log.LogWarning(error, "Unable to recover dispatch {Dispatch} from harness {Harness}", dispatch.Id, harness.Name);
                return HarnessRecovery.Unknown($"无法从 Harness {harness.Name} 核对本轮指令。");
            }
        }

        // Old claims had only a name. A positive journal entry identifies their original execution protocol.
        if (dispatch.RecoveryKey == NativeBotHarness.JournalKey || dispatch is { RecoveryKey: null, Harness: "codex" or "deepseek" })
        {
            var evidence = NativeBotHarness.ReadJournal(session, dispatch.Id);
            if (evidence == HarnessRecovery.NotSubmitted && harness?.RecoveryKey != NativeBotHarness.JournalKey)
                return HarnessRecovery.FromReceipt(new("interrupted", null, "原生指令尚未发送，原适配器已移除。", []));
            return evidence ?? HarnessRecovery.Unknown($"缺少 Harness {dispatch.Harness} 原生派发的恢复证据。");
        }
        return HarnessRecovery.Unknown($"Harness {dispatch.Harness} 的原恢复来源已不可用。");
    }

    public async Task<ChatMessage?> Queue(string room, string user, string name, Guid id, long? anchor, string kind, BotInput input)
    {
        if (input.Provider is null or ChatStore.BotName)
        {
            var sent = await store.Send(user, name, room, new(id.ToString(), input.Text, anchor),
                card: new(kind == "task" ? "request" : kind, input.Source), goalRobot: Robot(null).Id,
                legacyBotOperator: CanOperate(user), decisionRequest: true);
            if (sent is { IsNew: true }) await Notify(sent.Recipients, room);
            return sent?.Message;
        }
        if (input.Provider != ChatStore.ReviewBotName || input.Source is null)
            throw new ArgumentException("源码审阅需要一条带源码引用的消息。");
        var work = new BotWork(id, room, user, 0, kind, "queued", input);
        var result = await store.Send(user, name, room, new(id.ToString(), input.Text, anchor),
            card: new(kind, kind == "question" ? input.Source : null, id), job: work,
            goalRobot: Robot(null).Id, legacyBotOperator: CanOperate(user));
        if (result is not null && result.IsNew) await Notify(result.Recipients, room);
        return result?.Message;
    }

    public Task Notify(string[] recipients, string room) => hub.Clients.Users(recipients).SendAsync("roomChanged", room);
}
