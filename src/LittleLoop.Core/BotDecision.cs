using System.Text.Json.Serialization;

namespace LittleLoop.Core;

[JsonConverter(typeof(JsonStringEnumConverter<BotEventKind>))]
public enum BotEventKind { UserMessage, BotDecision, HarnessReceipt, ToolResult }
[JsonConverter(typeof(JsonStringEnumConverter<BotAction>))]
public enum BotAction { Reply, Delegate, Revise, Pause, Resume, Cancel, Wait, Complete, Remember, SetFinalReport, ManageMemory, Forget }

public sealed record BotObservation(BotEventKind Kind, string Text, Guid? GoalId = null, string? Harness = null,
    string? Status = null, long? Sequence = null, int? GoalVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? DispatchId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BotToolEvidence? ToolEvidence = null);

/// <summary>Counts describe the last reported state of each recorded call. Null truncation means unknown coverage.</summary>
public sealed record BotToolEvidence(int Recorded, int ReportedCompleted, int ReportedFailed, bool? Truncated,
    BotToolFailure? FirstFailure, BotToolFailure? FirstOmittedFailure);

public sealed record BotToolFailure(string ToolId, string ToolKind, string Status, string Detail);

public sealed record BotGoalSnapshot(Guid Id, ContinuousGoal Goal, int RemainingTurns);

public sealed record BotDecisionContext(BotObservation Trigger, BotGoalSnapshot[] Goals, BotObservation[] History,
    string[] Memory, string[] Harnesses, bool AutoFinalReport = true, Guid? ReplyGoalId = null, string? QuotedText = null,
    IReadOnlyDictionary<string, string>? Capabilities = null);

/// <summary>The caller supplies a scoped snapshot. Deciding does not send messages, mutate goals or invoke tools.</summary>
public interface IBotDecisionEngine
{
    Task<BotDecision> DecideAsync(BotDecisionContext context, CancellationToken token);

    async Task<BotDecisionResult> DecideWithSourceAsync(BotDecisionContext context, CancellationToken token) =>
        new(await DecideAsync(context, token), new(GetType().Name));
}

/// <summary>A delegate action with no goal creates one. Revisions and resumptions request a fresh decision.</summary>
public sealed record BotDecision(BotAction Action, Guid? GoalId = null, string? Message = null,
    string? Objective = null, string? Instruction = null, string? Harness = null, bool? Enabled = null, long? MemorySource = null)
{
    public BotDecision Validate(BotDecisionContext context)
    {
        var target = GoalId is null ? null : context.Goals.FirstOrDefault(g => g.Id == GoalId);
        if (GoalId is not null && target is null)
            throw new InvalidDataException("Decision refers to an unknown goal.");
        bool readingLocalResult = context.Trigger is { Kind: BotEventKind.ToolResult, GoalId: null } &&
            Action == BotAction.Reply && GoalId is null;
        if (context.Trigger.Kind != BotEventKind.UserMessage && !readingLocalResult &&
            (GoalId is null || GoalId != context.Trigger.GoalId || Action is not (BotAction.Delegate or BotAction.Wait or BotAction.Complete)))
            throw new InvalidDataException("A goal observation requires a next action for the same goal.");
        if (target is not null && !AllowsAction(target.Goal.Status, Action))
            throw new InvalidDataException($"Cannot apply {Action} to a goal in state '{target.Goal.Status}'.");
        if (Message is not null && !HasText(Message, 16000))
            throw new InvalidDataException("Decision has an empty or oversized message.");
        if ((Harness is not null && Action != BotAction.Delegate) ||
            (Objective is not null && (Action != BotAction.Delegate || GoalId is not null)) ||
            (Instruction is not null && Action is not (BotAction.Delegate or BotAction.Revise or BotAction.Remember)) ||
            (MemorySource is not null && Action is not (BotAction.Remember or BotAction.Forget)) ||
            (Enabled is not null && Action != BotAction.SetFinalReport))
            throw new InvalidDataException("Decision contains fields for a different action.");

        if (Action == BotAction.Delegate)
        {
            if (Harness is null || !context.Harnesses.Contains(Harness, StringComparer.Ordinal))
                throw new InvalidDataException("Delegate requires the exact ID of a listed harness.");
            if (!HasText(Instruction, 16000))
                throw new InvalidDataException("Delegate requires a nonempty instruction (maximum 16000 characters).");
            if (target is null && (!HasText(Objective, 4000) || Message is null))
                throw new InvalidDataException("A new delegation requires both an objective (maximum 4000 characters) and a brief plan in message.");
            if (target?.RemainingTurns <= 0)
                throw new InvalidDataException("This goal has no remaining harness turns.");
            return this;
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
        return valid ? this : throw new InvalidDataException($"Invalid target or required fields for {Action}.");
    }

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
