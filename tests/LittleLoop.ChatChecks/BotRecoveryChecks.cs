using LittleLoop.Core;

internal static class BotRecoveryChecks
{
    private enum Scenario { Revised, RevisedUnknown, Current, PauseResumeCancel }

    public static async Task Run(Action<bool, string> check)
    {
        foreach (var scenario in Enum.GetValues<Scenario>())
            await CheckCrash(scenario, check);
    }

    private static async Task CheckCrash(Scenario scenario, Action<bool, string> check)
    {
        bool revise = scenario is Scenario.Revised or Scenario.RevisedUnknown;
        bool lostReceipt = scenario == Scenario.RevisedUnknown;
        using var fixture = new TemporaryChatDatabase();
        ChatStore Open() => new(fixture.Connection, false, "Sqlite");
        var store = Open();
        await store.Initialize();
        await store.InitializeChannelAdministration();
        await store.InitializeGroupModeration();
        await store.InitializeChannelDiscussions();
        var owner = new Account(Guid.NewGuid().ToString(), "recovery_owner", "!fixture");
        await store.Register(owner);
        var robot = await store.GetRobot(ChatStore.BotName);
        var room = await store.CreateRoom(owner, new("direct", null, [robot.Name]));
        await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);
        const string original = "检查召回索引";
        const string constraint = "保留数据库文件";
        async Task ActOnMessage(string body, BotDecision decision)
        {
            await store.Send(owner.Id, owner.Name, room.Id, new(Guid.NewGuid().ToString(), body, null), goalRobot: robot.Id);
            var input = await store.NextBotInput(goal: false) ?? throw new Exception("Missing user message in inbox.");
            var snapshot = await store.DecisionContext(input, robot.Id, true, "fixture", ["codex"], 2, true, null)
                ?? throw new Exception("Missing decision snapshot.");
            await store.ApplyDecision(snapshot, decision.Validate(snapshot.Context), robot.Id, true, 2, "fixture");
            while (await store.NextGoalNotice() is { } plan)
            {
                await store.Send(robot.Id, robot.Name, room.Id, new(plan.Id, plan.Text, plan.ReplyTo),
                    card: new("task", TaskId: plan.TaskId));
                await store.CompleteGoalNotice(plan.Id);
            }
        }

        async Task CheckControls(BotWork first)
        {
            await ActOnMessage("先停一下", new(BotAction.Pause, first.Id));
            var paused = await store.ReadBotWork(first.Id, owner.Id);
            check(paused is { Status: "paused", Goal.Version: 2, Input.Loop: { Used: 1, Limit: 2, Dispatch: null, Next: null } } &&
                await store.GoalShouldYield(first), "pause supersedes a claimed turn and retains its budget charge");

            store = Open();
            BotWork second;
            await using (var lease = await store.AcquireBotLease())
            {
                await store.RecoverBotWork(lease);
                await store.RecoverContinuousGoals();
                var original = (await store.InterruptedDispatches()).Single();
                check(original.Input.Loop!.Dispatch == first.Input.Loop!.Dispatch && await store.ClaimBotWork(lease) is null,
                    "restart retains the paused goal's old dispatch solely for reconciliation");
                await store.SaveHarnessReceipt(original, new("returned", "thread", "old turn finished", []));
                var settled = await store.ReadBotWork(first.Id, owner.Id);
                check(settled is { Status: "paused", Goal.Version: 2, Input.Loop.Used: 1 } && settled.Output is null &&
                    await store.NextBotInput(goal: true) is null, "late receipt does not complete or wake a paused goal");

                await ActOnMessage("继续刚才的事", new(BotAction.Resume, first.Id));
                var next = await store.NextBotInput(goal: true) ?? throw new Exception("Resume did not request a decision.");
                var snapshot = await store.DecisionContext(next, robot.Id, true, "fixture", ["codex"], 2, true, null)
                    ?? throw new Exception("Missing resumed snapshot.");
                var target = snapshot.Context.Goals.Single(g => g.Id == first.Id);
                check(next.Trigger is { Kind: BotEventKind.BotDecision, Status: "Resume" } &&
                    target is { RemainingTurns: 1, Goal.Version: 3, Goal.Status: "deciding" },
                    "resume keeps the same goal and requests a fresh decision within its remaining budget");
                var continuation = new BotDecision(BotAction.Delegate, first.Id, Instruction: "继续检查索引", Harness: "codex");
                await store.ApplyDecision(snapshot, continuation.Validate(snapshot.Context), robot.Id, true, 2, "fixture");
                second = await store.ClaimBotWork(lease) ?? throw new Exception("Resumed dispatch not claimed.");
                check(second.Id == first.Id && second is { Goal.Version: 4, Input.Loop: { Used: 2, Limit: 2 } } &&
                    second.Input.Loop.Dispatch!.Id != original.Input.Loop.Dispatch!.Id,
                    "resuming dispatches once under a new identity and consumes only the remaining turn");
                await ActOnMessage("撤销后续工作", new(BotAction.Cancel, second.Id));
            }
            store = Open();
            await using var finalLease = await store.AcquireBotLease();
            await store.RecoverBotWork(finalLease);
            await store.RecoverContinuousGoals();
            var pending = (await store.InterruptedDispatches()).Single();
            check(pending.Input.Loop!.Dispatch == second.Input.Loop!.Dispatch && await store.GoalShouldYield(pending),
                "restarted service recognizes that the cancelled goal superseded its second claimed turn");
            await store.SaveHarnessReceipt(pending, new("returned", "thread", "second turn finished", []));
            var cancelled = await store.ReadBotWork(first.Id, owner.Id);
            check(cancelled is { Status: "cancelled", Goal.Version: 5, Input.Loop: { Used: 2, Limit: 2, Next: null, Dispatch: null } } &&
                cancelled.Output is null && await store.ClaimBotWork(finalLease) is null && await store.NextBotInput(goal: true) is null &&
                (await store.InterruptedDispatches()).Length == 0,
                "late success after cancel is archived without completing, requeuing or restoring budget");
        }

