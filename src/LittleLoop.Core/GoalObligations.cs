using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleLoop.Core;

public sealed class GoalObligationEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where T : struct, Enum { }

[JsonConverter(typeof(GoalObligationEnumConverter<GoalObligationKind>))]
public enum GoalObligationKind { Result, Behavior, Time, Constraint, Notice }

[JsonConverter(typeof(GoalObligationEnumConverter<GoalObligationStatus>))]
public enum GoalObligationStatus { Pending, InProgress, Satisfied, Unknown, Superseded, Withdrawn }

[JsonConverter(typeof(GoalObligationEnumConverter<GoalObligationEditKind>))]
public enum GoalObligationEditKind { Add, Replace, Withdraw }

public sealed record GoalObligationSource(long Sequence, string Text, Guid? GoalId = null);

public sealed record GoalObligationCheck(Guid Id, long DueAtUnixMilliseconds, int GoalVersion, bool Observed = false);

public sealed record GoalObligationEdit(GoalObligationEditKind Action, Guid? Id = null, GoalObligationKind? Kind = null,
    string? Description = null, int? DelaySeconds = null, string? Reason = null, long? SourceSequence = null);

public sealed record GoalObligationAssessment(Guid Id, GoalObligationStatus Status, string Reason,
    Guid? DispatchId = null, long? EventId = null, Guid? CheckId = null, bool CompletionNotice = false,
    string? ExpectedToolStatus = null);

public sealed record GoalObligation(Guid Id, Guid GoalId, int CreatedVersion, long SourceSequence,
    GoalObligationKind Kind, string Description, GoalObligationStatus Status = GoalObligationStatus.Pending,
    int? DelaySeconds = null, Guid? Replaces = null, GoalObligationCheck? Check = null,
    GoalObligationAssessment? Assessment = null, int? UpdatedVersion = null, long? ChangeSourceSequence = null,
    string? ChangeReason = null)
{
    [JsonIgnore]
    public bool Active => Status is not (GoalObligationStatus.Superseded or GoalObligationStatus.Withdrawn);
}

/// <summary>The Bot supplies meanings and assessments. This policy checks their source and the evidence boundary.</summary>
public static class GoalObligationPolicy
{
    private const int MaxEditsPerDecision = 24;
    private const int MaxObligations = 96;

    internal static Guid? DirectedUserGoal(BotDecisionContext context) =>
        context.Trigger.Kind == BotEventKind.UserMessage && context.Goals.Any(g => g.Id == context.ReplyGoalId &&
            (context.RequireObligations || g.Goal.Obligations is not null) &&
            g.Goal.Status is not ("completed" or "cancelled" or "disabled" or "failed"))
            ? context.ReplyGoalId : null;

    internal static bool IsDirectedUserAction(BotAction action) =>
        action is BotAction.Reply or BotAction.Revise or BotAction.Pause or BotAction.Resume or BotAction.Cancel or BotAction.ManageMemory;

    // Scheduling state is objective; only the Bot interprets when a condition's starting event occurred.
    public static GoalObligation[] UnscheduledTimeConditions(BotGoalSnapshot goal, BotObservation trigger) =>
        (goal.Goal.Obligations ?? []).Where(o => IsOpenTimeCondition(o) && (o.Check is not { } check ||
            !check.Observed && !IsDueCheck(o, check, trigger) &&
            (check.GoalVersion != goal.Goal.Version || check.DueAtUnixMilliseconds != goal.WakeAtUnixMilliseconds))).ToArray();

    private static bool IsOpenTimeCondition(GoalObligation obligation) => obligation is
        { Active: true, Kind: GoalObligationKind.Time, Status: not GoalObligationStatus.Satisfied };

