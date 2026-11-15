namespace LittleLoop.Core;

public enum ChoiceKind { Act, Ask, Reframe, Defer, Refuse }
public sealed record Proposal(string Id, ChoiceKind Kind, string Description, decimal Value,
    decimal Risk, string Reason, string? Capability = null, int Cost = 0)
{
    public ActionMeaning? Meaning { get; init; }
    public ToolCall? Tool { get; init; }
}
public sealed record DeliberationInput(string Id, string Goal, string RoleVersion, decimal MaximumRisk,
    int Budget, string[] AllowedCapabilities, Proposal[] Options, Dictionary<string, string> Facts)
{
    public RoleDeliberation? Cognition { get; init; }
    public Dictionary<string, string>? ToolArguments { get; init; }
}
public sealed record ToolCall(string Id, Dictionary<string, string> Bindings);
public sealed record PluginManifest(string Id, string Version, int ContractVersion = 1);
public interface IDecisionPlugin
{
    PluginManifest Manifest { get; }
    // Trusted in-process code. No execution authority is granted by this interface.
    IReadOnlyList<Proposal> Propose(DeliberationInput input);
}
public sealed record PluginResponse(PluginManifest Plugin, Proposal[] Proposals, string? Error);
public sealed record AssessedProposal(string Source, Proposal Proposal, bool Eligible, string Assessment);
public sealed record DeliberationDecision(string PolicyVersion, string InputId, string RoleVersion,
    AssessedProposal[] Candidates, AssessedProposal Selected)
{
    public ReasoningStep[]? Reasoning { get; init; }
}
public sealed record DeliberationTrace(DeliberationInput Input, PluginResponse[] Responses,
    DeliberationDecision Decision);

public sealed class PluginHost
{
    private readonly List<(PluginManifest Manifest, IDecisionPlugin Plugin)> plugins = [];
    public void Register(IDecisionPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var manifest = plugin.Manifest;
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.ContractVersion != 1 ||
            manifest.Id is "input" or "core" || plugins.Any(p => p.Manifest.Id == manifest.Id))
            throw new ArgumentException("Invalid, duplicate, or incompatible plugin manifest.");
        plugins.Add((manifest, plugin));
    }

    public DeliberationTrace Run(DeliberationInput input)
    {
        DeliberationPolicy.Validate(input);
        var responses = new List<PluginResponse>();
        foreach (var (manifest, plugin) in plugins)
        {
            try
            {
                // Each plugin receives its own mutable collections, never the authoritative input.
                var copy = System.Text.Json.JsonSerializer.Deserialize<DeliberationInput>(
                    System.Text.Json.JsonSerializer.Serialize(input))!;
                var proposals = plugin.Propose(copy)?.Take(65).ToArray()
                    ?? throw new ArgumentException("Null plugin response.");
                DeliberationPolicy.ValidateProposals(proposals);
                responses.Add(new(manifest, proposals, null));
            }
            catch (Exception error)
            {
                responses.Add(new(manifest, [], $"{error.GetType().Name}: {error.Message}"));
            }
        }
        return new(input, [.. responses], DeliberationPolicy.Decide(input, [.. responses]));
    }
}

public static class DeliberationPolicy
{
    public const string Version = "deliberation/1";
    public static void Validate(DeliberationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.Goal) ||
            string.IsNullOrWhiteSpace(input.RoleVersion) || input.MaximumRisk is < 0 or > 1 ||
            input.Budget < 0 || input.Options is null || input.Facts is null ||
            input.AllowedCapabilities is null || input.AllowedCapabilities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid deliberation input.");
        ValidateProposals(input.Options);
        if (input.ToolArguments is not null && input.ToolArguments.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null))
            throw new ArgumentException("Invalid task tool arguments.");
        if (input.Cognition is not null) RoleReasoner.Validate(input);
    }

    internal static void ValidateProposals(Proposal[] proposals)
    {
        if (proposals.Length > 64 || proposals.Any(p => p is null ||
            string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Description) ||
            string.IsNullOrWhiteSpace(p.Reason) || !Enum.IsDefined(p.Kind) ||
            p.Value is < -1 or > 1 || p.Risk is < 0 or > 1 || p.Cost < 0 ||
            (p.Kind == ChoiceKind.Act && string.IsNullOrWhiteSpace(p.Capability)) ||
            (p.Kind != ChoiceKind.Act && (p.Capability is not null || p.Cost != 0))) ||
            proposals.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != proposals.Length)
            throw new ArgumentException("Invalid proposals: maximum 64, unique IDs, bounded scores, and explicit action capability required.");
        foreach (var proposal in proposals)
        {
            if (proposal.Meaning is not null) RoleReasoner.ValidateMeaning(proposal.Meaning);
            if (proposal.Tool is { } tool && (proposal.Kind != ChoiceKind.Act || string.IsNullOrWhiteSpace(tool.Id) ||
                tool.Bindings is null || tool.Bindings.Count > 16 || tool.Bindings.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))))
                throw new ArgumentException("Invalid tool invocation bindings.");
        }
    }

    public static DeliberationDecision Decide(DeliberationInput input, PluginResponse[] responses)
    {
        Validate(input);
        ArgumentNullException.ThrowIfNull(responses);
        var sources = new HashSet<string>(StringComparer.Ordinal) { "input", "core" };
        var candidates = input.Options.Select(p => Assess("input", p)).ToList();
        foreach (var response in responses)
        {
            if (response?.Plugin is not { ContractVersion: 1 } manifest ||
                string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version) ||
                !sources.Add(manifest.Id) || response.Proposals is null ||
                (response.Error is not null && response.Proposals.Length != 0))
                throw new ArgumentException("Invalid recorded plugin response.");
            ValidateProposals(response.Proposals);
            candidates.AddRange(response.Proposals.Select(p => Assess(manifest.Id, p)));
        }
        candidates.Add(Assess("core", new("defer", ChoiceKind.Defer, "Wait and reconsider", 0, 0,
            "No sufficiently valuable admissible proposal is required to be selected.")));
        if (input.Cognition is not null) return RoleReasoner.Decide(input, candidates);
        var selected = candidates.Where(c => c.Eligible)
            .OrderByDescending(c => c.Proposal.Value - c.Proposal.Risk)
            .ThenBy(c => c.Source, StringComparer.Ordinal)
            .ThenBy(c => c.Proposal.Id, StringComparer.Ordinal).First();
        return new(Version, input.Id, input.RoleVersion, [.. candidates], selected);

        AssessedProposal Assess(string source, Proposal p)
        {
            string reason = p.Risk > input.MaximumRisk ? "Role risk limit exceeded."
                : p.Cost > input.Budget ? "Action budget exceeded."
                : p.Kind == ChoiceKind.Act && !input.AllowedCapabilities.Contains(p.Capability, StringComparer.Ordinal)
                    ? "Capability is not authorized." : "Admissible; ranked by value minus risk.";
            bool eligible = p.Risk <= input.MaximumRisk && p.Cost <= input.Budget &&
                (p.Kind != ChoiceKind.Act || input.AllowedCapabilities.Contains(p.Capability, StringComparer.Ordinal));
            return new(source, p, eligible, reason);
        }
    }
}
