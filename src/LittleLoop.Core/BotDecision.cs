using System.Text.Json.Serialization;

namespace LittleLoop.Core;

[JsonConverter(typeof(JsonStringEnumConverter<BotEventKind>))]
public enum BotEventKind { UserMessage, BotDecision, HarnessReceipt, ToolResult, Timer, DecisionAudit }
[JsonConverter(typeof(JsonStringEnumConverter<BotAction>))]
public enum BotAction { Reply, Delegate, Revise, Pause, Resume, Cancel, Wait, Complete, Remember, SetFinalReport, ManageMemory, Forget }

public sealed record BotObservation(BotEventKind Kind, string Text, Guid? GoalId = null, string? Harness = null,
    string? Status = null, long? Sequence = null, int? GoalVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? DispatchId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BotToolEvidence? ToolEvidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DispatchSubmitted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WakeAtUnixMilliseconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CheckId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? WakeObligationId = null);

/// <summary>Counts describe the last reported state of each recorded call. Null truncation means unknown coverage.</summary>
public sealed record BotToolEvidence(int Recorded, int ReportedCompleted, int ReportedFailed, bool? Truncated,
    BotToolFailure? FirstFailure, BotToolFailure? FirstOmittedFailure);

public sealed record BotToolFailure(string ToolId, string ToolKind, string Status, string Detail);

public sealed record BotGoalSnapshot(Guid Id, ContinuousGoal Goal, int RemainingTurns,
    int RemainingWakeups = BotDecision.MaxWakeups, long? WakeAtUnixMilliseconds = null)
{
    public long? SourceSequence { get; init; }
}

public sealed record BotDecisionContext(BotObservation Trigger, BotGoalSnapshot[] Goals, BotObservation[] History,
    string[] Memory, string[] Harnesses, bool AutoFinalReport = true, Guid? ReplyGoalId = null, string? QuotedText = null,
    IReadOnlyDictionary<string, string>? Capabilities = null)
{
    public BotGoalEvidence[] DispatchEvidence { get; init; } = [];
    public BotHarnessOption[] HarnessOptions { get; init; } = [];
    public bool RequireObligations { get; init; }
    public GoalObligationSource[] ObligationSources { get; init; } = [];
    public Guid[] UnavailableObligationEvidence { get; init; } = [];
    public BotDecisionRecovery? Recovery { get; init; }

    [JsonIgnore]
    public Action<BotModelCall>? ObserveModelCall { get; init; }
    [JsonIgnore]
    public Func<CancellationToken, Task<BotDecisionError?>>? CheckFreshness { get; init; }
    [JsonIgnore]
    public Func<BotFactQuery, CancellationToken, Task<BotFactQueryResponse>>? ReadFacts { get; init; }
}

/// <summary>The caller supplies a scoped snapshot. Deciding does not send messages, mutate goals or invoke tools.</summary>
public interface IBotDecisionEngine
{
    Task<BotDecision> DecideAsync(BotDecisionContext context, CancellationToken token);

    async Task<BotDecisionResult> DecideWithSourceAsync(BotDecisionContext context, CancellationToken token) =>
        new(await DecideAsync(context, token), new(GetType().Name));
}

