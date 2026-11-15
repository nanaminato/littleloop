using System.Text.Json;
using LittleLoop.Core;

internal static class RoleChecks
{
    public static void Run()
    {
        Proposal fast = new("fast", ChoiceKind.Act, "Resolve immediately", 1, .1m, "Fast initial option", "ticket.route", 1)
        { Meaning = new("direct", ["resolved"], ["fast"], []) };
        Proposal careful = new("careful", ChoiceKind.Act, "Review with a specialist", -1, .1m, "Review option", "ticket.route", 1)
        { Meaning = new("review", ["resolved", "audited"], ["careful"], []) };
        var cognition = new RoleDeliberation([new("resolve", "resolved", 10, true)],
            [new("caution", "careful", 10, true)], [], [], [], []);
        var input = new DeliberationInput("role", "Resolve ticket", "careful/1", .5m, 1,
            ["ticket.route"], [fast, careful], new() { ["category"] = "billing" }) { Cognition = cognition };
        var host = new PluginHost();
        var carefulTrace = host.Run(input);
        Check(carefulTrace.Decision.Selected.Proposal.Id == "careful", "role preference overrides plugin value score");
        var fastTrace = host.Run(input with { RoleVersion = "fast/1", Cognition = cognition with { Preferences = [new("speed", "fast", 10, true)] } });
        Check(fastTrace.Decision.Selected.Proposal.Id == "fast", "same task different role preference");
        Check(carefulTrace.Decision.Reasoning!.Any(s => s.Stage == "tradeoff" && s.Conclusion.Contains("preference:caution")), "specific preference explanation");

        var goalTrace = host.Run(input with { Cognition = cognition with { Preferences = [new("speed", "fast", 10, true)],
            Goals = [new("audit", "audited", 20, false), new("resolve", "resolved", 10, true)] } });
        Check(goalTrace.Decision.Selected.Proposal.Id == "careful", "goal priority precedes preference");
        var memory = new Experience("billing-review", "ticket-42", new() { ["category"] = "billing" }, careful, true, "Specialist review resolved a similar dispute.");
        var recalled = host.Run(input with { Options = [fast], Cognition = cognition with { Experiences = [memory] } });
        Check(recalled.Decision.Selected.Proposal.Id == "recall:billing-review", "experience generates option absent from offered choices");
        Check(host.Run(input with { Options = [fast], Facts = new() { ["category"] = "outage" }, Cognition = cognition with { Experiences = [memory] } }).Decision.Selected.Proposal.Id == "fast", "unrelated experience excluded");
        var failed = memory with { Id = "failure", Succeeded = false, Action = fast, Lesson = "Direct routing failed in this context." };
        var rejected = host.Run(input with { Cognition = cognition with { Experiences = [failed] } });
        Check(rejected.Decision.Candidates.Any(x => x.Proposal.Id == "fast" && !x.Eligible && x.Assessment.Contains("experience:failure")), "past failure challenges strategy");
        var premise = new Premise("outage", "service-is-down", true, true);
        var questioned = host.Run(input with { Cognition = cognition with { Premises = [premise],
            Evidence = [new("monitor", "service-is-down", false, .95m, "service monitor")] } });
        Check(questioned.Decision.Selected.Proposal.Kind == ChoiceKind.Reframe, "core questions contradicted goal premise without plugin flags");
        Check(questioned.Decision.Reasoning!.Any(x => x.References.Contains("evidence:monitor")), "premise explanation links evidence");
        var conflict = host.Run(input with { Cognition = cognition with { Premises = [premise], Evidence = [
            new("a", "service-is-down", true, .9m, "report"), new("b", "service-is-down", false, .9m, "monitor")] } });
        Check(conflict.Decision.Selected.Proposal.Kind == ChoiceKind.Reframe, "conflicting observations remain unresolved");
        var absent = host.Run(input with { Options = [fast with { Meaning = fast.Meaning! with { Premises = ["outage"] } }],
            Cognition = cognition with { Premises = [premise with { EssentialToGoal = false }] } });
        Check(absent.Decision.Selected.Proposal.Kind == ChoiceKind.Ask, "missing action evidence prompts inquiry");
        var refused = host.Run(input with { Cognition = cognition with {
            Goals = [new("fake", "fabricated", 10, true)], Boundaries = [new("honesty", "fabricated", "Do not falsify evidence.")] } });
        Check(refused.Decision.Selected.Proposal.Kind == ChoiceKind.Refuse && refused.Decision.Selected.Proposal.Reason.Contains("honesty"), "role commitment yields explained refusal");
        Check(host.Run(input with { AllowedCapabilities = [], Cognition = cognition with { Experiences = [memory] } }).Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "recalled plan cannot bypass permission");
        var roundtrip = JsonSerializer.Deserialize<DeliberationTrace>(JsonSerializer.Serialize(recalled))!;
        Check(JsonSerializer.Serialize(DeliberationPolicy.Decide(roundtrip.Input, roundtrip.Responses)) == JsonSerializer.Serialize(recalled.Decision), "role reasoning replay deterministic");
        var malicious = new PluginHost(); malicious.Register(new MutatingPlugin());
        malicious.Run(input);
        Check(input.Cognition.Goals[0].Id == "resolve" && input.Options[0].Meaning!.Outcomes[0] == "resolved", "nested role and proposal collections isolated from plugins");
        var unsupported = host.Run(input with { Options = [fast with { Meaning = null }] });
        Check(unsupported.Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "unmodeled high-scoring action not treated as justified");
        var badOption = fast with { Meaning = fast.Meaning! with { Outcomes = ["resolved", "fabricated"] } };
        Check(host.Run(input with { Options = [badOption, careful], Cognition = cognition with {
            Boundaries = [new("honesty", "fabricated", "Preserve evidence.")] } }).Decision.Selected.Proposal.Kind == ChoiceKind.Act,
            "bad option excluded without refusing a valid goal");
        Check(host.Run(input with { Budget = 0, Cognition = cognition with { Experiences = [memory] } }).Decision.Selected.Proposal.Kind == ChoiceKind.Defer,
            "experience cannot bypass current budget");
        var weak = host.Run(input with { Cognition = cognition with { Premises = [premise],
            Evidence = [new("weak", "service-is-down", true, .2m, "rumor")] } });
        Check(weak.Decision.Selected.Proposal.Kind == ChoiceKind.Reframe, "weak evidence cannot establish essential premise");
        var supported = host.Run(input with { Cognition = cognition with { Premises = [premise],
            Evidence = [new("strong", "service-is-down", true, .95m, "monitor")] } });
        Check(supported.Decision.Selected.Proposal.Kind == ChoiceKind.Act, "supported essential premise permits action");
        var advisory = new PluginHost(); advisory.Register(new RefusalPlugin());
        Check(advisory.Run(input).Decision.Selected.Proposal.Kind == ChoiceKind.Act, "plugin score cannot force unjustified refusal");
    }
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine($"PASS {label}"); }
    private sealed class MutatingPlugin : IDecisionPlugin
    {
        public PluginManifest Manifest => new("mutator", "1");
        public IReadOnlyList<Proposal> Propose(DeliberationInput input)
        {
            input.Cognition!.Goals[0] = new("changed", "changed", 1, true);
            input.Options[0].Meaning!.Outcomes[0] = "changed";
            return [];
        }
    }
    private sealed class RefusalPlugin : IDecisionPlugin
    {
        public PluginManifest Manifest => new("refusal", "1");
        public IReadOnlyList<Proposal> Propose(DeliberationInput input) =>
            [new("refuse", ChoiceKind.Refuse, "Refuse", 1, 0, "An unsupported opinion.")];
    }
}