    internal static BotDecisionError? CheckUserSource(BotDecision decision, BotDecisionContext context)
    {
        if (DirectedUserGoal(context) is not Guid directed) return null;
        if ((decision.GoalId is Guid chosen && chosen != directed) ||
            (decision.GoalId is null && decision.Action == BotAction.Delegate))
        {
            var linked = context.Goals.First(goal => goal.Id == directed);
            return Error("goal-source-target-conflict", "goalId",
                $"User message #{context.Trigger.Sequence} is linked to goal {linked.Id} at version {linked.Goal.Version}.")
                with { Expected = linked.Id.ToString(), Actual = decision.GoalId?.ToString() ?? "<missing>" };
        }
        if (!IsDirectedUserAction(decision.Action) ||
            (decision.Action == BotAction.Reply && !Text(decision.GoalReplyReason, 1000)))
            return Error("goal-source-disposition-required", "action",
                "A goal-linked message needs a revision, an explicit goal control, or an informational reply that explains why it leaves the requirements unchanged.");
        return null;
    }

    public static BotDecisionError? Check(BotDecision decision, BotDecisionContext context, BotGoalSnapshot? target)
    {
        var obligations = target?.Goal.Obligations;
        var edits = decision.ObligationEdits ?? [];
        var assessments = decision.ObligationAssessments ?? [];
        bool required = context.RequireObligations || obligations is not null;
        bool creating = decision.Action == BotAction.Delegate && target is null;
        bool revising = decision.Action == BotAction.Revise;
        var userSequence = context.Trigger.Kind == BotEventKind.UserMessage ? context.Trigger.Sequence : null;

        if (decision.GoalReplyReason is not null && (decision.Action != BotAction.Reply || !Text(decision.GoalReplyReason, 1000)))
            return Error("invalid-goal-reply-reason", "goalReplyReason", "A no-change reason belongs to an informational Reply.");
        var pending = target?.Goal.PendingRequirementSources ?? [];
        if (decision.ResolveSourceSequence is long resolved && (userSequence is null || target is null ||
            !pending.Contains(resolved) || !context.ObligationSources.Any(source => source.Sequence == resolved && source.GoalId == target.Id) ||
            decision.Action != BotAction.Revise && (decision.Action != BotAction.Reply || decision.GoalReplyReason is null)))
        {
            var available = context.ObligationSources.Where(source =>
                source.GoalId == target?.Id && pending.Contains(source.Sequence)).Select(source => source.Sequence);
            var sequences = string.Join(", ", available);
            return Error("unknown-pending-source", "resolveSourceSequence",
                $"Source #{resolved} cannot be resolved by this {decision.Action} on goal {target?.Id} at version {target?.Goal.Version}. " +
                "A current user turn is handled by a normal revision, while an older unresolved message needs an eligible pending source.")
                with
                {
                    Expected = sequences.Length > 0
                        ? $"omit for a normal revision, or use a pending source from [{sequences}] during a sourced Revise or Reply"
                        : "omit resolveSourceSequence because no pending source is currently available",
                    Actual = resolved.ToString()
                };
        }

        if (required && creating && edits.Length == 0)
            return Error("initial-obligations-required", "obligationEdits", "Record the user's distinct conditions as Add edits when creating this goal.");
        if (required && revising && edits.Length == 0)
            return Error("revision-obligations-required", "obligationEdits", "Translate the user's revision into explicit Add, Replace or Withdraw edits.");

        bool initializing = target is not null && obligations is null && edits.Length > 0;
        if (edits.Length > 0)
        {
            int editLimit = initializing ? MaxObligations : MaxEditsPerDecision;
            if (edits.Length > editLimit || edits.Any(e => e is null))
                return Error("invalid-obligation-edits", "obligationEdits", $"Provide at most {editLimit} non-null edits.");
            var problem = CheckEdits(edits, context, target, creating, revising, initializing, userSequence,
                decision.ResolveSourceSequence);
            if (problem is not null) return problem;
        }

        if (decision.WakeObligationId is Guid wake)
        {
            var obligation = obligations?.FirstOrDefault(o => o.Id == wake);
            if (decision.Action != BotAction.Wait || decision.WakeAfterSeconds is null)
                return Error("invalid-obligation-wake", "wakeObligationId", "A time condition reservation needs a timed Wait.") with
                { Expected = "action=wait and wakeAfterSeconds set", Actual = $"{decision.Action}; {decision.WakeAfterSeconds?.ToString() ?? "no delay"}" };
            if (obligation is null || obligation.GoalId != target!.Id || !IsOpenTimeCondition(obligation))
                return Error("invalid-obligation-wake", "wakeObligationId", "Choose an active unsatisfied time condition of this goal.") with
                { Expected = "An unsatisfied time condition ID of the target goal", Actual = wake.ToString() };
            if (obligation.Check is { } check && (check.Observed || IsDueCheck(obligation, check, context.Trigger)))
                return Error("time-check-already-observed", "wakeObligationId",
                    $"Condition {wake} already has matching due-check evidence {check.Id}. Assess it, or schedule an independent check for further work.") with
                { Expected = "An unsatisfied time condition without due-check evidence", Actual = wake.ToString() };
            if (decision.WakeAfterSeconds < obligation.DelaySeconds)
                return Error("wake-shorter-than-condition", "wakeAfterSeconds", $"Condition {wake} requires a minimum delay of {obligation.DelaySeconds} seconds.") with
                { Expected = $"At least {obligation.DelaySeconds} seconds", Actual = decision.WakeAfterSeconds.ToString() };
        }
        var unscheduled = target is null ? [] : UnscheduledTimeConditions(target, context.Trigger);
        bool independentAction = decision.Action == BotAction.Delegate ||
            decision is { Action: BotAction.Wait, WakeAfterSeconds: not null, WakeObligationId: null };
        if (decision.GoalWakeDeferralReason is not null && (!independentAction || unscheduled.Length == 0 || !Text(decision.GoalWakeDeferralReason, 1000)))
            return Error("invalid-wake-deferral", "goalWakeDeferralReason",
                "A delegation or independent timed Wait can explain why recorded time conditions remain unscheduled.") with
            { Expected = "A nonempty explanation of at most 1000 characters on Delegate or an independent timed Wait",
                Actual = $"{decision.Action}; reason length {decision.GoalWakeDeferralReason.Length}" };
        if (independentAction && unscheduled.Length > 0 && decision.GoalWakeDeferralReason is null)
            return Error("time-condition-disposition-required", "goalWakeDeferralReason",
                $"Goal {target!.Id} v{target.Goal.Version} has unreserved time conditions: {string.Join(", ", unscheduled.Select(o => $"{o.Id} ({o.DelaySeconds}s)"))}. " +
                "Choose a bound timed Wait if the starting event has happened. If other work comes first, explain what still needs to happen before scheduling.") with
            { Expected = "Timed Wait with wakeObligationId and wakeAfterSeconds, or a reasoned deferral", Actual = $"{decision.Action}; no deferral reason" };

        if (assessments.Length > MaxEditsPerDecision || assessments.Any(a => a is null) ||
            assessments.Select(a => a.Id).Distinct().Count() != assessments.Length)
            return Error("invalid-obligation-assessments", "obligationAssessments", "Assess each distinct obligation at most once within the bounded list.");
        if (assessments.Length > 0 && (target is null || decision.Action is not (BotAction.Delegate or BotAction.Wait or BotAction.Complete)))
            return Error("obligation-assessment-action", "obligationAssessments", "Assess existing obligations when choosing Delegate, Wait or Complete.");
        for (int index = 0; index < assessments.Length; index++)
        {
            var assessment = assessments[index];
            var obligation = obligations?.FirstOrDefault(o => o.Id == assessment.Id && o.GoalId == target!.Id);
            if (obligation is not { Active: true } || assessment.Id == decision.WakeObligationId ||
                assessment.Status is not (GoalObligationStatus.Pending or GoalObligationStatus.InProgress or GoalObligationStatus.Unknown or GoalObligationStatus.Satisfied) ||
                !Text(assessment.Reason, 1000))
                return Error("invalid-obligation-assessment", "obligationAssessments", "Assess an active condition of this goal and explain the evidence.");
            if (assessment.Status == GoalObligationStatus.Satisfied &&
                CheckAssessmentEvidence(assessment, obligation, decision, context, target!, index) is { } conflict)
                return conflict;
            if (assessment.Status != GoalObligationStatus.Satisfied &&
                (assessment.CompletionNotice || assessment.ExpectedToolStatus is not null))
                return Error("invalid-obligation-evidence", "obligationAssessments", "An expected tool outcome or completion notice must support a satisfied condition.");
        }

        if (required && decision.Action == BotAction.Complete)
        {
            if (pending.Length > 0)
                return Error("unreviewed-user-source", "action", "Review the pending user source before completing the goal.") with
                {
                    Expected = "no unreviewed user source sequences",
                    Actual = string.Join(", ", pending.Take(12))
                };
            if (obligations is null)
                return Error("legacy-obligations-required", "obligationEdits", "First capture the conditions from the original goal sources on a non-completion decision.");
            var open = obligations.Where(o => o.Active &&
                (assessments.FirstOrDefault(a => a.Id == o.Id)?.Status ??
                    (context.UnavailableObligationEvidence.Contains(o.Id) ? GoalObligationStatus.Unknown : o.Status))
                != GoalObligationStatus.Satisfied).ToArray();
            if (open.Length > 0)
                return Error("unsatisfied-obligations", "obligationAssessments",
                    "The goal still has open conditions. Continue with the next action that can obtain the missing evidence; consult the requirements and event query if needed.") with
                {
                    Expected = "every active condition satisfied by scoped evidence",
                    Actual = string.Join("; ", open.Take(8).Select(o =>
                        $"{o.Id} ({o.Kind}, source #{o.SourceSequence}, " +
                        $"{assessments.FirstOrDefault(a => a.Id == o.Id)?.Status ?? (context.UnavailableObligationEvidence.Contains(o.Id) ? GoalObligationStatus.Unknown : o.Status)})"))
                };
        }
        return null;
    }

