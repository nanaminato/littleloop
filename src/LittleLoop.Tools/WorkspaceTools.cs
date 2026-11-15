using LittleLoop.Core;

namespace LittleLoop.Tools;

public sealed record WorkspaceToolPolicy(string Root, string[] AllowedTools, int MaxFileBytes = 65536, int TimeoutMilliseconds = 10000);
public sealed record ToolDescriptor(string Id, string Version, string Description, string[] Parameters,
    string[] Outcomes, string[] Traits, int Cost, decimal MinimumRisk);

public static class WorkspaceTools
{
    private static readonly ToolDescriptor[] Definitions = [
        new("text.read", "1", "Read a UTF-8 file", ["source"], ["text.read"], ["read-only"], 1, .01m),
        new("text.create", "1", "Create a UTF-8 file from task content", ["target", "content"], ["file.created", "text.written"], [], 1, .1m),
        new("text.copy", "1", "Copy UTF-8 text without changing layout", ["source", "target"], ["file.created", "text.copied"], ["preserve-layout"], 1, .1m),
        new("text.normalize-lines", "1", "Normalize line endings and trim trailing spaces", ["source", "target"], ["file.created", "text.normalized"], ["normalize-layout"], 1, .1m)
    ];
    public static ToolDescriptor[] List() => Definitions.Select(d => d with
    { Parameters = [.. d.Parameters], Outcomes = [.. d.Outcomes], Traits = [.. d.Traits] }).ToArray();
}

// Supplies known executable recipes; the core still judges role/goal/premise constraints.
public sealed class WorkspaceToolPlugin(IEnumerable<string> enabledTools) : IDecisionPlugin
{
    private readonly HashSet<string> enabled = new(enabledTools, StringComparer.Ordinal);
    public PluginManifest Manifest => new("workspace.tools", "1");
    public IReadOnlyList<Proposal> Propose(DeliberationInput input)
    {
        if (input.Cognition is null || input.ToolArguments is null) return [];
        return WorkspaceTools.List().Where(d => enabled.Contains(d.Id) && input.AllowedCapabilities.Contains(d.Id, StringComparer.Ordinal) &&
            d.Parameters.All(input.ToolArguments.ContainsKey) &&
            input.Cognition.Goals.Any(g => d.Outcomes.Contains(g.Outcome)) &&
            input.Cognition.Goals.Where(g => g.Required).All(g => d.Outcomes.Contains(g.Outcome)))
            .Select(d => new Proposal(d.Id, ChoiceKind.Act, d.Description, 0, d.MinimumRisk,
                "Known local tool recipe; outcomes must be verified by actual execution.", d.Id, d.Cost)
            {
                Meaning = new($"workspace/{d.Id}/{d.Version}", d.Outcomes, d.Traits, []),
                Tool = new(d.Id, d.Parameters.ToDictionary(p => p, p => p, StringComparer.Ordinal))
            }).ToArray();
    }
}
