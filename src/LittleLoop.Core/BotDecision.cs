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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WakeAtUnixMilliseconds = null);

/// <summary>Counts describe the last reported state of each recorded call. Null truncation means unknown coverage.</summary>
public sealed record BotToolEvidence(int Recorded, int ReportedCompleted, int ReportedFailed, bool? Truncated,
    BotToolFailure? FirstFailure, BotToolFailure? FirstOmittedFailure);

public sealed record BotToolFailure(string ToolId, string ToolKind, string Status, string Detail);

public sealed record BotGoalSnapshot(Guid Id, ContinuousGoal Goal, int RemainingTurns,
    int RemainingWakeups = BotDecision.MaxWakeups, long? WakeAtUnixMilliseconds = null);

public sealed record BotDecisionContext(BotObservation Trigger, BotGoalSnapshot[] Goals, BotObservation[] History,
    string[] Memory, string[] Harnesses, bool AutoFinalReport = true, Guid? ReplyGoalId = null, string? QuotedText = null,
    IReadOnlyDictionary<string, string>? Capabilities = null)
{
    public BotGoalEvidence[] DispatchEvidence { get; init; } = [];
    public BotHarnessOption[] HarnessOptions { get; init; } = [];

    [JsonIgnore]
    public Action<BotModelCall>? ObserveModelCall { get; init; }
    [JsonIgnore]
    public Func<CancellationToken, Task<BotDecisionError?>>? CheckFreshness { get; init; }
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewReason = null)
{
    public const int MinWakeSeconds = 5;
    public const int MaxWakeSeconds = 7 * 24 * 60 * 60;
    public const int MaxWakeups = 32;

    public BotDecision Validate(BotDecisionContext context) =>
        Check(context) is { } error ? throw new InvalidDataException(error.Message) : this;

    public BotDecisionError? Check(BotDecisionContext context)
    {
        var target = GoalId is null ? null : context.Goals.FirstOrDefault(g => g.Id == GoalId);
        if (GoalId is not null && target is null)
            return new("unknown-goal", "field", "Decision refers to an unknown or unlisted goal.", "goalId");
        bool readingLocalResult = context.Trigger is { Kind: BotEventKind.ToolResult, GoalId: null } &&
            Action == BotAction.Reply && GoalId is null;
        if (context.Trigger.Kind != BotEventKind.UserMessage && !readingLocalResult)
        {
            if (GoalId is null || GoalId != context.Trigger.GoalId)
                return new("wrong-trigger-goal", "field", "A goal observation requires a next action for the same goal.", "goalId");
            if (Action is not (BotAction.Delegate or BotAction.Wait or BotAction.Complete))
                return new("invalid-trigger-action", "field", "A goal observation accepts Delegate, Wait or Complete for the same goal.", "action");
        }
        bool dueTimer = DueTimer(context, target);
        if (context.Trigger.Kind == BotEventKind.Timer && !dueTimer)
            return new("stale-timer", "goal-version-conflict", "A timer observation must match the current goal's scheduled check.", "goalId");
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
        if ((Harness is not null && Action != BotAction.Delegate) ||
            (Objective is not null && (Action != BotAction.Delegate || GoalId is not null)) ||
            (Instruction is not null && Action is not (BotAction.Delegate or BotAction.Revise or BotAction.Remember)) ||
            (MemorySource is not null && Action is not (BotAction.Remember or BotAction.Forget)) ||
            (Enabled is not null && Action != BotAction.SetFinalReport))
            return new("extraneous-fields", "field", "Decision contains fields for a different action.");

        if (Action == BotAction.Delegate)
        {
            if (string.IsNullOrWhiteSpace(Harness))
                return new("missing-harness", "harness-reference", "Delegate requires the exact ID of a listed harness.", "harness");
            if (!context.Harnesses.Contains(Harness, StringComparer.Ordinal))
            {
                if (context.HarnessOptions.Any(h => h.Id == Harness && !h.CanQueue))
                    return new("unavailable-harness", "harness-reference", "The selected Harness cannot be queued. Choose a currently available ID.", "harness");
                return new("unknown-harness", "harness-reference", "The selected Harness ID is not listed.", "harness");
            }
            if (!HasText(Instruction, 16000))
                return new("missing-instruction", "field", "Delegate requires a nonempty instruction (maximum 16000 characters).", "instruction");
            if (target is null && (!HasText(Objective, 4000) || Message is null))
                return new("missing-new-goal-fields", "field", "A new delegation requires both an objective (maximum 4000 characters) and a brief plan in message.");
            if (target?.RemainingTurns <= 0)
                return new("turn-budget-exhausted", "field", "This goal has no remaining harness turns.", "action");
            return CheckEvidence(context);
        }
        bool valid = Action switch
        {
            BotAction.Reply => Message is not null,
            BotAction.Revise => target is not null && HasText(Instruction, 4000),
            BotAction.Cancel or BotAction.Pause or BotAction.Resume or BotAction.Complete => target is not null,
            BotAction.Wait => target is not null || Message is not null,
            BotAction.Remember => target is null && HasText(Instruction, 4000),
            BotAction.Forget => target is null && MemorySource > 0,
            BotAction.SetFinalReport => target is null && Enabled.HasValue,
            BotAction.ManageMemory => target is null,
            _ => false
        };
        return valid ? CheckEvidence(context) : new("missing-action-fields", "field", $"Invalid target or required fields for {Action}.");
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
        if (target is not null) return Enum.GetValues<BotAction>().Where(Allowed).ToArray();
        return context.Harnesses.Length == 0
            ? [BotAction.Reply, BotAction.Wait, BotAction.Remember, BotAction.Forget, BotAction.SetFinalReport, BotAction.ManageMemory]
            : [BotAction.Reply, BotAction.Wait, BotAction.Remember, BotAction.Forget, BotAction.SetFinalReport, BotAction.ManageMemory, BotAction.Delegate];

        bool Allowed(BotAction action) => target is not null &&
            (AllowsAction(target.Goal.Status, action) || action == BotAction.Delegate && DueTimer(context, target)) &&
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