    private static BotDecisionError? CheckEdits(GoalObligationEdit[] edits, BotDecisionContext context, BotGoalSnapshot? target,
        bool creating, bool revising, bool initializing, long? userSequence, long? resolvedSource)
    {
        if (!(creating || revising || initializing) || (creating || revising) && userSequence is null)
            return Error("obligation-edits-require-source", "obligationEdits", "Change the user's conditions through a source-bound goal creation or revision.");
        var old = target?.Goal.Obligations ?? [];
        if (old.Length + edits.Count(e => e.Action is GoalObligationEditKind.Add or GoalObligationEditKind.Replace) > MaxObligations)
            return Error("obligation-limit", "obligationEdits", "The bounded goal condition history has reached its limit.");
        var seen = new HashSet<Guid>();
        var sources = context.ObligationSources.Where(s => s.GoalId == target?.Id).Select(s => s.Sequence).ToHashSet();
        foreach (var edit in edits)
        {
            if (!Enum.IsDefined(edit.Action))
                return Error("invalid-obligation-edit", "obligationEdits", "Use Add, Replace or Withdraw.");
            if (edit.Action == GoalObligationEditKind.Add ? edit.Id.HasValue :
                edit.Id is not Guid id || !revising || !old.Any(o => o.Id == id && o.GoalId == target?.Id && o.Active) ||
                !seen.Add(id) || !Text(edit.Reason, 1000))
                return Error("invalid-obligation-edit", "obligationEdits",
                    "Add has no existing ID. Replace and Withdraw need one active condition of this same goal and a user-sourced reason.");
            if (edit.Action == GoalObligationEditKind.Withdraw)
            {
                if (edit.Kind is not null || edit.Description is not null || edit.DelaySeconds is not null)
                    return Error("invalid-obligation-withdrawal", "obligationEdits", "A withdrawal identifies the existing condition and the reason.");
            }
            else if (edit.Kind is not { } kind || !Enum.IsDefined(kind) || !Text(edit.Description, 1500) ||
                (kind == GoalObligationKind.Time
                    ? edit.DelaySeconds is not (>= BotDecision.MinWakeSeconds and <= BotDecision.MaxWakeSeconds)
                    : edit.DelaySeconds is not null))
                return Error("invalid-obligation-description", "obligationEdits",
                    "Record a distinct description and kind. A time condition needs the required delay in the supported range.");

            if (edit.SourceSequence is long sequence && (sequence <= 0 || sequence != userSequence && sequence != resolvedSource &&
                !(initializing && edit.Action == GoalObligationEditKind.Add && sources.Contains(sequence))))
                return Error("foreign-obligation-source", "obligationEdits", "Use the current revision source or a displayed source of this goal when initializing.");
        }
        if (initializing)
        {
            if (target!.SourceSequence is long initial) sources.Add(initial);
            var covered = edits.Select(e => e.SourceSequence ?? (revising ? userSequence : target.SourceSequence)).ToHashSet();
            if (sources.Count == 0 || sources.Any(s => !covered.Contains(s)))
                return Error("legacy-source-coverage", "obligationEdits", "Initialize conditions for every displayed prior goal and revision source.");
        }
        if (revising && !edits.Any(e => e.SourceSequence is null || e.SourceSequence == userSequence))
            return Error("revision-source-coverage", "obligationEdits", "Record the changes from the current user message.");
        if (resolvedSource is long pending && !edits.Any(e => e.SourceSequence == pending))
            return Error("pending-source-coverage", "obligationEdits", "A revision resolving a pending source must include its effects.");
        return null;
    }

