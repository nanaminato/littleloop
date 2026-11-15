using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class PlanningModelChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Run()
    {
        var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "backup.txt"), "chosen  \r\n");
        DocumentGoals.Add(h.Session, new("recover", "Normalize supplied sources", "output.txt", ["missing.txt", "input.txt", "backup.txt"]));
        var model = new Planner(input =>
        {
            bool failed = input.GetProperty("failedSources").GetArrayLength() > 0;
            string source = input.GetProperty("resources").GetProperty("source").GetString()!;
            return failed && source == "input.txt" ? Plan(["read-source", "normalize", "verify-output"], "backup.txt") : Remaining(input);
        });
        var state = await h.Run(model);
        var goal = state.Goals[0];
        Check(goal.Status == GoalStatus.Completed && goal.Replans == 1 && model.Calls == 4 &&
            File.ReadAllText(Path.Combine(h.Root, "output.txt")) == "chosen\n", "model replans from actual failure and selects an authorized alternative source");
        Check(goal.PlanningReviews[1].Input.Contains("missing.txt") && goal.PlanningReviews[1].EvidenceDecisionId is not null &&
            goal.Plans.Length == 4 && goal.PlanningReviews.All(r => r.Verdict == "accepted"), "each model plan is audited against actual receipt and core approval");
        Check((await h.Run(model)).Revision == state.Revision && model.Calls == 4, "completed model-assisted goal does not invoke model or tools on restart");
        var altered = goal with { PlanningReviews = [.. goal.PlanningReviews.Skip(1)] };
        try { h.Session.SaveGoal(altered, state.Revision); throw new Exception("Audit erased"); }
        catch (ArgumentException) { Check(h.Session.Read().Goals[0].PlanningReviews.Length == 4, "recorded planning audit cannot be erased by later progress update"); }
        var compatible = ModelPlanningAdvisor.Review(goal with { Facts = ["output.created", "output.normalized"], Resources = goal.Resources }, h.Policy.AllowedTools, 1,
            new(ModelPlanningAdvisor.Version, "test", "test", "{}", "{\"disposition\":\"plan\",\"steps\":[\"verify-output\"],\"reason\":\"Verify remaining output.\"}", "pending", "test", null));
        Check(compatible.Review.Verdict == "accepted", "model may omit optional null source and question without weakening mandatory plan checks");

        var changed = new Harness();
        File.WriteAllText(Path.Combine(changed.Root, "input.txt"), "THIS IS A DRAFT\r\n");
        File.WriteAllText(Path.Combine(changed.Root, "final.txt"), "final  \r\n");
        DocumentGoals.Add(changed.Session, new("new-info", "Use supplied best source", "out.txt", ["input.txt", "final.txt"]));
        var informed = new Planner(input =>
        {
            bool draftObserved = input.GetProperty("observations").EnumerateArray().Any(o => o.GetProperty("outputExcerpt").ValueKind == JsonValueKind.String && o.GetProperty("outputExcerpt").GetString()!.Contains("DRAFT"));
            return draftObserved && input.GetProperty("resources").GetProperty("source").GetString() == "input.txt"
                ? Plan(["read-source", "normalize", "verify-output"], "final.txt") : Remaining(input);
        });
        Check((await changed.Run(informed)).Goals[0].Status == GoalStatus.Completed && File.ReadAllText(Path.Combine(changed.Root, "out.txt")) == "final\n",
            "new observed file information can change remaining plan and force rereading the selected source");

        foreach (var proposal in new[] {
            Plan(["shell.exec"]), Plan(["normalize", "verify-output"]), Plan(["read-source", "normalize"]),
            Plan(["read-source", "copy", "verify-output"]), Plan(["read-source", "normalize", "verify-output"], "../unapproved.txt") })
        {
            var denied = new Harness(); DocumentGoals.Add(denied.Session, new("denied", "Normalize", "out.txt", ["input.txt"]));
            var blocked = await denied.Run(new Planner(_ => proposal));
            Check(blocked.Goals[0].Status == GoalStatus.Blocked && blocked.Episodes.Length == 0 && !File.Exists(Path.Combine(denied.Root, "out.txt")),
                "core rejects unsafe, incomplete or goal-changing model plan before execution");
        }
        var role = new Harness(new("careful", .5m, [], [new("layout", "text.normalized", "Preserve meaningful whitespace.")]));
        DocumentGoals.Add(role.Session, new("role", "Normalize", "out.txt", ["input.txt"]));
        Check((await role.Run(new Planner(Remaining))).Goals[0].Status == GoalStatus.Refused && !File.Exists(Path.Combine(role.Root, "out.txt")),
            "role core can refuse a structurally valid model plan");

        var ask = new Harness(); DocumentGoals.Add(ask.Session, new("ask", "Normalize", "out.txt", ["input.txt"]));
        var asking = new Planner(input => input.GetProperty("facts").EnumerateArray().Any(f => f.GetString() == "source.readable") && input.GetProperty("answers").GetArrayLength() == 0
            ? new("ask", [], null, "Input needs review.", "Continue using this supplied input?") : Remaining(input));
        var waiting = await ask.Run(asking);
        Check(waiting.Goals[0].Status == GoalStatus.AwaitingClarification && waiting.Episodes.Length == 1, "new information can pause execution for a persistent planning question");
        ask.Session.AnswerGoal("ask", "Continue with the original constraints.", waiting.Revision);
        var continued = await ask.Run(asking);
        Check(continued.Goals[0].Status == GoalStatus.Completed && continued.Goals[0].PlanningAnswers.Length == 1 && continued.Episodes.Length == 3,
            "planning answer resumes only remaining steps without repeating completed tools");

        var limited = new Harness(); DocumentGoals.Add(limited.Session, new("limited", "Normalize", "out.txt", ["input.txt"]));
        Check((await limited.Run(new Planner(Remaining), maxCalls: 1)).Goals[0].Status == GoalStatus.Blocked && !File.Exists(Path.Combine(limited.Root, "out.txt")),
            "persistent model planning call budget bounds repeated reconsideration");
        var offline = new Harness(); DocumentGoals.Add(offline.Session, new("offline", "Normalize", "out.txt", ["input.txt"]));
        var unavailable = new Planner(_ => throw new SemanticModelException("timeout"));
        Check((await offline.Run(unavailable)).Goals[0].Status == GoalStatus.AwaitingClarification && offline.Session.Read().Episodes.Length == 0,
            "unavailable planner never silently falls back to execution");
        offline.Session.AnswerGoal("offline", "retry", offline.Session.Read().Revision);
        try { await new GoalOrchestrator(offline.Session, offline.Policy).RunAsync(); throw new Exception("Silent fallback"); }
        catch (ArgumentException) { Check(true, "model-reviewed goal cannot silently resume through local-only planner"); }

        var stale = new Harness(); DocumentGoals.Add(stale.Session, new("stale", "Normalize", "out.txt", ["input.txt"]));
        var racing = new Planner(input => { stale.Session.AddRuntimeBudget(1, stale.Session.Read().Revision); return Remaining(input); });
        try { await stale.Run(racing); throw new Exception("Stale planning reply accepted"); }
        catch (ArgumentException) { Check(stale.Session.Read().Episodes.Length == 0, "concurrent changes invalidate model planning reply before enqueue"); }
        Credentials();
    }
    private static void Credentials()
    {
        string root = Path.Combine(Path.GetTempPath(), "littleloop-secrets-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string name = "LITTLELOOP_SECRET_TEST_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(root, ".env.local"), "# Local test\n" + name + "='local-test-value'\nUNRELATED=ignored\n");
        Check(LocalModelSecrets.Read(name, root) == "local-test-value" && Environment.GetEnvironmentVariable(name) is null, "project credential is read without exporting arbitrary environment variables");
        Environment.SetEnvironmentVariable(name, "environment-test-value");
        try { Check(LocalModelSecrets.Read(name, root) == "environment-test-value", "process environment overrides project credential file"); }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
    private static ModelPlanCandidate Plan(string[] steps, string? source = null) => new("plan", steps, source, "Plan justified by current observations and required goal facts.", null);
    private static ModelPlanCandidate Remaining(JsonElement input)
    {
        var facts = input.GetProperty("facts").EnumerateArray().Select(f => f.GetString()).ToHashSet();
        return Plan(facts.Contains("output.created") ? ["verify-output"] : facts.Contains("source.readable") ? ["normalize", "verify-output"] : ["read-source", "normalize", "verify-output"]);
    }
    private sealed class Planner(Func<JsonElement, ModelPlanCandidate> reply) : ISemanticModelClient
    {
        public string Provider => "controlled-test";
        public string Model => "test-planner";
        public int Calls { get; private set; }
        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            using var document = JsonDocument.Parse(input);
            return Task.FromResult(new SemanticModelReply(JsonSerializer.Serialize(reply(document.RootElement), Json), Model));
        }
    }
    private sealed class Harness
    {
        public string Root { get; }
        public LearningSession Session { get; }
        public WorkspaceToolPolicy Policy { get; }
        public Harness(PersistentRole? role = null)
        {
            var parent = Path.Combine(Path.GetTempPath(), "littleloop-model-plan-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(parent, "workspace"); Directory.CreateDirectory(Root);
            Session = new(Path.Combine(parent, "state.json")); Session.Initialize(role); Session.EnableScheduling(new(2, 100));
            Policy = new(Root, WorkspaceTools.List().Select(t => t.Id).ToArray());
            File.WriteAllText(Path.Combine(Root, "input.txt"), "first  \r\nsecond\t\r\n");
        }
        public Task<LearningState> Run(ISemanticModelClient client, int maxCalls = 8) => new GoalOrchestrator(Session, Policy, new ModelPlanningAdvisor(client, maxCalls)).RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
}