/// <summary>A delegate action with no goal creates one. Revisions and resumptions request a fresh decision.</summary>
public sealed partial record BotDecision(BotAction Action, Guid? GoalId = null, string? Message = null,
    string? Objective = null, string? Instruction = null, string? Harness = null, bool? Enabled = null, long? MemorySource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WakeAfterSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? VerifyDispatchId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReviewDispatchId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GoalObligationEdit[]? ObligationEdits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GoalObligationAssessment[]? ObligationAssessments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? WakeObligationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GoalReplyReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ResolveSourceSequence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GoalWakeDeferralReason = null)
{
    public const int MinWakeSeconds = 5;
    public const int MaxWakeSeconds = 7 * 24 * 60 * 60;
    public const int MaxWakeups = 32;

    public BotDecision Validate(BotDecisionContext context) =>
        Check(context) is { } error ? throw new InvalidDataException(error.Message) : this;

    public BotDecisionError? Check(BotDecisionContext context)
    {
        if (GoalObligationPolicy.CheckUserSource(this, context) is { } sourceError) return sourceError;
        var target = GoalId is null ? null : context.Goals.FirstOrDefault(g => g.Id == GoalId);
        if (GoalId is not null && target is null)
            return new BotDecisionError("unknown-goal", "field", "Decision refers to an unknown or unlisted goal.", "goalId")
            { Expected = "a UUID from the current goals" };
        bool readingLocalResult = context.Trigger is { Kind: BotEventKind.ToolResult, GoalId: null } &&
            Action == BotAction.Reply && GoalId is null;
        if (context.Trigger.Kind != BotEventKind.UserMessage && !readingLocalResult)
        {
            if (GoalId is null || GoalId != context.Trigger.GoalId)
                return new BotDecisionError("wrong-trigger-goal", "field", "A goal observation requires a next action for the same goal.", "goalId")
                { Expected = $"trigger goal {context.Trigger.GoalId} at version {context.Trigger.GoalVersion?.ToString() ?? "unknown"}" };
            if (Action is not (BotAction.Delegate or BotAction.Wait or BotAction.Complete))
                return new("invalid-trigger-action", "field", "A goal observation accepts Delegate, Wait or Complete for the same goal.", "action");
        }
        bool dueTimer = DueTimer(context, target);
        if (context.Trigger.Kind == BotEventKind.Timer && !dueTimer)
            return new("stale-timer", "goal-version-conflict", "A timer observation must match the current goal's scheduled check.", "goalId");
        if (Action == BotAction.Wait && !dueTimer && target is { Goal.Status: "waiting-input", WakeAtUnixMilliseconds: long scheduled })
            return new("wake-already-scheduled", "obligation-conflict",
                $"Goal {target.Id} v{target.Goal.Version} already has a wake at {scheduled} Unix milliseconds. Keep the reservation or apply an explicit user-sourced revision or control.", "action")
            { Expected = "Keep the existing wake until due or change the goal through an explicit control", Actual = nameof(BotAction.Wait) };
        if (target is not null && !(dueTimer && Action == BotAction.Delegate) && !AllowsAction(target.Goal.Status, Action))
            return target.Goal.Status == "needs-reconciliation"
                ? new("execution-unknown", "evidence-conflict", "The execution result is unknown. Reconcile it before delegating or completing.", "action")
                : new("invalid-goal-state", "field", $"Cannot apply {Action} to a goal in state '{target.Goal.Status}'.", "action");
        if (WakeAfterSeconds is int delay)
        {
            if (Action != BotAction.Wait)
                return new("wake-requires-wait", "field", "Only Wait accepts wakeAfterSeconds. Choose whether to wait or act now.", "action");
            if (target?.Goal.Status is not ("deciding" or "waiting-input") || target.RemainingWakeups <= 0)
                return new("wake-not-available", "field", "Scheduling requires an eligible goal with remaining checks.", "wakeAfterSeconds");
            if (delay is < MinWakeSeconds or > MaxWakeSeconds)
                return new("wake-out-of-range", "field", $"wakeAfterSeconds must be an integer from {MinWakeSeconds} to {MaxWakeSeconds}.", "wakeAfterSeconds");
        }
        if (Message is not null && !HasText(Message, 16000))
            return new("invalid-message", "field", "Decision has an empty or oversized message.", "message");
        if (Harness is not null && Action != BotAction.Delegate) return Extraneous("harness");
        if (Objective is not null && (Action != BotAction.Delegate || GoalId is not null)) return Extraneous("objective");
        if (Instruction is not null && Action is not (BotAction.Delegate or BotAction.Revise or BotAction.Remember))
            return Extraneous("instruction");
        if (MemorySource is not null && Action is not (BotAction.Remember or BotAction.Forget)) return Extraneous("memorySource");
        if (Enabled is not null && Action != BotAction.SetFinalReport) return Extraneous("enabled");

        if (Action == BotAction.Delegate)
        {
            if (string.IsNullOrWhiteSpace(Harness))
                return HarnessError("missing-harness", "Delegate requires the exact ID of a listed Harness.");
            if (!context.Harnesses.Contains(Harness, StringComparer.Ordinal))
            {
                if (context.HarnessOptions.Any(h => h.Id == Harness && !h.CanQueue))
                    return HarnessError("unavailable-harness", "The selected Harness cannot be queued.");
                return HarnessError("unknown-harness", "The selected Harness ID is not listed.");
            }
            if (!HasText(Instruction, 16000))
                return new("missing-instruction", "field", "Delegate requires a nonempty instruction (maximum 16000 characters).", "instruction");
            if (target is null && !HasText(Objective, 4000))
                return Missing("missing-new-goal-fields", "objective", "a nonempty objective of at most 4000 characters");
            if (target is null && Message is null)
                return Missing("missing-new-goal-fields", "message", "a brief plan for the user");
            if (target?.RemainingTurns <= 0)
                return new("turn-budget-exhausted", "field", "This goal has no remaining harness turns.", "action");
            return CheckEvidence(context) ?? GoalObligationPolicy.Check(this, context, target);
        }
        var missing = Action switch
        {
            BotAction.Reply when Message is null => ("message", "a nonempty reply"),
            BotAction.Revise or BotAction.Cancel or BotAction.Pause or BotAction.Resume or BotAction.Complete when target is null
                => ("goalId", "an existing goal UUID"),
            BotAction.Revise or BotAction.Remember when !HasText(Instruction, 4000)
                => ("instruction", "a nonempty instruction of at most 4000 characters"),
            BotAction.Wait when target is null && Message is null => ("message", "a concrete question when waiting without a goal"),
            BotAction.Remember or BotAction.Forget or BotAction.SetFinalReport or BotAction.ManageMemory when target is not null
                => ("goalId", "omitted"),
            BotAction.Forget when MemorySource is not > 0 => ("memorySource", "a positive preference source sequence"),
            BotAction.SetFinalReport when Enabled is null => ("enabled", "true or false"),
            _ => ((string, string)?)null
        };
        if (!Enum.IsDefined(Action)) return Missing("missing-action-fields", "action", "a supported action");
        return missing is { } needed ? Missing("missing-action-fields", needed.Item1, needed.Item2)
            : CheckEvidence(context) ?? GoalObligationPolicy.Check(this, context, target);

        BotDecisionError Extraneous(string field) =>
            new("extraneous-fields", "field", $"{field} does not apply to this {Action} decision.", field)
            { Expected = "omitted" };

        BotDecisionError HarnessError(string code, string message) => new(code, "harness-reference", message, "harness")
        { Expected = context.Harnesses.Length > 0 ? string.Join(" | ", context.Harnesses) : "no Harness is available" };

        BotDecisionError Missing(string code, string field, string expected) =>
            new(code, "field", $"This {Action} decision requires {field} to be {expected}.", field) { Expected = expected };
    }

    public static BotAction[] LegalActions(BotDecisionContext context, BotGoalSnapshot? target)
    {
        if (context.Trigger.Kind != BotEventKind.UserMessage)
        {
            if (context.Trigger is { Kind: BotEventKind.ToolResult, GoalId: null }) return [BotAction.Reply];
            if (target is null || target.Id != context.Trigger.GoalId ||
                context.Trigger.Kind == BotEventKind.Timer && !DueTimer(context, target)) return [];
            return new[] { BotAction.Delegate, BotAction.Wait, BotAction.Complete }.Where(Allowed).ToArray();
        }
        if (GoalObligationPolicy.DirectedUserGoal(context) is Guid directed)
        {
            target = context.Goals.First(g => g.Id == directed);
            return Enum.GetValues<BotAction>().Where(action => GoalObligationPolicy.IsDirectedUserAction(action) &&
                (Allowed(action) || action == BotAction.ManageMemory)).ToArray();
        }
        if (target is not null) return Enum.GetValues<BotAction>().Where(Allowed).ToArray();
        return context.Harnesses.Length == 0
            ? [BotAction.Reply, BotAction.Wait, BotAction.Remember, BotAction.Forget, BotAction.SetFinalReport, BotAction.ManageMemory]
            : [BotAction.Reply, BotAction.Wait, BotAction.Remember, BotAction.Forget, BotAction.SetFinalReport, BotAction.ManageMemory, BotAction.Delegate];

        bool Allowed(BotAction action) => target is not null &&
            (AllowsAction(target.Goal.Status, action) || action == BotAction.Delegate && DueTimer(context, target)) &&
            (action != BotAction.Wait || target.WakeAtUnixMilliseconds is null || DueTimer(context, target)) &&
            (action != BotAction.Delegate || target.RemainingTurns > 0 && context.Harnesses.Length > 0);
    }

    private static bool DueTimer(BotDecisionContext context, BotGoalSnapshot? target) =>
        context.Trigger.Kind == BotEventKind.Timer && target is not null &&
        context.Trigger.GoalVersion == target.Goal.Version && context.Trigger.WakeAtUnixMilliseconds is > 0 &&
        context.Trigger.WakeAtUnixMilliseconds == target.WakeAtUnixMilliseconds && target.Goal.Status == "waiting-input";

    private static bool HasText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max;

    private static bool AllowsAction(string status, BotAction action) => action switch
    {
        BotAction.Reply => true,
        // Queued and running goals already have a dispatched step. Only a decision boundary may schedule the next one.
        BotAction.Delegate => status == "deciding",
        BotAction.Wait when status == "needs-reconciliation" => true,
        BotAction.Complete or BotAction.Wait => status is "deciding" or "waiting-input",
        BotAction.Resume => status is "paused" or "waiting-input",
        BotAction.Pause => status is "queued" or "running" or "deciding" or "waiting-input",
        BotAction.Cancel when status == "needs-reconciliation" => true,
        BotAction.Cancel or BotAction.Revise => status is "queued" or "running" or "deciding" or "waiting-input" or "paused",
        _ => false
    };
}