    private static BotDecisionError? CheckAssessmentEvidence(GoalObligationAssessment assessment, GoalObligation obligation,
        BotDecision decision, BotDecisionContext context, BotGoalSnapshot goal, int index)
    {
        if (obligation.Kind == GoalObligationKind.Notice || assessment.CompletionNotice)
        {
            bool valid = assessment.CompletionNotice && obligation.Kind == GoalObligationKind.Notice && decision.Action == BotAction.Complete &&
                context.AutoFinalReport && Text(decision.Message, 16000) &&
                assessment.DispatchId is null && assessment.EventId is null && assessment.CheckId is null &&
                assessment.ExpectedToolStatus is null;
            return valid ? null : Conflict("completionNotice",
                "Submit a notice condition with completionNotice alone in a Complete action that has a message and automatic reporting enabled.",
                "a notice condition, completionNotice=true, and no dispatchId, eventId, checkId or expectedToolStatus",
                $"{obligation.Kind}; {decision.Action}; autoFinalReport={context.AutoFinalReport}; hasMessage={Text(decision.Message, 16000)}; " +
                $"completionNotice={assessment.CompletionNotice}; dispatchId={assessment.DispatchId}; eventId={assessment.EventId}; " +
                $"checkId={assessment.CheckId}; expectedToolStatus={assessment.ExpectedToolStatus}");
        }
        if (obligation.Kind == GoalObligationKind.Time)
        {
            bool valid = assessment.DispatchId is null && assessment.EventId is null && assessment.CheckId is Guid checkId &&
                assessment.ExpectedToolStatus is null &&
                obligation.Check is { } check && check.Id == checkId && check.GoalVersion >= obligation.CreatedVersion &&
                (check.Observed || IsDueCheck(obligation, check, context.Trigger));
            return valid ? null : Conflict("checkId",
                "This time condition needs its own observed, due check. Keep it open and schedule Wait when the required delay should start.",
                "the condition's observed checkId only",
                $"submitted checkId={assessment.CheckId}, dispatchId={assessment.DispatchId}, eventId={assessment.EventId}; " +
                (obligation.Check is { } scheduled
                    ? $"scheduled={scheduled.Id}, dueAt={scheduled.DueAtUnixMilliseconds}, version={scheduled.GoalVersion}, observed={scheduled.Observed}"
                    : "no check registered"));
        }
        if (assessment.CheckId is not null || assessment.DispatchId is not Guid dispatchId || assessment.EventId is not > 0)
            return Conflict(assessment.CheckId.HasValue ? "checkId" : assessment.DispatchId.HasValue ? "eventId" : "dispatchId",
                "Cite a specific evidence event from this goal, or leave the condition open and seek evidence.",
                "dispatchId and positive eventId for a tool outcome or report, with no checkId",
                $"dispatchId={assessment.DispatchId}; eventId={assessment.EventId}; checkId={assessment.CheckId}");
        var dispatch = context.DispatchEvidence.FirstOrDefault(g => g.GoalId == goal.Id && g.GoalVersion == goal.Goal.Version)?
            .Dispatches.FirstOrDefault(d => d.DispatchId == dispatchId);
        if (dispatch is not { EvidenceState: "observed", SubmissionStarted: not false, Execution: not ("uncertain" or "not-submitted") })
            return Conflict("dispatchId", "Use a submitted dispatch with an observed outcome from this goal, or obtain new evidence.",
                $"an observed dispatch of goal {goal.Id} at version {goal.Goal.Version}",
                $"dispatch={dispatchId}; execution={dispatch?.Execution}; evidenceState={dispatch?.EvidenceState}");
        var fact = FindEvidenceEvent(dispatch, assessment.EventId.Value);
        if (fact is null || fact.GoalVersion != dispatch.GoalVersion ||
            fact.GoalVersion is not int version || version < obligation.CreatedVersion || version > goal.Goal.Version)
            return Conflict("eventId", "The event is outside this condition's scope. Query the dispatch if the recorded event is missing from this view.",
                $"an event of dispatch {dispatchId} executed within versions {obligation.CreatedVersion} through {goal.Goal.Version}",
                $"event={assessment.EventId}; eventVersion={fact?.GoalVersion}; dispatchVersion={dispatch.GoalVersion}");
        if (SupportsRecordedEvidence(obligation, assessment, dispatch, goal.Goal.Version)) return null;
        string actual = $"event={fact.EventId}; source={fact.Source}; status={fact.Status}; toolId={fact.ToolId}; " +
            $"laterFailure={fact.SupersededByEventId}; reportedFailures={dispatch.ReportedFailedTools}; " +
            $"receiptTruncated={dispatch.ReceiptToolsTruncated}; expectedToolStatus={assessment.ExpectedToolStatus}";
        if (fact.EvidenceKind == BotEvidenceKind.Counterexample)
            return Conflict("eventId", "This event establishes a failed probe or denied action. Leave the result open and take the next authorized action to obtain it. An expected failed probe may support only a behavior or constraint explicitly requiring that outcome.",
                "a positive fact for a result, or a failed tool event with expectedToolStatus=failed for a required failure observation", actual);
        return Conflict("eventId", "This reference does not provide the claimed outcome. Seek a corroborating tool result or continue the unfinished work.",
            "a completed tool outcome without a later conflicting failure, or a returned report with no failed or truncated tool evidence", actual);

        BotDecisionError Conflict(string field, string message, string expected, string actual) =>
            Error("obligation-evidence-missing", $"obligationAssessments[{index}].{field}",
                $"Condition {obligation.Id} ({obligation.Kind}, source #{obligation.SourceSequence}). {message}") with
            { Expected = expected, Actual = actual };
    }

