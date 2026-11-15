using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class DevelopmentChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Run()
    {
        var h = new Harness();
        var scripted = new TestModel(input =>
        {
            var observations = input.GetProperty("observations").EnumerateArray().ToArray();
            var hashes = input.GetProperty("observedHashes");
            int failures = input.GetProperty("failures").GetInt32();
            if (observations.Length == 0) return Action("project.search", new() { ["query"] = "" });
            if (!hashes.TryGetProperty("calculator.py", out var hash)) return Action("project.read", new() { ["path"] = "calculator.py" });
            if (!hashes.TryGetProperty("tests.py", out _)) return Action("project.read", new() { ["path"] = "tests.py" });
            string? last = observations[^1].GetProperty("tool").GetString();
            if (last == "project.command" && observations[^1].GetProperty("command").GetProperty("exitCode").GetInt32() == 0)
                return new("finish", [], Reason: "All required tests passed for final code.");
            if (last == "project.command") return Action("project.patch", new()
            {
                ["path"] = "calculator.py", ["sha256"] = hash.GetString()!,
                ["oldText"] = failures == 1 ? "return a - b" : "return a * b", ["newText"] = failures == 1 ? "return a * b" : "return a + b"
            });
            return Action("project.command", new() { ["command"] = "unit" });
        });
        var runner = new DevelopmentRunner(h.Session, h.Files, scripted);
        runner.Start("fix", "Repair addition and pass all tests.");
        var result = await runner.RunAsync("fix").WaitAsync(TimeSpan.FromSeconds(40));
        Check(result.Status == GoalStatus.Completed && result.Failures == 2 && h.Files.Text("calculator.py").Contains("return a + b"),
            "development loop searches reads edits executes failing tests and corrects a failed patch automatically");
        var state = h.Session.Read();
        Check(state.Episodes.Count(e => e.Result?.Command is { ExitCode: 0 }) == 1 && state.Episodes.Count(e => e.Result?.Command is { ExitCode: 1 }) == 2,
            "actual process exit codes and stderr are retained as learning episodes");
        Check(result.PassedTests["unit"] == h.Files.Snapshot() && File.ReadAllText(Path.Combine(h.Root, "tests.py")) == Harness.TestCode,
            "completion requires final snapshot test proof and verifier remains unchanged");
        int calls = scripted.Calls;
        Check((await runner.RunAsync("fix")).Status == GoalStatus.Completed && scripted.Calls == calls, "completed developer task does not repeat commands on restart");
        Check(state.Episodes.Where(e => e.Result?.Command is not null).Select(e => e.Trace.Input.Facts["projectSnapshot"]).Distinct().Count() == 3,
            "test failure experience is scoped to code snapshot so changed code can be retested");

        foreach (var proposal in new[] {
            new DevelopmentProposal("finish", [], Reason: "Assume success."),
            Action("project.command", new() { ["command"] = "rm -rf ." }),
            Action("project.read", new() { ["path"] = "../outside.py" }),
            Action("project.read", new() { ["path"] = ".env.local" }),
            Action("project.patch", new() { ["path"] = "tests.py", ["sha256"] = new string('A', 64), ["oldText"] = "assert", ["newText"] = "#assert" }),
            Action("project.patch", new() { ["path"] = "calculator.py", ["sha256"] = new string('A', 64), ["oldText"] = "return a - b", ["newText"] = "return a + b" }) })
        {
            var denied = new Harness(); var run = new DevelopmentRunner(denied.Session, denied.Files, new TestModel(_ => proposal)); run.Start("denied", "Fix addition");
            var blocked = await run.RunAsync("denied");
            Check(blocked.Status == GoalStatus.Blocked && denied.Session.Read().Episodes.Length == 0 && denied.Files.Text("calculator.py").Contains("a - b"),
                "core blocks premature completion unauthorized command path or unobserved patch");
        }
        var role = new Harness(new("readonly", .5m, [], [new("no-code", "code.changed", "Read-only role cannot edit code.")]));
        var refusing = new DevelopmentRunner(role.Session, role.Files, new TestModel(input => input.GetProperty("observedHashes").TryGetProperty("calculator.py", out var hash)
            ? Action("project.patch", new() { ["path"] = "calculator.py", ["sha256"] = hash.GetString()!, ["oldText"] = "return a - b", ["newText"] = "return a + b" })
            : Action("project.read", new() { ["path"] = "calculator.py" })));
        refusing.Start("role", "Fix addition");
        Check((await refusing.RunAsync("role")).Status == GoalStatus.Refused && role.Files.Text("calculator.py").Contains("a - b"), "role judgment can refuse an authorized code patch");

        var external = new Harness();
        var changing = new TestModel(input =>
        {
            if (!input.GetProperty("observedHashes").TryGetProperty("calculator.py", out var hash)) return Action("project.read", new() { ["path"] = "calculator.py" });
            File.AppendAllText(Path.Combine(external.Root, "calculator.py"), "# user edit\n");
            return Action("project.patch", new() { ["path"] = "calculator.py", ["sha256"] = hash.GetString()!, ["oldText"] = "return a - b", ["newText"] = "return a + b" });
        });
        var externalRunner = new DevelopmentRunner(external.Session, external.Files, changing); externalRunner.Start("stale", "Fix addition");
        var externalResult = await externalRunner.RunAsync("stale");
        Check(externalResult.Status != GoalStatus.Completed && external.Files.Text("calculator.py").Contains("a - b") &&
            external.Session.Read().Episodes.Any(e => e.Result?.Failure == ToolFailure.StaleContent), "hash guard preserves concurrent user edits instead of overwriting them");

        var timeout = new Harness(timeout: 100);
        var slow = new DevelopmentRunner(timeout.Session, timeout.Files, new TestModel(_ => Action("project.command", new() { ["command"] = "slow" })));
        slow.Start("timeout", "Inspect project");
        var unknown = await slow.RunAsync("timeout").WaitAsync(TimeSpan.FromSeconds(10));
        Check(unknown.Status == GoalStatus.NeedsReconciliation && timeout.Session.Read().Episodes.Length == 1, "timed-out process tree is stopped and unknown effects are not automatically retried");

        var secret = new Harness(); string variable = "DEEPSEEK_API_KEY";
        var old = Environment.GetEnvironmentVariable(variable); Environment.SetEnvironmentVariable(variable, "fake-test-secret");
        try
        {
            var scrub = new DevelopmentRunner(secret.Session, secret.Files, new TestModel(input => input.GetProperty("observations").GetArrayLength() == 0
                ? Action("project.command", new() { ["command"] = "environment" }) : new("refuse", [], Reason: "Inspection complete; no requested edit.")));
            scrub.Start("env", "Inspect environment"); await scrub.RunAsync("env");
            Check(secret.Session.Read().Episodes[0].Result!.Command!.StandardOutput.Trim() == "absent", "development command does not inherit model credentials");
        }
        finally { Environment.SetEnvironmentVariable(variable, old); }

        var multi = new Harness();
        File.WriteAllText(Path.Combine(multi.Root, "settings.py"), "FACTOR = 2\n");
        File.WriteAllText(Path.Combine(multi.Root, "calculator.py"), "from settings import FACTOR\ndef add(a, b):\n    return (a - b) * FACTOR\n");
        var multiFiles = new ProjectFiles(multi.Files.Policy with { WritableFiles = ["calculator.py", "settings.py"] });
        bool summaryRetained = false;
        var multiModel = new TestModel(input =>
        {
            var history = input.GetProperty("observations").EnumerateArray().ToArray();
            var hashes = input.GetProperty("observedHashes");
            if (history.Length == 0) return Action("project.inspect", new() { ["prefix"] = "", ["offset"] = "0" });
            foreach (string path in new[] { "calculator.py", "settings.py" })
                if (!hashes.TryGetProperty(path, out _)) return Action("project.read-range", new() { ["path"] = path, ["startLine"] = "1", ["lineCount"] = "8" });
            if (history[^1].GetProperty("tool").GetString() == "project.command")
            {
                if (history[^1].GetProperty("command").GetProperty("exitCode").GetInt32() == 0)
                {
                    summaryRetained = input.GetProperty("modifiedFiles").GetArrayLength() == 2;
                    return new("finish", [], Reason: "Both dependent files fixed and verified.");
                }
                bool first = input.GetProperty("failures").GetInt32() == 1;
                string path = first ? "calculator.py" : "settings.py";
                return Action("project.patch", new() { ["path"] = path, ["sha256"] = hashes.GetProperty(path).GetString()!,
                    ["oldText"] = first ? "a - b" : "FACTOR = 2", ["newText"] = first ? "a + b" : "FACTOR = 1" });
            }
            return Action("project.command", new() { ["command"] = "unit" });
        });
        var multiRunner = new DevelopmentRunner(multi.Session, multiFiles, multiModel);
        multiRunner.Start("multi", "Repair both modules and verify dependent behavior.");
        var multiResult = await multiRunner.RunAsync("multi");
        Check(multiResult.Status == GoalStatus.Completed && multiResult.Failures == 2 && summaryRetained,
            "multi-file workflow inspects structure reads ranges fixes dependent modules after real failures and retains change summary");
        Check(multiResult.PassedTests["unit"] == multiFiles.Snapshot(), "multi-file completion verifies tests after the last dependent edit");

        for (int i = 0; i < 30; i++) File.WriteAllText(Path.Combine(multi.Root, $"module{i:00}.py"), "needle\nneedle\n");
        var listed = new HashSet<string>(); int offset = 0;
        while (true)
        {
            using var page = JsonDocument.Parse(ProjectInspection.Inspect(multiFiles, "module", offset));
            foreach (var entry in page.RootElement.GetProperty("entries").EnumerateArray()) listed.Add(entry.GetProperty("path").GetString()!);
            var next = page.RootElement.GetProperty("nextOffset"); if (next.ValueKind == JsonValueKind.Null) break; offset = next.GetInt32();
        }
        Check(listed.Count == 30, "project structure pagination reaches files beyond the first page");
        int hits = 0; offset = 0;
        while (true)
        {
            using var page = JsonDocument.Parse(ProjectInspection.Search(multiFiles, "needle", "module", offset));
            hits += page.RootElement.GetProperty("matches").GetArrayLength();
            var next = page.RootElement.GetProperty("nextOffset"); if (next.ValueKind == JsonValueKind.Null) break; offset = next.GetInt32();
        }
        Check(hits == 60, "paged search preserves matches across files without silently dropping later hits");
        File.WriteAllText(Path.Combine(multi.Root, "long.py"), string.Join('\n', Enumerable.Range(1, 200).Select(i => "line " + i)));
        using var range = JsonDocument.Parse(ProjectInspection.Range(multiFiles, "long.py", 150, 3));
        Check(range.RootElement.GetProperty("lines")[0].GetProperty("text").GetString() == "line 150" && range.RootElement.GetProperty("nextLine").GetInt32() == 153,
            "source ranges expose late-file code and continuation line");
        bool rejected = false;
        try { ProjectInspection.Inspect(multiFiles, "../", 0); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "structure browsing rejects traversal prefixes");

        var verifier = new Harness();
        var tamper = new DevelopmentRunner(verifier.Session, verifier.Files, new TestModel(_ =>
        {
            File.WriteAllText(Path.Combine(verifier.Root, "tests.py"), "print('fake pass')\n");
            return Action("project.command", new() { ["command"] = "unit" });
        }));
        tamper.Start("verifier", "Fix code");
        Check((await tamper.RunAsync("verifier")).Status == GoalStatus.Blocked && verifier.Session.Read().Episodes.Length == 0,
            "protected verifier baseline blocks changed tests before command dispatch");

        File.WriteAllText(Path.Combine(multi.Root, "App.csproj"), "<Project><ItemGroup><ProjectReference Include=\"../Library/Library.csproj\"/></ItemGroup></Project>");
        var manifestFiles = new ProjectFiles(multiFiles.Policy with { ReadExtensions = [".py", ".csproj"] });
        using var map = JsonDocument.Parse(ProjectInspection.Inspect(manifestFiles, "App"));
        Check(map.RootElement.GetProperty("entries")[0].GetProperty("projectReferences")[0].GetString() == "../Library/Library.csproj",
            "structure map reports declared project references without evaluating build files");
    }
    private static DevelopmentProposal Action(string tool, Dictionary<string, string> args) => new("act", ["Inspect", "Repair", "Verify actual tests"], tool, args, "Use actual evidence for the next development step.");
    private sealed class TestModel(Func<JsonElement, DevelopmentProposal> respond) : ISemanticModelClient
    {
        public string Provider => "controlled-development"; public string Model => "test"; public int Calls { get; private set; }
        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        { Calls++; using var doc = JsonDocument.Parse(input); return Task.FromResult(new SemanticModelReply(JsonSerializer.Serialize(respond(doc.RootElement), Json), Model)); }
    }
    private sealed class Harness
    {
        public const string TestCode = "import os, sys\nsys.path.insert(0, os.path.dirname(__file__))\nfrom calculator import add\nassert add(2, 3) == 5, '2 + 3 must be 5'\nassert add(-2, 3) == 1, '-2 + 3 must be 1'\nprint('All addition tests passed')\n";
        public string Root { get; } public LearningSession Session { get; } public ProjectFiles Files { get; }
        public Harness(PersistentRole? role = null, int timeout = 5000)
        {
            var parent = Path.Combine(Path.GetTempPath(), "littleloop-dev-" + Guid.NewGuid().ToString("N")); Root = Path.Combine(parent, "project"); Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "calculator.py"), "def add(a, b):\n    return a - b\n"); File.WriteAllText(Path.Combine(Root, "tests.py"), TestCode);
            Session = new(Path.Combine(parent, "state.json")); Session.Initialize(role); Session.EnableScheduling(new(1, 100));
            string exe = FindPython();
            Files = new(new(Root, ["calculator.py"], [new("unit", exe, ["-I", "-B", "tests.py"], true, ["tests.py"]),
                new("slow", exe, ["-I", "-c", "import time; time.sleep(5)"], false, []),
                new("environment", exe, ["-I", "-c", "import os; print(os.environ.get('DEEPSEEK_API_KEY','absent'))"], false, [])], [".py"], TimeoutMilliseconds: timeout, MaxModelCalls: 24));
        }
    }
    private static string FindPython()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string name in OperatingSystem.IsWindows() ? new[] { "python.exe" } : new[] { "python3", "python" })
            { string path = Path.Combine(directory.Trim('"'), name); if (Path.IsPathFullyQualified(path) && File.Exists(path)) return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path; }
        throw new Exception("Development integration checks require Python 3 on PATH.");
    }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
}
