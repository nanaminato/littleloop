using System.Text.Json.Serialization;

namespace LittleLoop.Core;

[JsonConverter(typeof(GoalObligationEnumConverter<BotFactQueryKind>))]
public enum BotFactQueryKind { Requirements, Events }

public sealed record BotFactQuery(BotFactQueryKind Kind, Guid GoalId, Guid? DispatchId = null,
    long? BeforeEventId = null, int? Offset = null);

public sealed record BotFactQueryResponse(BotFactQuery Query, int? GoalVersion = null)
{
    public BotDecisionError? Error { get; init; }
    public string? Objective { get; init; }
    public BotRequirementFact[]? Conditions { get; init; }
    public GoalObligationSource[]? Sources { get; init; }
    public long[]? PendingSourceSequences { get; init; }
    public int? OpenConditionCount { get; init; }
    public int? NextOffset { get; init; }
    public BotScopedFact[]? Events { get; init; }
    public long? NextBeforeEventId { get; init; }
    [JsonIgnore]
    public BotGoalEvidence? QueriedEvidence { get; init; }
}

public sealed record BotRequirementFact(Guid Id, GoalObligationKind Kind, string Description, long SourceSequence,
    int CreatedVersion, GoalObligationStatus Status, int? DelaySeconds, GoalObligationCheck? Check,
    Guid? AssessedDispatchId, long? AssessedEventId);

public sealed record BotScopedFact(BotEvidenceFact Fact, Guid? DispatchId, string? Harness, long? SourceSequence,
    Guid? CheckId = null, Guid? WakeObligationId = null, long? WakeAtUnixMilliseconds = null, Guid? ReviewDispatchId = null);

public static class BotFactContext
{
    public static BotDecisionContext WithQueriedEvidence(this BotDecisionContext context,
        IEnumerable<BotFactQueryResponse> responses)
    {
        var evidence = context.DispatchEvidence;
        foreach (var response in responses)
        {
            if (response.QueriedEvidence is not { } added) continue;
            var existing = evidence.FirstOrDefault(group => group.GoalId == added.GoalId && group.GoalVersion == added.GoalVersion);
            var merged = existing is null ? added : MergeEvidence(existing, added);
            evidence = [.. evidence.Where(group => group.GoalId != added.GoalId), merged];
        }
        return context with { DispatchEvidence = evidence };
    }

    public static BotGoalEvidence MergeEvidence(BotGoalEvidence current, BotGoalEvidence extra)
    {
        var dispatches = current.Dispatches.Concat(extra.Dispatches).GroupBy(dispatch => dispatch.DispatchId)
            .Select(group => group.Last() with
            {
                ToolEvidence = group.SelectMany(dispatch => dispatch.ToolEvidence).GroupBy(fact => fact.EventId)
                    .Select(facts => facts.Last() with { SupersededByEventId = facts.Max(fact => fact.SupersededByEventId) }).ToArray(),
                Verification = group.Select(dispatch => dispatch.Verification).OfType<BotEvidenceVerification>()
                    .MaxBy(verification => verification.DecisionEventId)
            }).ToArray();
        return current with { Dispatches = dispatches };
    }
}