    private static BotEvidenceFact? FindEvidenceEvent(BotDispatchEvidence dispatch, long eventId) =>
        dispatch.Receipt?.EventId == eventId ? dispatch.Receipt : dispatch.ToolEvidence.FirstOrDefault(fact => fact.EventId == eventId);

    public static bool SupportsRecordedEvidence(GoalObligation obligation, GoalObligationAssessment assessment,
        BotDispatchEvidence dispatch, int currentVersion)
    {
        if (assessment.EventId is not long eventId || assessment.DispatchId != dispatch.DispatchId ||
            dispatch is not { EvidenceState: "observed", SubmissionStarted: not false, Execution: not ("uncertain" or "not-submitted") } ||
            FindEvidenceEvent(dispatch, eventId) is not { } fact || fact.GoalVersion is not int version ||
            version != dispatch.GoalVersion || version < obligation.CreatedVersion || version > currentVersion) return false;
        if (assessment.ExpectedToolStatus is not null)
            return assessment.ExpectedToolStatus == "failed" && obligation.Kind is GoalObligationKind.Behavior or GoalObligationKind.Constraint &&
                fact is { Source: BotEventKind.ToolResult, Status: "failed" };
        if (fact.EvidenceKind == BotEvidenceKind.ToolOutcome)
            return fact.SupersededByEventId is null && !dispatch.ToolEvidence.Any(other => fact.ToolId is not null &&
                other.ToolId == fact.ToolId && other.EventId > fact.EventId && other.EvidenceKind == BotEvidenceKind.Counterexample);
        return fact.EvidenceKind == BotEvidenceKind.HarnessReport && dispatch.ReceiptToolsTruncated != true &&
            dispatch.ReportedFailedTools is not > 0 && !dispatch.ToolEvidence.Any(other => other.EvidenceKind == BotEvidenceKind.Counterexample);
    }

