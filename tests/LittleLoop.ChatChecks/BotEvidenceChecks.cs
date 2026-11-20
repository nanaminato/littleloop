using LittleLoop.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class WorkerChecks
{
    private static async Task CheckBotEvidence(IConfiguration config, string project, Account owner, Action<bool, string> check)
    {
        using var fixture = new TemporaryChatDatabase();
        var clock = new TestClock();
        var store = new ChatStore(fixture.Connection, false, "Sqlite", clock);
        await store.Initialize();
        await store.InitializeChannelAdministration();
        await store.InitializeGroupModeration();
        await store.InitializeChannelDiscussions();
        await store.Register(owner);
        var robot = await store.GetRobot(ChatStore.BotName);
        var room = await store.CreateRoom(owner, new("direct", null, [robot.Name]));
        await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);
        var engine = new ControlledEngine();
        var harness = new ControlledHarness("inspect", project);
        var unavailable = new CodexBotHarness(Path.Combine(project, "absent-harness.exe"), project, Timeout);
        using var worker = new ChatBot(store, new QuietHub(), config, NullLogger<ChatBot>.Instance, engine, [harness, unavailable]);
        await worker.StartAsync(default);
        try
        {
            long firstMessage = await Say("独立核对工作区的新合成材料");
            var request = await engine.Next();
            check(request.Context.Harnesses.SequenceEqual(["inspect"]) &&
                request.Context.HarnessOptions.Single(o => o.Id == "codex") is
                    { CanQueue: false, Availability: "unavailable", Permission: "deployment-configured" },
                "the model sees adapted capability and permission facts while unavailable IDs are excluded from legal dispatch");
            var readOnly = new DeepSeekBotHarness(new DeepSeekAcpClient(Environment.ProcessPath!, [], null, false, project, Timeout)
            {
                ConversationWorkspaceFiles = true
            });
            check(readOnly.Describe(false, "deployment-workspace").Permission == "read-only" &&
                readOnly.Describe(false, "deployment-workspace").Capabilities.Contains("workspace-read") &&
                readOnly.Describe(true, "private-workspace").Capabilities.Contains("workspace-text-write"),
                "ACP capability declarations follow the actual project and private workspace permission profiles");
            const string instruction = "核对输入材料和输出记录一致。";
            request.Choose(new(BotAction.Delegate, Message: "我先查看材料", Objective: "核对一致性",
                Harness: "inspect", Instruction: instruction));
            var first = await harness.Next();
            first.Return("原始工具结论", [new("early-failure", "read", "failed", "early independent failure"),
                .. Enumerable.Range(1, 40).Select(i => new HarnessToolResult($"sample-{i}", "read", "completed", $"data set {i}"))]);
            var returned = await engine.Next();
            var evidence = returned.Context.DispatchEvidence.Single();
            var observed = evidence.Dispatches.Single();
            check(observed is { Execution: "returned", EvidenceState: "observed", Verification: null, RecordedToolCount: 41,
                    ToolCoverageIncomplete: true } &&
                observed.DispatchId == first.Dispatch && observed.GoalVersion == returned.Context.Trigger.GoalVersion &&
                observed.Receipt is { Source: BotEventKind.HarnessReceipt, EventId: > 0 } &&
                observed.ToolEvidence.Any(tool => tool is { ToolId: "early-failure", EventId: > 0, Source: BotEventKind.ToolResult }) &&
                returned.Context.History.All(o => o.ToolId != "early-failure") && !evidence.GoalCompleted,
                "the projection keeps a linked receipt and failure even when raw history is full, without inferring verification or completion");
            Guid goalId = evidence.GoalId;
            check(Invalid(new(BotAction.Wait, goalId, VerifyDispatchId: Guid.NewGuid()), returned.Context),
                "Bot verification cannot cite an unobserved dispatch");
            var repeat = new BotDecision(BotAction.Delegate, goalId, Harness: "inspect", Instruction: instruction);
            check(Invalid(repeat with { VerifyDispatchId = first.Dispatch }, returned.Context),
                "checking and repeating a dispatch in the same decision still requires a review reason");
            returned.Choose(new(BotAction.Wait, goalId, WakeAfterSeconds: 5, VerifyDispatchId: first.Dispatch));
            using (var deadline = new CancellationTokenSource(Timeout))
                while ((await store.ReadBotWork(goalId, owner.Id))?.Input.Loop?.Wake is null)
                    await Task.Delay(25, deadline.Token);
            clock.Advance(5000);
            var timer = await engine.Next();
            var preserved = timer.Context.DispatchEvidence.Single();
            check(!preserved.GoalCompleted && preserved.Dispatches.Single().Verification is { DecisionEventId: > 0 } verification &&
                verification.GoalVersion == preserved.GoalVersion,
                "a persisted Bot assessment cites its own versioned event and does not complete the goal");
            check(Invalid(repeat, timer.Context) && Invalid(repeat with { ReviewDispatchId = first.Dispatch }, timer.Context) &&
                !Invalid(repeat with { Instruction = "抽取一个区别于原核对的独立样本。" }, timer.Context),
                "only a literal repeat of a checked delegation must cite its dispatch and give a review reason");
            const string reason = "用户需要对相同结果复核不同的输入版本。";
            timer.Choose(repeat with { ReviewDispatchId = first.Dispatch, ReviewReason = reason });
            var review = await harness.Next();
            await Say("当前进度怎样？", firstMessage);
            var progress = await engine.Next();
            var queued = progress.Context.DispatchEvidence.Single().Dispatches.Single(d => d.DispatchId == review.Dispatch);
            check(queued is { Execution: "unknown", EvidenceState: "unknown", Verification: null } &&
                queued.ReviewDispatchId == first.Dispatch && queued.ReviewReason == reason &&
                queued.Request is { Source: BotEventKind.BotDecision, EventId: > 0 },
                "a later decision reads the explicit review rationale, while the new dispatch is unknown until there is a receipt");
            progress.Choose(new(BotAction.Reply, goalId, Message: "正在核对"));
            using (var deadline = new CancellationTokenSource(Timeout))
                while (await store.NextBotInput(goal: false) is not null)
                    await Task.Delay(25, deadline.Token);
            review.Return("新输入版本的证据", new HarnessToolResult("review", "read", "completed", "sample present"));
            (await engine.Next()).Choose(new(BotAction.Complete, goalId, VerifyDispatchId: review.Dispatch));
            using (var deadline = new CancellationTokenSource(Timeout))
                while ((await store.ReadBotWork(goalId, owner.Id))?.Status != "completed")
                    await Task.Delay(25, deadline.Token);
            await Say("这个目标是否完成？", firstMessage);
            var final = await engine.Next();
            check(final.Context.DispatchEvidence.Single().GoalCompleted,
                "goal completion comes from the actual committed status");
            final.Choose(new(BotAction.Reply, goalId, Message: "目标状态为完成"));
        }
        finally { await worker.StopAsync(default); }

        async Task<long> Say(string text, long? reply = null)
        {
            var result = await store.Send(owner.Id, owner.Name, room.Id, new(Guid.NewGuid().ToString(), text, reply), goalRobot: robot.Id);
            return result!.Message.Sequence;
        }
    }

    private static bool Invalid(BotDecision decision, BotDecisionContext context)
    {
        try { decision.Validate(context); return false; }
        catch (InvalidDataException) { return true; }
    }
}
