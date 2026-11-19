using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Plugins.Example;

if (args is ["--agent-command-fixture"])
{
    Environment.ExitCode = File.ReadAllText("code.cs") == "new" ? 0 : 1;
    Console.WriteLine(Environment.ExitCode == 0 ? "fixture passed" : "fixture failed");
    return;
}

var input = new DeliberationInput("test", "Route ticket", "analyst/1", .5m, 1,
    ["ticket.route"], [new("original", ChoiceKind.Act, "Route to operations", .5m, .2m, "Original suggestion", "ticket.route", 1)],
    new() { ["alternativeRoute"] = "specialist" });
var host = new PluginHost();
host.Register(new ReviewPlugin());
var trace = host.Run(input);
Check(trace.Decision.Selected.Proposal.Id == "alternative", "new option selected");
Check(!input.Options.Any(p => p.Id == "alternative"), "original options preserved");
Check(host.Run(input with { AllowedCapabilities = [] }).Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "permissions enforced");
Check(host.Run(input with { Budget = 0 }).Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "budget enforced");
Check(host.Run(input with { MaximumRisk = 0 }).Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "risk enforced");
Check(host.Run(input with { Facts = new() { ["goalConflict"] = "Task requires falsifying evidence" } }).Decision.Selected.Proposal.Kind == ChoiceKind.Refuse, "goal refusal");
Check(host.Run(input with { Facts = new() { ["premiseConflict"] = "Both offered routes assume an outage; evidence contradicts this" } }).Decision.Selected.Proposal.Kind == ChoiceKind.Reframe, "premise reconsideration");
var roundtrip = JsonSerializer.Deserialize<DeliberationTrace>(JsonSerializer.Serialize(trace))!;
Check(JsonSerializer.Serialize(DeliberationPolicy.Decide(roundtrip.Input, roundtrip.Responses)) == JsonSerializer.Serialize(trace.Decision), "replay without plugin execution");
try { host.Register(new ReviewPlugin()); throw new Exception("duplicate accepted"); } catch (ArgumentException) { }
var failing = new PluginHost();
failing.Register(new BrokenPlugin());
var failure = failing.Run(input);
Check(failure.Responses[0].Error is not null && failure.Decision.Selected.Source == "input", "plugin failure fallback");
Check(input.Facts.ContainsKey("alternativeRoute"), "plugin input mutation isolated");
var invalid = new PluginHost();
invalid.Register(new InvalidPlugin());
Check(invalid.Run(input).Responses[0].Error is not null, "invalid proposals quarantined");
foreach (var path in Directory.GetFiles("examples/phase1", "*.json"))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var oldInput = doc.RootElement.GetProperty("input").Deserialize<DecisionInput>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var decision = TicketPolicy.Decide(oldInput);
    var receipt = TicketSimulation.Execute(oldInput, decision, doc.RootElement.GetProperty("actualRoute").GetString()!);
    Check(receipt.DecisionId == oldInput.Id, $"legacy scenario {path}");
}
RoleChecks.Run();
ConversationMemoryChecks.Run();
LearningChecks.Run();
await LearningLockChecks.Run();
await SchedulingChecks.Run();
await RealToolChecks.Run();
await GoalChecks.Run();
await LanguageChecks.Run();
await ModelLanguageChecks.Run();
await PlanningModelChecks.Run();
await DevelopmentChecks.Run();
await ProjectDiscoveryChecks.Run();
await AgentChecks.Run();
await AtomicSnapshotChecks.Run();
Console.WriteLine("All checks passed.");
static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine($"PASS {name}"); }
sealed class BrokenPlugin : IDecisionPlugin
{
    public PluginManifest Manifest => new("broken", "1");
    public IReadOnlyList<Proposal> Propose(DeliberationInput input) { input.Facts.Clear(); throw new InvalidOperationException("offline"); }
}
sealed class InvalidPlugin : IDecisionPlugin
{
    public PluginManifest Manifest => new("invalid", "1");
    public IReadOnlyList<Proposal> Propose(DeliberationInput input) => [new("bad", ChoiceKind.Act, "bad", 2, 0, "bad")];
}