        var root = Directory.CreateTempSubdirectory("littleloop-recovery-");
        try
        {
            BotWork running;
            await using (var lease = await store.AcquireBotLease())
            {
                await ActOnMessage(original, new(BotAction.Delegate, Message: "我先检查索引。", Objective: original,
                    Instruction: original, Harness: "codex"));
                running = await store.ClaimBotWork(lease) ?? throw new Exception("Initial dispatch was not claimed.");
            }
            if (scenario == Scenario.PauseResumeCancel)
            {
                await CheckControls(running);
                return;
            }
            var dispatch = running.Input.Loop!.Dispatch!;
            string session = Directory.CreateDirectory(Path.Combine(root.FullName, "session")).FullName;
            if (lostReceipt)
            {
                var journal = new HarnessTurn(session, dispatch.Id, null);
                journal.Opened("fixture-thread");
                journal.Sending();
                journal.Tool("test", "command", "running", "test has started");
                // Simulate process death after submission without marking a terminal protocol response.
            }
            else
            {
                var first = new CountingHarness(root.FullName);
                await first.SendAsync(session, dispatch.Id, dispatch.Instruction, null, default);
                check(first.Submissions == 1, "first dispatch was executed exactly once before SQL settlement");
            }
            if (revise)
            {
                await ActOnMessage(constraint, new(BotAction.Revise, running.Id, Instruction: constraint));
                check(await store.GoalShouldYield(running) && await store.NextBotInput(goal: true) is null,
                    "new goal revision waits for older inflight dispatch before making its next decision");
            }

            store = Open();
            await using var restartedLease = await store.AcquireBotLease();
            await store.RecoverBotWork(restartedLease);
            await store.RecoverContinuousGoals();
            await store.MigrateBotQueue(2);
            var claim = (await store.InterruptedDispatches()).Single();
            check(claim.Id == running.Id && claim.Goal?.Version == running.Goal!.Version &&
                claim.Input.Loop?.Dispatch == dispatch && claim.Input.Loop.Used == 1 &&
                await store.ClaimBotWork(restartedLease) is null,
                "reopened database recovers the original dispatch identity, version and budget reservation");
            var replay = new CountingHarness(root.FullName);
            var receipt = NativeBotHarness.RecoverReceipt(session, dispatch.Id)
                ?? await replay.SendAsync(session, dispatch.Id, dispatch.Instruction, null, default);
            check(replay.Submissions == 0 && receipt.Status == (lostReceipt ? "uncertain" : "returned"),
                "durable native journal reconciles completed and unknown submissions without another turn");
            await store.SaveHarnessReceipt(claim, receipt);
            var once = await store.GoalObservations(claim.Id);
            await store.SaveHarnessReceipt(claim, receipt);
            var twice = await store.GoalObservations(claim.Id);
            check((await store.InterruptedDispatches()).Length == 0 && once.SequenceEqual(twice) &&
                twice.Count(o => o.Kind == BotEventKind.HarnessReceipt) == 1 &&
                twice.Count(o => o.Kind == BotEventKind.ToolResult) == 1,
                "retried SQL settlement consumes one claim and records each receipt and tool result once");
            check(twice.Where(o => o.Kind is BotEventKind.ToolResult or BotEventKind.HarnessReceipt)
                .All(o => o.GoalVersion == claim.Goal!.Version), "recovered evidence carries the executed version into decision history");
            var current = await store.ReadBotWork(claim.Id, owner.Id) ?? throw new Exception("Recovery lost goal.");
            var next = await store.NextBotInput(goal: true);
            check(current.Input.Loop!.Used == 1 && current.Input.Loop.Dispatch is null,
                "settlement retains the original turn cost and leaves no dispatch to replay");
            if (lostReceipt)
            {
                check(current is { Status: "needs-reconciliation", Error: not null } &&
                    current.Goal!.Requirements.SequenceEqual([constraint]) && next?.Trigger is { Kind: BotEventKind.HarnessReceipt, Status: "uncertain" } &&
                    await store.ClaimBotWork(restartedLease) is null,
                    "unknown effects of superseded turn block further dispatch while retaining new constraints");
            }
            else if (revise)
            {
                check(current.Status == "deciding" && current.Goal!.Version == running.Goal!.Version + 1 &&
                    current.Goal.Requirements.SequenceEqual([constraint]) && next?.Trigger is
                        { Kind: BotEventKind.BotDecision, Status: "Revise", Text: constraint },
                    "late successful execution preserves revised version and its pending decision");
            }
            else
            {
                check(current.Status == "deciding" && next?.Trigger is
                    { Kind: BotEventKind.HarnessReceipt, Status: "returned", Text: "fixture result" },
                    "journal completed before crash reaches Bot evaluation without closing the goal");
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class CountingHarness(string project) : NativeBotHarness
    {
        public int Submissions { get; private set; }
        public override string Name => "codex";
        public override string ProjectRoot => project;
        protected override Task<HarnessReceipt> Run(string directory, string workspace, bool isolated,
            string instruction, HarnessTurn turn, CancellationToken token)
        {
            Submissions++;
            turn.Opened("fixture-thread");
            turn.Sending();
            turn.Tool("test", "command", "completed", "exit=0");
            return Task.FromResult(turn.Finish("returned", "fixture result"));
        }
    }
}
