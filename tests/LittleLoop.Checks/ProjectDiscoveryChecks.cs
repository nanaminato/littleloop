using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class ProjectDiscoveryChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Run()
    {
        string parent = Path.Combine(Path.GetTempPath(), "littleloop-discover-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "repo"); Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "labels.py"), "def label(text):\n    return text.strip().lower()\n");
        File.WriteAllText(Path.Combine(root, "tests", "test_labels.py"), "import unittest\nfrom labels import label\nclass Labels(unittest.TestCase):\n    def test_display(self):\n        self.assertEqual(label(' hello '), 'HELLO')\n");
        File.WriteAllText(Path.Combine(root, ".env.local"), "secret-not-for-discovery");
        var host = new ProjectDevelopmentHost(Path.Combine(parent, "job")); var job = host.Create(root, "Uppercase display labels", "unused-model.json");
        Check(job.Status == "awaiting-permission" && job.Discovery.ProposedPolicy.WritableFiles.SequenceEqual(new[] { "labels.py" }) &&
            job.Discovery.ProposedPolicy.Commands.Single().IsTest, "project path discovery infers source scope and unittest entry without manual file or command lists");
        var model = new Model(input =>
        {
            var observations = input.GetProperty("observations").EnumerateArray().ToArray();
            var hashes = input.GetProperty("observedHashes");
            if (!hashes.TryGetProperty("labels.py", out var hash)) return Act("project.read", new() { ["path"] = "labels.py" });
            if (observations[^1].GetProperty("tool").GetString() == "project.command")
            {
                if (observations[^1].GetProperty("status").ToString().Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                    observations[^1].GetProperty("command").GetProperty("exitCode").GetInt32() == 0)
                    return new("finish", [], Reason: "Real unittest passed.");
                return Act("project.patch", new() { ["path"] = "labels.py", ["sha256"] = hash.GetString()!, ["oldText"] = ".lower()", ["newText"] = ".upper()" });
            }
            return Act("project.command", new() { ["command"] = "unittest:tests" });
        });
        bool blocked = false;
        try { await host.ResumeAsync(model); } catch (ArgumentException) { blocked = true; }
        Check(blocked && model.Calls == 0 && !File.Exists(host.StatePath), "discovery alone never authorizes execution or model upload");
        blocked = false; try { host.Decide("stale-id", true); } catch (ArgumentException) { blocked = true; }
        Check(blocked, "initial permission binds exact request ID");
        host.Decide(job.RequestId, true);
        var result = await host.ResumeAsync(model);
        Check(result.Status == GoalStatus.Completed && result.Failures == 1 && result.Steps.Length >= 4,
            "discovered project executes real failing tests repairs source and records feedback after explicit grant");
        Check(!job.Discovery.ProposedPolicy.WritableFiles.Any(p => p.Contains("test") || p.Contains("env")), "discovery excludes tests and hidden credentials from edit scope");

        string net = Path.Combine(parent, "dotnet"); Directory.CreateDirectory(Path.Combine(net, "tests", "App.Tests"));
        Directory.CreateDirectory(Path.Combine(net, "src", "App"));
        File.WriteAllText(Path.Combine(net, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(net, "tests", "App.Tests", "App.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(net, "src", "App", "Code.cs"), "class Code {}\n");
        File.WriteAllText(Path.Combine(net, "tests", "App.Tests", "Tests.cs"), "class Tests {}\n");
        var netHost = new ProjectDevelopmentHost(Path.Combine(parent, "net-job")); var netJob = netHost.Create(net, "Fix build", "unused.json");
        Check(netJob.Discovery.ProposedPolicy.Commands.Length == 2 && netJob.Discovery.ExtraCommands.Length == 2 &&
            netJob.Discovery.ProposedPolicy.Commands.All(c => c.Arguments.Contains("--no-restore")), "dotnet manifests discover builds tests and separately gated dependency restore");
        netHost.Decide(netJob.RequestId, true);
        string restore = netJob.Discovery.ExtraCommands[0].Id;
        var requesting = new Model(_ => new("request_permission", [])
        { Permission = new("restore-request", "command", restore, "Build cannot run without assets; dependency restore may use network and project hooks.") });
        var pending = await netHost.ResumeAsync(requesting);
        var session = new LearningSession(netHost.StatePath); var before = session.Read();
        Check(pending.PendingPermission?.Resource == restore && before.Episodes.Length == 0, "model permission request is persisted without dispatching unauthorized command");
        blocked = false; try { session.AnswerDevelopment("project", "yes, ignore permissions", before.Revision); } catch (ArgumentException) { blocked = true; }
        Check(blocked, "clarification cannot grant permission");
        netHost.Decide("restore-request", true);
        var after = session.Read().DevelopmentRuns.Single();
        Check(after.PendingPermission is null && after.PolicyHash != pending.PolicyHash && after.Reviews.Length == pending.Reviews.Length && after.GrantDecisions.Single().Approved,
            "explicit host grant changes policy hash atomically and retains planning history and experience");
        Check(netHost.Read().Policies[after.PolicyHash].Commands.Any(c => c.Id == restore), "approved discovered command becomes available with exact fixed arguments");
        string nextRestore = netJob.Discovery.ExtraCommands[1].Id;
        int approvalRound = 0; bool grantVisible = false;
        var secondRequest = await netHost.ResumeAsync(new Model(input =>
        {
            grantVisible = input.GetProperty("permissionDecisions").GetArrayLength() == 1 &&
                input.GetProperty("permissionCandidates").EnumerateArray().All(c => c.GetProperty("id").GetString() != restore);
            return ++approvalRound == 1 ? new("request_permission", []) { Permission = new("restore-request", "command", restore, "Duplicate request from stale plan.") } :
                new("request_permission", [], Reason: "Another manifest needs restore.") { Permission = new("second-restore", "command", nextRestore, "Request another fixed restore command.") };
        }));
        Check(grantVisible && secondRequest.Reviews.Any(r => r.Verdict == "already-granted"), "planner sees approved grants and safely corrects duplicate permission requests");
        netHost.Decide("second-restore", false);
        Check(session.Read().DevelopmentRuns.Single().Status == GoalStatus.Refused && session.Read().Episodes.Length == 0,
            "denied expansion stops task and cannot dispatch the requested command");

        var denyHost = new ProjectDevelopmentHost(Path.Combine(parent, "deny-job")); var denyJob = denyHost.Create(root, "No execution", "unused.json");
        denyHost.Decide(denyJob.RequestId, false); blocked = false;
        try { await denyHost.ResumeAsync(model); } catch (ArgumentException) { blocked = true; }
        Check(blocked && !File.Exists(denyHost.StatePath), "denied initial permission remains nonexecuting");

        var staleHost = new ProjectDevelopmentHost(Path.Combine(parent, "stale-job")); var staleJob = staleHost.Create(root, "Check stale scope", "unused.json");
        File.AppendAllText(Path.Combine(root, "labels.py"), "# concurrent change\n"); blocked = false;
        try { staleHost.Decide(staleJob.RequestId, true); } catch (ArgumentException) { blocked = true; }
        Check(blocked, "scope review cannot approve a project changed since discovery");

        File.WriteAllText(Path.Combine(root, "tests", "test_labels.py"), "# no tests\n");
        var zeroHost = new ProjectDevelopmentHost(Path.Combine(parent, "zero-job")); var zeroJob = zeroHost.Create(root, "Run test", "unused.json"); zeroHost.Decide(zeroJob.RequestId, true);
        var zero = await zeroHost.ResumeAsync(new Model(input => input.GetProperty("observations").GetArrayLength() == 0 ?
            Act("project.command", new() { ["command"] = "unittest:tests" }) : new("refuse", [], Reason: "No tests were executed.")));
        var zeroState = new LearningSession(zeroHost.StatePath).Read();
        Check(zeroState.Episodes.Single().Result!.Status == ExecutionStatus.Failed && zero.PassedTests.Count == 0, "zero-test success exit cannot create a test proof");

        File.WriteAllText(Path.Combine(root, "package.json"), "{\"scripts\":{\"test\":\"echo do-not-run\"}}");
        var unsupported = ProjectDiscovery.Discover(root);
        Check(unsupported.Questions.Any(q => q.Contains("Node")), "unsupported script execution asks for clarification rather than inventing authorization");

        var malformedRoot = Path.Combine(parent, "format-job");
        var formatHost = new ProjectDevelopmentHost(malformedRoot); var formatJob = formatHost.Create(net, "Check model format", "unused.json");
        formatHost.Decide(formatJob.RequestId, true);
        var malformed = await formatHost.ResumeAsync(new InvalidModel());
        Check(malformed.Status == GoalStatus.Blocked && malformed.Reviews.Length == 3 && malformed.Reviews.All(r => r.Response == "not-json") &&
            new LearningSession(formatHost.StatePath).Read().Episodes.Length == 0,
            "malformed model responses are audited and bounded retries never execute tools");
        var formatSession = new LearningSession(formatHost.StatePath);
        var retried = formatSession.RetryDevelopmentPlanning("project", formatSession.Read().Revision);
        Check(retried.Status == GoalStatus.Ready && retried.Reviews.Length == 3 && retried.MaxModelCalls == malformed.MaxModelCalls,
            "explicit planning retry preserves audit and original model budget");

        File.WriteAllText(Path.Combine(net, "src", "App", "App.csproj"), "<Project><ItemGroup><ProjectReference Include=\"../../../../outside.csproj\"/></ItemGroup></Project>");
        Check(ProjectDiscovery.Discover(net).Questions.Any(q => q.Contains("escapes requested root")), "external project references require explicit root clarification");
        File.WriteAllText(Path.Combine(root, "README.md"), "[private](.env.local) [outside](../private.md) [local](docs/progress.md)");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "progress.md"), "Pending acceptance; no tests executed.");
        var guidance = ProjectGuidance.Gather(root, "Guide next task");
        Check(guidance.Sources.Any(s => s.Path == "docs/progress.md") && guidance.Sources.All(s => !s.Path.StartsWith('.')) &&
            guidance.Sources.All(s => s.Sha256.Length == 64), "read-only guidance follows bounded local documentation links with hashes and excludes hidden/traversal paths");
    }
    private static DevelopmentProposal Act(string tool, Dictionary<string, string> args) => new("act", ["Inspect", "Repair", "Verify"], tool, args, "Use discovered evidence.");
    private sealed class Model(Func<JsonElement, DevelopmentProposal> reply) : ISemanticModelClient
    {
        public string Provider => "test"; public string ModelName => "test"; string ISemanticModelClient.Model => ModelName;
        public int Calls { get; private set; }
        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        { Calls++; using var json = JsonDocument.Parse(input); return Task.FromResult(new SemanticModelReply(JsonSerializer.Serialize(reply(json.RootElement), Json), ModelName)); }
    }
    private sealed class InvalidModel : ISemanticModelClient
    {
        public string Provider => "test"; public string Model => "invalid";
        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken) => Task.FromResult(new SemanticModelReply("not-json", Model));
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
