using System.Text.Json.Serialization;

namespace LittleLoop.Core;

// Provenance belongs to the decision provider and is separate from the action proposed by a model.
public sealed record BotDecisionSource(string Engine, string? Model = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BotModelCall[]? ModelCalls { get; init; }
}

public sealed record BotDecisionResult(BotDecision Decision, BotDecisionSource Source);

public sealed record BotModelCall(string Outcome, string? Model = null, BotTokenUsage? Usage = null);

/// <summary>Token counts returned by the service. A missing count is unknown.</summary>
public sealed record BotTokenUsage(long? InputTokens = null, long? OutputTokens = null, long? TotalTokens = null,
    long? CachedInputTokens = null, long? UncachedInputTokens = null);
