using LittleLoop.Core;

namespace LittleLoop.Plugins.Example;

// Deliberately explicit rules: these facts are observations, not established truth.
public sealed class ReviewPlugin : IDecisionPlugin
{
    public PluginManifest Manifest => new("example.review", "1.0.0");
    public IReadOnlyList<Proposal> Propose(DeliberationInput input)
    {
        if (input.Facts.GetValueOrDefault("goalConflict") is { Length: > 0 } conflict)
            return [new("refuse", ChoiceKind.Refuse, "Decline the requested goal", 1, 0,
                $"Observed goal conflict: {conflict}")];
        if (input.Facts.GetValueOrDefault("premiseConflict") is { Length: > 0 } premise)
            return [new("reframe", ChoiceKind.Reframe, "Recheck the premise before choosing a route", .95m, .05m,
                $"Observed premise conflict: {premise}")];
        if (input.Facts.GetValueOrDefault("alternativeRoute") is { Length: > 0 } route)
            return [new("alternative", ChoiceKind.Act, $"Route to {route}", .9m, .1m,
                "Alternative route supplied by the recorded observation.", "ticket.route", 1)];
        return [];
    }
}