    private static bool IsDueCheck(GoalObligation obligation, GoalObligationCheck check, BotObservation trigger) =>
        trigger is { Kind: BotEventKind.Timer, Status: "due" } && trigger.GoalId == obligation.GoalId &&
        trigger.WakeObligationId == obligation.Id && trigger.CheckId == check.Id &&
        trigger.GoalVersion == check.GoalVersion && trigger.WakeAtUnixMilliseconds == check.DueAtUnixMilliseconds;

    // Called after the goal's lifecycle version has been selected, inside the same commit transaction.
    public static ContinuousGoal Apply(ContinuousGoal goal, Guid goalId, long sourceSequence, BotDecision decision,
        BotObservation trigger, GoalObligationCheck? scheduledCheck = null)
    {
        var edits = decision.ObligationEdits ?? [];
        var assessments = decision.ObligationAssessments ?? [];
        if (goal.Obligations is not null || edits.Length > 0)
        {
            var obligations = (goal.Obligations ?? []).ToList();
            foreach (var edit in edits)
            {
                var source = edit.SourceSequence ?? sourceSequence;
                if (edit.Id is Guid previous)
                {
                    int index = obligations.FindIndex(o => o.Id == previous);
                    var status = edit.Action == GoalObligationEditKind.Replace ? GoalObligationStatus.Superseded : GoalObligationStatus.Withdrawn;
                    obligations[index] = obligations[index] with { Status = status, UpdatedVersion = goal.Version,
                        ChangeSourceSequence = source, ChangeReason = edit.Reason };
                }
                if (edit.Action != GoalObligationEditKind.Withdraw)
                    obligations.Add(new(Guid.NewGuid(), goalId, goal.Version, source, edit.Kind!.Value, edit.Description!,
                        DelaySeconds: edit.DelaySeconds, Replaces: edit.Id, UpdatedVersion: goal.Version,
                        ChangeSourceSequence: source, ChangeReason: edit.Reason));
            }
            goal = goal with { Obligations = obligations.Select(Advance).ToArray() };
        }
        if (decision.ResolveSourceSequence is long resolved && goal.PendingRequirementSources is { } pending)
            goal = goal with { PendingRequirementSources = pending.Where(s => s != resolved).ToArray() };
        return goal;

        GoalObligation Advance(GoalObligation obligation)
        {
            if (!obligation.Active) return obligation;
            if (obligation.Check is { Observed: false } check)
            {
                if (IsDueCheck(obligation, check, trigger))
                    obligation = obligation with { Check = check with { Observed = true }, UpdatedVersion = goal.Version };
                else if (check.GoalVersion != goal.Version)
                    obligation = obligation with { Check = null, Status = GoalObligationStatus.Pending, UpdatedVersion = goal.Version };
            }
            if (decision.WakeObligationId == obligation.Id && scheduledCheck is not null)
                return obligation with { Check = scheduledCheck, Status = GoalObligationStatus.InProgress,
                    Assessment = null, UpdatedVersion = goal.Version };
            var assessment = assessments.FirstOrDefault(a => a.Id == obligation.Id);
            return assessment is null ? obligation : obligation with
            {
                Status = assessment.Status, Assessment = assessment, UpdatedVersion = goal.Version
            };
        }
    }

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static BotDecisionError Error(string code, string field, string message) => new(code, "obligation-conflict", message, field);
}
