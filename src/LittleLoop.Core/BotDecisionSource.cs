using System.Text.Json.Serialization;

namespace LittleLoop.Core;

// Provenance belongs to the decision provider and is separate from the action proposed by a model.
public sealed record BotDecisionSource(string Engine, string? Model = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BotModelCall[]? ModelCalls { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BotDecisionError? Error { get; init; }
}

public sealed record BotDecisionResult(BotDecision Decision, BotDecisionSource Source);

public sealed record BotModelCall(string Outcome, string? Model = null, BotTokenUsage? Usage = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BotDraftAudit? Draft { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BotDecisionError? Error { get; init; }
}

public sealed record BotDecisionError(string Code, string Category, string Message, string? Field = null);
public sealed record BotDraftRepair(string Field, string Change);
public sealed record BotDraftAudit(string? Raw, int Characters, string? Sha256, BotDraftRepair[] Repairs);

// A stale or disallowed snapshot is neither a model failure nor evidence about execution.
public sealed class BotDecisionSupersededException(BotDecisionError error) : Exception(error.Message)
{
    public BotDecisionError Error => error;
}

/// <summary>Token counts returned by the service. A missing count is unknown.</summary>
public sealed record BotTokenUsage(long? InputTokens = null, long? OutputTokens = null, long? TotalTokens = null,
    long? CachedInputTokens = null, long? UncachedInputTokens = null);
