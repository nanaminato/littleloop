using System.Text.Json;
using System.Text.Json.Serialization;
using LittleLoop.Core;
using LittleLoop.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class WorkerChecks
{
    private static async Task CheckBotGoalObligations(IConfiguration config, string project, Account owner,
        Action<bool, string> check)
    {
        await CheckTimedObligation(config, owner, check);
        await CheckRevisionObligations(owner, check);
        await CheckParallelObligations(owner, check);
        await CheckDirectedObligationSource(owner, check);
        await CheckObligationEvidence(owner, check);
        await CheckDroppedInitialEditsInRepair(owner, check);
        await CheckFailedObligationRepair(config, project, owner, check);
    }

    private static async Task CheckTimedObligation(IConfiguration config, Account owner, Action<bool, string> check)
    {
        using var fixture = new ObligationFixture(owner);
        await fixture.Initialize();
        const string resultText = "Count distinct labels in the archive.";
        const string timingText = "Recheck the saved count after 43 seconds.";
        var (sequence, running) = await fixture.StartGoal($"{resultText} {timingText}",
            Add(GoalObligationKind.Result, resultText), Add(GoalObligationKind.Time, timingText, 43));
        Guid goalId = running.Id;
        var obligations = running.Goal!.Obligations!;
        var result = obligations.Single(item => item.Kind == GoalObligationKind.Result);
        var timed = obligations.Single(item => item.Kind == GoalObligationKind.Time);
        check(obligations.Length == 2 && obligations.All(item => item.Status == GoalObligationStatus.Pending) &&
            obligations.All(item => item.GoalId == goalId && item.SourceSequence == sequence && item.CreatedVersion == running.Goal.Version) &&
            obligations.Select(item => item.Id).Distinct().Count() == 2,
            "creating a goal persists two distinct unsatisfied conditions before execution");

        await fixture.Return(running, "The archive contains 3 distinct labels.");
        var receipt = await fixture.Snapshot(goal: true);
        var dispatch = receipt.Context.DispatchEvidence.Single().Dispatches.Single();
        var resultProof = Satisfy(result, dispatch);
        var premature = new BotDecision(BotAction.Complete, goalId, Message: "The count is 3.",
            ObligationAssessments: [resultProof]);
        check(premature.Check(receipt.Context) is { Code: "unsatisfied-obligations" } &&
            await fixture.TryCommit(receipt, premature) is { Code: "unsatisfied-obligations" } &&
            (await fixture.Read(goalId)).Status == "deciding",
            "a correct result and a successful Harness receipt cannot close a goal with an unfinished time condition");

        var wait = new BotDecision(BotAction.Wait, goalId, WakeAfterSeconds: 43, VerifyDispatchId: dispatch.DispatchId,
            ObligationAssessments: [resultProof], WakeObligationId: timed.Id);
        var illegal = new BotDecision(BotAction.Delegate, goalId, Instruction: "Review the count again.", Harness: "codex",
            WakeAfterSeconds: 43, WakeObligationId: timed.Id);
        var raw = JsonSerializer.Serialize(illegal, ObligationJson);
        var model = new ObligationModel(raw, JsonSerializer.Serialize(wait, ObligationJson));
        var calls = new List<BotModelCall>();
        var repaired = await new ModelBotDecisionEngine(model).DecideWithSourceAsync(receipt.Context with
            { ObserveModelCall = calls.Add }, default);
        check(repaired.Decision is { Action: BotAction.Wait, WakeAfterSeconds: 43 } && repaired.Decision.WakeObligationId == timed.Id &&
            calls.Count == 2 && calls[0] is { Error.Code: "wake-requires-wait", Draft.Raw: not null } && calls[0].Draft!.Raw == raw &&
            model.Inputs[1].Contains(timed.Id.ToString(), StringComparison.Ordinal) &&
            model.Inputs[1].Contains(timingText, StringComparison.Ordinal),
            "repairing an illegal Delegate preserves the original draft and sees the still-effective timed condition");
        await CheckDroppedWakeInRepair(receipt.Context, illegal, raw, check);
        await fixture.Commit(receipt, repaired.Decision, repaired.Source with { ModelCalls = calls.ToArray() });

        var waiting = await fixture.Read(goalId);
        var checkId = waiting.Goal!.Obligations!.Single(item => item.Id == timed.Id).Check!.Id;
        fixture.Clock.Advance(42_999);
        check(waiting.Goal.Obligations!.Single(item => item.Id == result.Id).Status == GoalObligationStatus.Satisfied &&
            waiting.Goal.Obligations!.Single(item => item.Id == timed.Id) is { Status: GoalObligationStatus.InProgress, Check.Observed: false } &&
            await fixture.Store.NextBotInput(goal: true) is null,
            "a durable reservation is in progress and supplies no before-due wake evidence");

        await fixture.Say("What have you confirmed so far?", sequence);
        var progress = await fixture.Snapshot(goal: false);
        var scheduledIsNotProof = premature with
        {
            ObligationAssessments = [new(timed.Id, GoalObligationStatus.Satisfied, "The check was scheduled.", CheckId: checkId)]
        };
        var beforeDueEvidence = progress.Context with { Trigger = receipt.Context.Trigger, ReplyGoalId = null };
        check(scheduledIsNotProof.Check(beforeDueEvidence) is { Code: "obligation-evidence-missing" },
            "claiming satisfaction with an unobserved check ID is rejected as missing evidence");
        await fixture.Commit(progress, new(BotAction.Reply, goalId, Message: "The count is 3. The follow-up is pending.",
            GoalReplyReason: "The user requests a progress report without changing the goal."));

        var engine = new ControlledEngine();
        ChatBot Create() => new(fixture.Store, new QuietHub(), config, NullLogger<ChatBot>.Instance, engine, []);
        using (var previous = Create())
        {
            await previous.StartAsync(default);
            await previous.StopAsync(default);
        }
        fixture.Reopen();
        var restored = await fixture.Read(goalId);
        check(restored.Goal!.Obligations!.Select(item => item.Id).SequenceEqual(waiting.Goal.Obligations!.Select(item => item.Id)) &&
            restored.Goal.Obligations!.Single(item => item.Id == timed.Id).Check!.Id == checkId &&
            restored.Goal.Obligations!.Single(item => item.Id == result.Id).Assessment == resultProof &&
            await fixture.Store.NextBotInput(goal: true) is null,
            "reopening the SQLite store during a wait preserves stable condition identities, proof and the timer fence");
        fixture.Clock.Advance(1);
        var due = await fixture.Snapshot(goal: true);
        check(due.Context.Trigger is { Kind: BotEventKind.Timer } && due.Context.Trigger.CheckId == checkId &&
            due.Context.Trigger.WakeObligationId == timed.Id,
            "the due Timer enters the unified decision context with evidence scoped to its own condition");
        var complete = premature with
        {
            Message = "I rechecked the saved distinct-label count against the archived report. The count remains 3.",
            ObligationAssessments = [new(timed.Id, GoalObligationStatus.Satisfied,
                "At the recorded due check, I compared the saved count with the archive receipt.", CheckId: checkId)]
        };
        var repeat = new BotDecision(BotAction.Delegate, goalId, Harness: "codex", Instruction: running.Input.Loop!.Dispatch!.Instruction);
        check(repeat.Check(due.Context) is { Code: "verified-delegation-repeated" } && (repeat with
        {
            ReviewDispatchId = dispatch.DispatchId,
            ReviewReason = "Verify independently if another archival observation contradicts the count."
        }).Check(due.Context) is null,
            "the verified dispatch discourages an unexplained repeat while permitting a Bot-identified review reason");
        using var restarted = Create();
        await restarted.StartAsync(default);
        BotWork done;
        try
        {
            var request = await engine.Next();
            if (request.Context.Trigger != due.Context.Trigger)
                throw new InvalidOperationException("The restarted worker did not receive the saved due check.");
            request.Choose(complete);
            done = await fixture.Until(goalId, item => item.Status == "completed");
        }
        finally { await restarted.StopAsync(default); }
        var events = await fixture.Store.GoalObservations(goalId);
        check(done.Status == "completed" && done.Goal!.Obligations!.All(item => item.Status == GoalObligationStatus.Satisfied) &&
            done.Output == complete.Message && events.Count(item => item.Status == "Delegate") == 1 &&
            events.Count(item => item.Kind == BotEventKind.Timer) == 1,
            "the Bot closes the goal when both conditions are supported, reusing verified work without another dispatch");
    }

    private static async Task CheckDroppedWakeInRepair(BotDecisionContext context, BotDecision illegal, string original,
        Action<bool, string> check)
    {
        var dropsWake = illegal with { WakeAfterSeconds = null, WakeObligationId = null };
        var model = new ObligationModel(original, JsonSerializer.Serialize(dropsWake, ObligationJson));
        var calls = new List<BotModelCall>();
        try
        {
            await new ModelBotDecisionEngine(model).DecideAsync(context with { ObserveModelCall = calls.Add }, default);
            throw new InvalidOperationException("The repaired draft silently discarded the timed intent.");
        }
        catch (InvalidDataException)
        {
            check(calls.Count == 2 && calls[1] is { Error.Code: "repair-wake-intent-unresolved" },
                "a well-formed replacement draft cannot silently drop the timed intent during repair");
        }
        var defers = dropsWake with { GoalWakeDeferralReason = "I need to retrieve the newer report before scheduling the saved follow-up." };
        var explained = new ObligationModel(original, JsonSerializer.Serialize(defers, ObligationJson));
        var result = await new ModelBotDecisionEngine(explained).DecideAsync(context, default);
        check(result == defers, "the Bot can explicitly explain why it will act first while the existing time condition remains active");
    }

    private static async Task CheckRevisionObligations(Account owner, Action<bool, string> check)
    {
        using var fixture = new ObligationFixture(owner);
        await fixture.Initialize();
        const string original = "Read the open inventory.";
        const string constraint = "Exclude items flagged for disposal.";
        var (sequence, running) = await fixture.StartGoal(original, Add(GoalObligationKind.Result, original));
        var initial = running.Goal!.Obligations!.Single();
        long supplement = await fixture.Say(constraint, sequence);
        var revision = await fixture.Snapshot(goal: false);
        var falseReply = new BotDecision(BotAction.Reply, running.Id, Message: "I have saved the new requirement.");
        var rejection = await fixture.TryCommit(revision, falseReply);
        var unchanged = await fixture.Read(running.Id);
        check(rejection is { Code: "goal-source-disposition-required" } && unchanged.Goal!.Version == running.Goal.Version &&
            unchanged.Goal.Requirements.Length == 0 && unchanged.Goal.Obligations!.Single().Id == initial.Id &&
            (await fixture.Snapshot(goal: false)).Input.Sequence == supplement,
            "a goal-bound Reply cannot acknowledge a revision by consuming its source without durable conditions");

        await fixture.Commit(revision, new(BotAction.Revise, running.Id, Instruction: constraint,
            ObligationEdits: [Add(GoalObligationKind.Constraint, constraint)]));
        var amended = await fixture.Read(running.Id);
        var added = amended.Goal!.Obligations!.Single(item => item.Kind == GoalObligationKind.Constraint);
        await fixture.Return(running, "I read the old unfiltered inventory.");
        var next = await fixture.Snapshot(goal: true);
        var oldDispatch = next.Context.DispatchEvidence.Single().Dispatches.Single();
        var staleProof = new BotDecision(BotAction.Complete, running.Id,
            ObligationAssessments: [Satisfy(initial, oldDispatch), Satisfy(added, oldDispatch)]);
        check(amended.Goal.Version > running.Goal.Version && amended.Goal.Requirements.Contains(constraint) &&
            next.Context.Trigger.Kind == BotEventKind.BotDecision && oldDispatch.GoalVersion == running.Goal.Version &&
            (await fixture.Read(running.Id)).Goal!.Obligations!.Any(item => item.Id == added.Id && item.Status == GoalObligationStatus.Pending &&
                item.SourceSequence == supplement && item.CreatedVersion == amended.Goal.Version) &&
            staleProof.Check(next.Context) is { Code: "obligation-evidence-missing" },
            "a late old-version receipt is retained as evidence but cannot satisfy a concurrently added condition");

        await fixture.Say("Use the closed inventory instead. The disposal filter no longer applies.", sequence);
        var replacement = await fixture.Snapshot(goal: false);
        const string substitute = "Read the closed inventory instead of the open inventory.";
        await fixture.Commit(replacement, new(BotAction.Revise, running.Id, Instruction: substitute,
            ObligationEdits:
            [
                new(GoalObligationEditKind.Replace, initial.Id, GoalObligationKind.Result, substitute,
                    Reason: "The user switched to the closed inventory."),
                new(GoalObligationEditKind.Withdraw, added.Id, Reason: "The user removed the disposal filter.")
            ]));
        fixture.Reopen();
        var replaced = (await fixture.Read(running.Id)).Goal!.Obligations!;
        check(replaced.Length == 3 && replaced.Single(item => item.Id == initial.Id).Status == GoalObligationStatus.Superseded &&
            replaced.Single(item => item.Id == added.Id).Status == GoalObligationStatus.Withdrawn &&
            replaced.Single(item => item.Description == substitute) is { Status: GoalObligationStatus.Pending } current && current.Replaces == initial.Id &&
            current.Id != initial.Id && current.Id != added.Id,
            "a user replacement creates a stable new condition while the superseded and withdrawn conditions survive reopening");
    }

    private static async Task CheckParallelObligations(Account owner, Action<bool, string> check)
    {
        using var fixture = new ObligationFixture(owner);
        await fixture.Initialize();
        const string firstResult = "Read the report title.";
        const string timing = "Check the title once more after 17 seconds.";
        var (firstSequence, first) = await fixture.StartGoal($"{firstResult} {timing}",
            Add(GoalObligationKind.Result, firstResult), Add(GoalObligationKind.Time, timing, 17));
        var firstCondition = first.Goal!.Obligations!.Single(item => item.Kind == GoalObligationKind.Result);
        var followup = first.Goal!.Obligations!.Single(item => item.Kind == GoalObligationKind.Time);
        await fixture.Return(first, "The title is North.");
        var firstReturn = await fixture.Snapshot(goal: true);
        var firstEvidence = firstReturn.Context.DispatchEvidence.Single().Dispatches.Single();
        await fixture.Commit(firstReturn, new(BotAction.Wait, first.Id, WakeAfterSeconds: 17, WakeObligationId: followup.Id,
            ObligationAssessments: [Satisfy(firstCondition, firstEvidence)]));

        const string secondResult = "Read the independent ledger total.";
        const string notice = "Notify me with the total after checking it.";
        var (_, second) = await fixture.StartGoal($"{secondResult} {notice}", Add(GoalObligationKind.Result, secondResult),
            Add(GoalObligationKind.Notice, notice));
        var secondCondition = second.Goal!.Obligations!.Single(item => item.Kind == GoalObligationKind.Result);
        var noticeCondition = second.Goal.Obligations!.Single(item => item.Kind == GoalObligationKind.Notice);
        await fixture.Return(second, "The total is 19.");
        var secondReturn = await fixture.Snapshot(goal: true);
        var secondEvidence = secondReturn.Context.DispatchEvidence.Single().Dispatches.Single();
        var done = new BotDecision(BotAction.Complete, second.Id, Message: "The independent total is 19.",
            ObligationAssessments: [Satisfy(secondCondition, secondEvidence), new(noticeCondition.Id, GoalObligationStatus.Satisfied,
                "The verified total is included in this final notice.", CompletionNotice: true)]);
        var foreignCondition = done with { ObligationAssessments = [Satisfy(firstCondition, secondEvidence)] };
        var foreignProof = done with { ObligationAssessments = [Satisfy(secondCondition, firstEvidence)] };
        check(secondReturn.Context.Goals.Any(item => item.Id == first.Id) && secondReturn.Context.DispatchEvidence.Single().GoalId == second.Id &&
            await fixture.TryCommit(secondReturn, foreignCondition) is { Code: "invalid-obligation-assessment" } &&
            await fixture.TryCommit(secondReturn, foreignProof) is { Code: "obligation-evidence-missing" },
            "parallel goals stay visible while cross-goal condition IDs and evidence cannot authorize completion");
        check((done with { Message = null }).Check(secondReturn.Context) is { Code: "obligation-evidence-missing" } &&
            done.Check(secondReturn.Context with { AutoFinalReport = false }) is { Code: "obligation-evidence-missing" },
            "a completion-notice condition needs real message content and an enabled notification path");
        await fixture.Commit(secondReturn, done);
        check((await fixture.Read(second.Id)).Status == "completed" && (await fixture.Read(first.Id)).Status == "waiting-input",
            "one goal can complete without closing or counting the other goal's scheduled check");

        const string early = "The current title is enough. Please finish now and skip the later check.";
        await fixture.Say(early, firstSequence);
        var withdrawal = await fixture.Snapshot(goal: false);
        await fixture.Commit(withdrawal, new(BotAction.Revise, first.Id, Instruction: early,
            ObligationEdits: [new(GoalObligationEditKind.Withdraw, followup.Id, Reason: "The user explicitly waived the later check.")]));
        var unblocked = await fixture.Snapshot(goal: true);
        await fixture.Commit(unblocked, new(BotAction.Complete, first.Id, Message: "The report title is North."));
        fixture.Clock.Advance(17_000);
        var earlyDone = await fixture.Read(first.Id);
        check(earlyDone.Status == "completed" && earlyDone.Goal!.Obligations!.Single(item => item.Id == followup.Id)
            .Status == GoalObligationStatus.Withdrawn && (await fixture.Store.GoalObservations(first.Id)).All(item => item.Kind != BotEventKind.Timer) &&
            await fixture.Store.NextBotInput(goal: true) is null,
            "an explicit user waiver supports early completion and invalidates the old timer without inventing a check");
    }

    private static async Task CheckObligationEvidence(Account owner, Action<bool, string> check)
    {
        using var fixture = new ObligationFixture(owner);
        await fixture.Initialize();
        const string objective = "Confirm the new index can be read.";
        const string behavior = "Confirm that reading the retired index yields the expected failure.";
        const string constraint = "The retired index must remain unreadable.";
        var (_, running) = await fixture.StartGoal($"{objective} {behavior} {constraint}", Add(GoalObligationKind.Result, objective),
            Add(GoalObligationKind.Behavior, behavior), Add(GoalObligationKind.Constraint, constraint));
        var obligations = running.Goal!.Obligations!;
        var condition = obligations.Single(item => item.Kind == GoalObligationKind.Result);
        await fixture.Return(running, "The index looks readable, but one read failed and other tool output was omitted.",
            status: "failed", truncated: true);
        var receipt = await fixture.Snapshot(goal: true);
        var dispatch = receipt.Context.DispatchEvidence.Single().Dispatches.Single();
        var draft = new BotDecision(BotAction.Complete, running.Id, ObligationAssessments: [Satisfy(condition, dispatch)]);
        var missing = draft with { ObligationAssessments = [Satisfy(condition, dispatch) with { EventId = long.MaxValue }] };
        var conflicting = receipt.Context with
        {
            DispatchEvidence = [receipt.Context.DispatchEvidence.Single() with
            {
                Dispatches = [dispatch with { ReceiptToolsTruncated = false }]
            }]
        };
        var incomplete = receipt.Context with
        {
            DispatchEvidence = [receipt.Context.DispatchEvidence.Single() with
            {
                Dispatches = [dispatch with { ToolEvidence = [], ReceiptToolsTruncated = true, ReportedFailedTools = 0 }]
            }]
        };
        check(missing.Check(receipt.Context) is { Code: "obligation-evidence-missing" } &&
            draft.Check(conflicting) is { Code: "obligation-evidence-missing" } &&
            draft.Check(incomplete) is { Code: "obligation-evidence-missing" } &&
            await fixture.TryCommit(receipt, draft) is { Code: "obligation-evidence-missing" },
            "missing event references, conflicting tool outcomes and incomplete receipt evidence block satisfaction");

        var failure = dispatch.ToolEvidence.Single(fact => fact.Status == "failed");
        var expected = obligations.Where(item => item.Kind is GoalObligationKind.Behavior or GoalObligationKind.Constraint)
            .Select(item => new GoalObligationAssessment(item.Id, GoalObligationStatus.Satisfied, "The retired index failed to open as required.",
                dispatch.DispatchId, failure.EventId, ExpectedToolStatus: "failed")).ToArray();
        var wait = new BotDecision(BotAction.Wait, running.Id, Message: "The new index still needs the full report.", ObligationAssessments:
            [.. expected, new(condition.Id, GoalObligationStatus.Unknown, "The reported read and recorded failure disagree.")]);
        var deniedContext = receipt.Context with
        {
            DispatchEvidence = [receipt.Context.DispatchEvidence.Single() with
            {
                Dispatches = [dispatch with { ToolEvidence = [failure with { Status = "denied" }] }]
            }]
        };
        var expectedFailure = expected.First();
        check((wait with { ObligationAssessments = [expectedFailure with { Id = condition.Id }] }).Check(receipt.Context)
                is { Code: "obligation-evidence-missing" } &&
            (wait with { ObligationAssessments = [expectedFailure] }).Check(deniedContext) is { Code: "obligation-evidence-missing" } &&
            (wait with { ObligationAssessments = [expectedFailure with { ExpectedToolStatus = "denied" }] }).Check(deniedContext)
                is { Code: "obligation-evidence-missing" } &&
            (wait with { ObligationAssessments = [expectedFailure with { EventId = dispatch.Receipt!.EventId }] }).Check(receipt.Context)
                is { Code: "obligation-evidence-missing" } &&
            (wait with { ObligationAssessments = [expectedFailure with { ExpectedToolStatus = null }] }).Check(receipt.Context)
                is { Code: "obligation-evidence-missing" },
            "a recorded failure cannot establish a result, treat denial as expected failure, use a receipt as a tool result, or imply success");
        await fixture.Commit(receipt, wait);
        var assessed = (await fixture.Read(running.Id)).Goal!.Obligations!;
        check(assessed.Single(item => item.Id == condition.Id).Status == GoalObligationStatus.Unknown &&
            assessed.Where(item => item.Id != condition.Id).All(item => item is
                { Status: GoalObligationStatus.Satisfied, Assessment.ExpectedToolStatus: "failed" }),
            "the Bot can retain an unknown result while assessing the matching failed tool fact for behavior and constraint conditions");
    }

    private static async Task CheckDirectedObligationSource(Account owner, Action<bool, string> check)
    {
        using var fixture = new ObligationFixture(owner);
        await fixture.Initialize();
        const string objective = "Confirm the directory name.";
        var (firstSequence, first) = await fixture.StartGoal(objective, Add(GoalObligationKind.Result, objective));
        var condition = first.Goal!.Obligations!.Single();
        await fixture.Return(first, "The directory is named amber.");
        var receipt = await fixture.Snapshot(goal: true);
        await fixture.Commit(receipt, new(BotAction.Wait, first.Id,
            ObligationAssessments: [Satisfy(condition, receipt.Context.DispatchEvidence.Single().Dispatches.Single())]));
        var satisfied = await fixture.Read(first.Id);

        const string peerObjective = "Find the owner's contact number.";
        var (_, peer) = await fixture.StartGoal(peerObjective, Add(GoalObligationKind.Result, peerObjective));
        const string newCondition = "Also include the full directory path.";
        long sourceSequence = await fixture.Say(newCondition, firstSequence);
        var source = await fixture.Snapshot(goal: false);
        var edits = new[] { Add(GoalObligationKind.Result, newCondition) };
        var otherGoal = new BotDecision(BotAction.Revise, peer.Id, Instruction: newCondition, ObligationEdits: edits);
        var newGoal = new BotDecision(BotAction.Delegate, Message: "I will start another task.", Objective: newCondition,
            Instruction: "Read the directory path.", Harness: "codex", ObligationEdits: edits);
        check(source.Context.ReplyGoalId == first.Id &&
            otherGoal.Check(source.Context) is { Code: "goal-source-target-conflict" } &&
            newGoal.Check(source.Context) is { Code: "goal-source-target-conflict" } &&
            await fixture.TryCommit(source, otherGoal) is { Code: "goal-source-target-conflict" } &&
            await fixture.TryCommit(source, newGoal) is { Code: "goal-source-target-conflict" },
            "a source addressed to one goal cannot revise a peer or silently start another goal");

        var complete = new BotDecision(BotAction.Complete, first.Id, Message: "The directory is amber.");
        var wait = new BotDecision(BotAction.Wait, first.Id, Message: "I will wait for further input.");
        check(satisfied.Goal!.Obligations!.All(item => item.Status == GoalObligationStatus.Satisfied) &&
            complete.Check(source.Context) is { Code: "goal-source-disposition-required" } &&
            wait.Check(source.Context) is { Code: "goal-source-disposition-required" } &&
            await fixture.TryCommit(source, complete) is { Code: "goal-source-disposition-required" } &&
            await fixture.TryCommit(source, wait) is { Code: "goal-source-disposition-required" } &&
            (await fixture.Snapshot(goal: false)).Input.Sequence == sourceSequence,
            "even with all original conditions satisfied, Complete and Wait cannot consume an unrecorded goal-linked source");

        await fixture.Commit(source, otherGoal with { GoalId = first.Id });
        var revised = await fixture.Read(first.Id);
        var unaffected = await fixture.Read(peer.Id);
        check(revised.Goal!.Obligations!.Any(item => item.Description == newCondition && item.SourceSequence == sourceSequence) &&
            unaffected.Goal!.Version == peer.Goal!.Version && unaffected.Goal.Obligations!.Single().Description == peerObjective,
            "the original source remains available for a durable revision of the addressed goal alone");
    }

    private static async Task CheckFailedObligationRepair(IConfiguration config, string project, Account owner,
        Action<bool, string> check)
    {
        using var fixture = new ObligationFixture(owner);
        await fixture.Initialize();
        var model = new DraftModel();
        var harness = new ControlledHarness("codex", project);
        using var worker = new ChatBot(fixture.Store, new QuietHub(), config, NullLogger<ChatBot>.Instance,
            new ModelBotDecisionEngine(model), [harness]);
        await worker.StartAsync(default);
        try
        {
            const string goal = "Identify the report author and confirm the source signature.";
            long sequence = await fixture.Say(goal);
            (await model.Next()).Return(JsonSerializer.Serialize(new BotDecision(BotAction.Delegate,
                Message: "I will read the report.", Objective: goal, Instruction: "Read the author and signature.", Harness: "codex",
                ObligationEdits:
                [
                    Add(GoalObligationKind.Result, "Identify the report author."),
                    Add(GoalObligationKind.Result, "Confirm the report signature.")
                ]), ObligationJson));
            (await harness.Next()).Return("The author is Ada. The signature was not present.");
            var receipt = await model.Next();
            using var input = JsonDocument.Parse(receipt.Input);
            Guid goalId = input.RootElement.GetProperty("trigger").GetProperty("goalId").GetGuid();
            const string first = """{"action":"complete","message":"The report was signed."}""";
            const string second = """{"action":"complete","message":"Everything is confirmed."}""";
            receipt.Return(first);
            var correction = await model.Next();
            correction.Return(second);

            var stopped = await fixture.Until(goalId, item => item.Status == "waiting-input");
            check(stopped.Error is not null && stopped.Output is null && stopped.Goal!.Obligations!.Length == 2 &&
                stopped.Goal.Obligations.All(item => item.Status != GoalObligationStatus.Satisfied) &&
                harness.Calls.Count == 1 && await fixture.Store.NextBotInput(goal: true) is null,
                "exhausting the normal draft-repair budget persists a failed decision without fabricating satisfaction or replaying execution");
            check(await fixture.RepairWasRecorded(goalId, first, second),
                "the failed repair preserves both rejected decision drafts in the durable event ledger");

            long supplement = await fixture.Say("Please identify the signature date too.", sequence);
            const string wrongReply = """{"action":"reply","message":"The signature date requirement is saved."}""";
            (await model.Next()).Return(wrongReply);
            (await model.Next()).Return(wrongReply);
            var unresolved = await fixture.Until(goalId, item => item.Goal?.PendingRequirementSources?.Contains(supplement) == true);
            check(unresolved.Goal!.Obligations!.Length == 2 && unresolved.Goal.Requirements.Length == 0 &&
                await fixture.RepairWasRecorded(goalId, wrongReply, wrongReply),
                "a failed goal-linked revision is recorded as an unresolved user source instead of an acknowledged requirement");
        }
        finally { await worker.StopAsync(default); }
    }

    private static async Task CheckDroppedInitialEditsInRepair(Account owner, Action<bool, string> check)
    {
        using var fixture = new ObligationFixture(owner);
        await fixture.Initialize();
        const string task = "Find the invoice total and keep the original source unchanged.";
        await fixture.Say(task);
        var input = await fixture.Snapshot(goal: false);
        var total = Add(GoalObligationKind.Result, "Find the invoice total.");
        var unchanged = Add(GoalObligationKind.Constraint, "Keep the source unchanged.");
        var illegal = new BotDecision(BotAction.Delegate, Message: "I will read the invoice.", Objective: task,
            Instruction: "Read the invoice without editing it.", Harness: "unregistered", ObligationEdits: [total, unchanged]);
        string first = JsonSerializer.Serialize(illegal, ObligationJson);
        var valid = illegal with { Harness = "codex" };
        string incomplete = JsonSerializer.Serialize(valid with { ObligationEdits = [total] }, ObligationJson);
        var model = new ObligationModel(first, incomplete);
        var calls = new List<BotModelCall>();
        try
        {
            await new ModelBotDecisionEngine(model).DecideAsync(input.Context with { ObserveModelCall = calls.Add }, default);
            throw new InvalidOperationException("Repair lost a valid condition from the first candidate.");
        }
        catch (InvalidDataException)
        {
            check(calls.Select(call => call.Error?.Code).SequenceEqual(["unknown-harness", "repair-obligation-edits-unresolved"]) &&
                await fixture.Store.NextBotInput(goal: true) is null,
                "correcting an invalid initial Harness ID cannot drop a second valid user condition");
        }

        var retained = new ObligationModel(first, JsonSerializer.Serialize(valid, ObligationJson));
        var repaired = await new ModelBotDecisionEngine(retained).DecideAsync(input.Context, default);
        await fixture.Commit(input, repaired);
        var claimed = await fixture.Claim();
        check(claimed.Goal!.Obligations!.Select(item => item.Description).SequenceEqual([total.Description, unchanged.Description]),
            "repair can switch to a legal Harness and persist both of the originally drafted conditions");
    }

    private static GoalObligationEdit Add(GoalObligationKind kind, string description, int? delay = null) =>
        new(GoalObligationEditKind.Add, Kind: kind, Description: description, DelaySeconds: delay);

    private static GoalObligationAssessment Satisfy(GoalObligation item, BotDispatchEvidence dispatch) =>
        new(item.Id, GoalObligationStatus.Satisfied, "The scoped execution provides evidence for this condition.",
            dispatch.DispatchId, dispatch.Receipt!.EventId);

    private static readonly JsonSerializerOptions ObligationJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed class ObligationModel(params string[] drafts) : ISemanticModelClient
    {
        public string Provider => "fixture";
        public string Model => "goal-obligation-model";
        public List<string> Inputs { get; } = [];

        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string draft = drafts[Inputs.Count];
            Inputs.Add(input);
            return Task.FromResult(new SemanticModelReply(draft, Model));
        }
    }

    private sealed class ObligationFixture : IDisposable
    {
        private const string Grant = "obligation-fixture";
        private const int TurnLimit = 3;
        private readonly TemporaryChatDatabase database = new();
        private readonly Account owner;
        private string robot = "";
        private string room = "";

        public TestClock Clock { get; } = new();
        public ChatStore Store { get; private set; }

        public ObligationFixture(Account owner)
        {
            this.owner = owner;
            Store = Open();
        }

        private ChatStore Open() => new(database.Connection, false, "Sqlite", Clock, requireObligations: true);
        public void Reopen() => Store = Open();

        public async Task Initialize()
        {
            await Store.Initialize();
            await Store.InitializeChannelAdministration();
            await Store.InitializeGroupModeration();
            await Store.InitializeChannelDiscussions();
            await Store.Register(owner);
            var bot = await Store.GetRobot(ChatStore.BotName);
            room = (await Store.CreateRoom(owner, new("direct", null, [bot.Name]))).Id;
            robot = bot.Id;
            await Store.InstallRobot(room, owner.Id, [robot], true);
        }

        public async Task<long> Say(string text, long? replyTo = null) =>
            (await Store.Send(owner.Id, owner.Name, room, new(Guid.NewGuid().ToString(), text, replyTo), goalRobot: robot))
                ?.Message.Sequence ?? throw new InvalidOperationException("Could not enqueue the fixture message.");

        public async Task<DecisionSnapshot> Snapshot(bool goal)
        {
            var input = await Store.NextBotInput(goal) ?? throw new InvalidOperationException("Missing fixture input.");
            return await Store.DecisionContext(input, robot, true, Grant, ["codex", "deepseek"], TurnLimit, true, null)
                ?? throw new InvalidOperationException("Missing fixture decision snapshot.");
        }

        public async Task Commit(DecisionSnapshot snapshot, BotDecision decision, BotDecisionSource? source = null)
        {
            var error = await Store.ApplyDecision(snapshot, decision.Validate(snapshot.Context), robot, true, TurnLimit, Grant,
                cognitionSource: source);
            if (error is not null) throw new InvalidOperationException($"Fixture decision failed with {error.Code}. {error.Message}");
        }

        public Task<BotDecisionError?> TryCommit(DecisionSnapshot snapshot, BotDecision decision) =>
            Store.ApplyDecision(snapshot, decision, robot, true, TurnLimit, Grant);

        public async Task<BotWork> Claim()
        {
            while (await Store.NextGoalNotice() is { } notice)
            {
                await Store.Send(robot, ChatStore.BotName, notice.Room, new(notice.Id, notice.Text, notice.ReplyTo),
                    card: new("task", TaskId: notice.TaskId));
                await Store.CompleteGoalNotice(notice.Id);
            }
            await using var lease = await Store.AcquireBotLease();
            return await Store.ClaimBotWork(lease) ?? throw new InvalidOperationException("Missing fixture dispatch.");
        }

        public async Task<(long Sequence, BotWork Running)> StartGoal(string objective, params GoalObligationEdit[] conditions)
        {
            long sequence = await Say(objective);
            var input = await Snapshot(goal: false);
            await Commit(input, new(BotAction.Delegate, Message: "I will begin with the available data.", Objective: objective,
                Instruction: "Read the requested data and report the result.", Harness: "codex", ObligationEdits: conditions));
            return (sequence, await Claim());
        }

        public Task Return(BotWork running, string report, string status = "completed", bool? truncated = false) =>
            Store.SaveHarnessReceipt(running, new("returned", "fixture-session", report,
                [new("read-1", "read", status, report)], ToolsTruncated: truncated, DispatchSubmitted: true));

        public async Task<BotWork> Read(Guid id) => await Store.ReadBotWork(id, owner.Id)
            ?? throw new InvalidOperationException("Missing fixture goal.");

        public async Task<BotWork> Until(Guid id, Func<BotWork, bool> matches)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            while (true)
            {
                var work = await Read(id);
                if (matches(work)) return work;
                await Task.Delay(25, deadline.Token);
            }
        }

        public async Task<bool> RepairWasRecorded(Guid id, string first, string second)
        {
            var options = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(database.Connection) { Pooling = false };
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT COUNT(*) FROM chat_bot_events WHERE work_id=$id
                    AND json_extract(detail, '$.cognitionSource.modelCalls[0].draft.raw')=$first
                    AND json_extract(detail, '$.cognitionSource.modelCalls[1].draft.raw')=$second
                    AND json_extract(detail, '$.cognitionSource.modelCalls[0].error.code') IS NOT NULL
                    AND json_extract(detail, '$.cognitionSource.modelCalls[1].error.code') IS NOT NULL
                """;
            query.Parameters.AddWithValue("$id", id.ToString());
            query.Parameters.AddWithValue("$first", first);
            query.Parameters.AddWithValue("$second", second);
            return Convert.ToInt64(await query.ExecuteScalarAsync()) == 1;
        }

        public void Dispose() => database.Dispose();
    }
}
