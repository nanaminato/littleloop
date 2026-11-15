using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class RealToolChecks
{
    public static async Task Run()
    {
        await LearningFromFiles();
        await Boundaries();
        await LifecycleAndConcurrency();
    }
    private static LearningTask TaskInput(string id, string outcome, Dictionary<string, string> arguments,
        string[] tools, string? context = null) => new(id, "Complete local file task", new() { ["workflow"] = context ?? id },
            [new("result", outcome, 1, true)], [], [], [], 1, tools) { ToolArguments = arguments };
    private static Dictionary<string, string> Paths(string target, string source = "input.txt") => new() { ["source"] = source, ["target"] = target };

    private static async Task LearningFromFiles()
    {
        var h = new Harness();
        string original = "first line  \r\nsecond line\t\r\n";
        File.WriteAllText(Path.Combine(h.Root, "input.txt"), original, new UTF8Encoding(false));
        var first = await h.Run(TaskInput("first", "file.created", Paths("first.txt"), ["text.normalize-lines"], "notes"));
        Check(first.Result!.Status == ExecutionStatus.Completed && first.Result.ExternalEffect &&
            File.ReadAllText(Path.Combine(h.Root, "first.txt")) == "first line\nsecond line\n", "real tool creates and verifies normalized file");
        var artifact = first.Result.ToolReceipt!.Artifacts.Single(a => a.Usage == "output");
        Check(artifact.Sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(h.Root, artifact.Path)))), "receipt records observed output hash");
        Check(first.Trace.Responses.Any(r => r.Plugin.Id == "workspace.tools"), "tool recipes discovered without supplied action options");
        File.WriteAllText(Path.Combine(h.Root, "other.txt"), "another  \r\n", new UTF8Encoding(false));
        var second = await h.Run(TaskInput("second", "file.created", Paths("second.txt", "other.txt"), ["text.normalize-lines"], "notes"), usePlugin: false);
        Check(second.Trace.Decision.Selected.Proposal.Id.StartsWith("recall:") && File.ReadAllText(Path.Combine(h.Root, "second.txt")) == "another\n",
            "experience reuses procedure with current file arguments, not old paths");
        h.Session.Feedback(new("feedback-first", first.Trace.Input.Id, null, false, ["file.created", "text.normalized"], "document-reviewer",
            "Trailing whitespace is meaningful for this document class; preserve layout."));
        var third = await h.Run(TaskInput("third", "file.created", Paths("third.txt"), ["text.normalize-lines", "text.copy"], "notes"));
        Check(third.Trace.Decision.Selected.Proposal.Tool!.Id == "text.copy" && File.ReadAllText(Path.Combine(h.Root, "third.txt")) == original,
            "real execution feedback changes the next chosen tool strategy");
        Check(h.Session.Read().Episodes[0].Result!.Status == ExecutionStatus.Completed, "semantic feedback preserves original technical execution evidence");
        File.WriteAllText(Path.Combine(h.Root, "first.txt"), "human edit");
        Check(JsonSerializer.Serialize(DeliberationPolicy.Decide(first.Trace.Input, first.Trace.Responses)) == JsonSerializer.Serialize(first.Trace.Decision) &&
            File.ReadAllText(Path.Combine(h.Root, "first.txt")) == "human edit", "replay does not reread or rewrite real tool output");

        var missing = await h.Run(TaskInput("missing", "text.read", new() { ["source"] = "missing.txt" }, ["text.read"], "read-notes"));
        Check(missing.Result!.Status == ExecutionStatus.Failed && !missing.Result.ExternalEffect, "actual missing file produces failure instead of invented success");
        var read = await h.Run(TaskInput("read", "text.read", new() { ["source"] = "input.txt" }, ["text.read"], "read-notes"));
        Check(read.Result!.ToolReceipt!.OutputText == original && read.Result.ActualOutcomes.SequenceEqual(["text.read"]), "read tool returns actual bounded file content");
        Check(!read.Trace.Input.Cognition!.Experiences.Any(e => e.Id == "episode:" + missing.Trace.Input.Id), "file-specific operational failure is not generalized to a different path");
        var create = await h.Run(TaskInput("write", "text.written", new() { ["target"] = "written.txt", ["content"] = "真实文本\n" }, ["text.create"]));
        Check(create.Result!.Status == ExecutionStatus.Completed && File.ReadAllText(Path.Combine(h.Root, "written.txt")) == "真实文本\n", "write tool creates actual UTF-8 content");
        var absentArguments = await h.Run(TaskInput("needs-path", "file.created", new() { ["source"] = "input.txt" }, ["text.copy"], "notes"), usePlugin: false);
        Check(absentArguments.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Ask, "recalled tool requests missing current parameters before execution");
    }

    private static async Task Boundaries()
    {
        var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "input.txt"), "text");
        File.WriteAllText(Path.Combine(h.Root, "existing.txt"), "keep");
        foreach (var (id, path) in new[] { ("traversal", "../escape.txt"), ("absolute", Path.Combine(h.Parent, "escape.txt")),
                     ("alternate-stream", "file.txt:stream"), ("hidden", ".git/config"), ("reserved", "CON.txt"),
                     ("device-alias", "COM¹.txt"), ("device-spaces", "NUL .txt"), ("existing", "existing.txt") })
        {
            var result = await h.Run(TaskInput(id, "file.created", Paths(path), ["text.copy"]));
            Check(result.Result!.Status == ExecutionStatus.Blocked && !result.Result.ExternalEffect, "blocked file boundary: " + id);
        }
        Check(!File.Exists(Path.Combine(h.Parent, "escape.txt")) && File.ReadAllText(Path.Combine(h.Root, "existing.txt")) == "keep", "blocked operations leave outside and existing files unchanged");
        var denied = await h.Run(TaskInput("grant", "file.created", Paths("denied.txt"), ["text.copy"]), policy: h.Policy with { AllowedTools = ["text.read"] });
        Check(denied.Result!.Status == ExecutionStatus.Blocked && !File.Exists(Path.Combine(h.Root, "denied.txt")), "task cannot exceed host tool grants");
        var large = await h.Run(TaskInput("large", "text.read", new() { ["source"] = "input.txt" }, ["text.read"]), policy: h.Policy with { MaxFileBytes = 2 });
        Check(large.Result!.Status == ExecutionStatus.Blocked, "real file size limit enforced");
        File.WriteAllBytes(Path.Combine(h.Root, "invalid.txt"), [0xFF, 0xFE]);
        var invalid = await h.Run(TaskInput("invalid", "text.read", new() { ["source"] = "invalid.txt" }, ["text.read"]));
        Check(invalid.Result!.Status == ExecutionStatus.Failed && invalid.Result.ToolReceipt!.Artifacts.Length == 1, "invalid UTF-8 records failure with source evidence");
        var validTask = TaskInput("cheap", "file.created", Paths("cheap.txt"), ["text.copy"]);
        var source = new DeliberationInput("proposal", "goal", "role", .5m, 1, ["text.copy"], [], [])
        { Cognition = new(validTask.Goals, [], [], [], [], []), ToolArguments = validTask.ToolArguments };
        var recipe = new WorkspaceToolPlugin(["text.copy"]).Propose(source).Single();
        var cheap = await h.Run(validTask with { Options = [recipe with { Cost = 0 }] }, usePlugin: false);
        Check(cheap.Result!.Status == ExecutionStatus.Blocked, "tool contract prevents understated action cost");
        var fake = await h.Run(TaskInput("fake", "invented-success", Paths("fake.txt"), ["text.copy"]) with
        { Options = [recipe with { Meaning = recipe.Meaning! with { Outcomes = ["invented-success"] } }] }, usePlugin: false);
        Check(fake.Result!.Status == ExecutionStatus.Completed && !fake.Result.ActualOutcomes.Contains("invented-success") &&
            h.Session.Read().Runtime!.Tasks.Single(t => t.Task.Id == "fake").Status == WorkStatus.WaitingFeedback,
            "actual tool outcomes cannot be forged by a candidate's claimed effects");

        string link = Path.Combine(h.Root, "linked");
        string outside = Path.Combine(h.Parent, "outside"); Directory.CreateDirectory(outside);
        bool linked = false;
        try { Directory.CreateSymbolicLink(link, outside); linked = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        { Console.WriteLine("SKIP symlink creation unavailable: " + error.GetType().Name); }
        if (linked)
        {
            var escape = await h.Run(TaskInput("symlink", "file.created", Paths("linked/escape.txt"), ["text.copy"]));
            Check(escape.Result!.Status == ExecutionStatus.Blocked && !File.Exists(Path.Combine(outside, "escape.txt")), "symlink cannot redirect workspace output");
        }
    }

    private static async Task LifecycleAndConcurrency()
    {
        var h = new Harness(); File.WriteAllText(Path.Combine(h.Root, "input.txt"), "text");
        var task = TaskInput("stale", "file.created", Paths("stale.txt"), ["text.copy"]) with { Budget = 2 };
        h.Session.Enqueue(task); h.Session.ThinkNext(DateTimeOffset.UtcNow, h.Host());
        var request = h.Session.DispatchNext()!;
        h.Session.ReviseTask(task with { ToolArguments = Paths("new.txt") }, 1);
        var executor = new WorkspaceToolExecutor(h.Session, h.Policy);
        var stale = await executor.ExecuteAsync(request, CancellationToken.None);
        h.Session.Complete(stale);
        Check(stale.Status == ExecutionStatus.Blocked && !File.Exists(Path.Combine(h.Root, "stale.txt")) &&
            h.Session.Read().Runtime!.Tasks.Single().Version == 2, "live task version checked before any real file effect");
        var done = await new MultiTaskRuntime(h.Session, executor, h.Host()).RunAsync();
        var episode = done.Episodes.Last();
        var repeated = await executor.ExecuteAsync(new("stale", 2, episode.Trace), CancellationToken.None);
        Check(repeated.Status == ExecutionStatus.Blocked && File.Exists(Path.Combine(h.Root, "new.txt")), "completed dispatch cannot be executed again");

        var race = new Harness();
        foreach (var id in new[] { "one", "two" }) race.Session.Enqueue(TaskInput(id, "text.written",
            new() { ["target"] = "shared.txt", ["content"] = id }, ["text.create"]));
        var state = await new MultiTaskRuntime(race.Session, new WorkspaceToolExecutor(race.Session, race.Policy), race.Host()).RunAsync();
        Check(state.Episodes.Count(e => e.Result!.Status == ExecutionStatus.Completed) == 1 &&
            new[] { "one", "two" }.Contains(File.ReadAllText(Path.Combine(race.Root, "shared.txt"))), "concurrent writers create once without overwriting each other");
        Check(Directory.GetFiles(race.Root, ".littleloop-*.tmp").Length == 0, "temporary output cleaned after writer conflict");

        var cancelled = new Harness();
        cancelled.Session.Enqueue(TaskInput("cancel", "text.written", new() { ["target"] = "never.txt", ["content"] = "never" }, ["text.create"]));
        cancelled.Session.ThinkNext(DateTimeOffset.UtcNow, cancelled.Host());
        var pending = cancelled.Session.DispatchNext()!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try
        {
            await new WorkspaceToolExecutor(cancelled.Session, cancelled.Policy).ExecuteAsync(pending, cancellation.Token);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(cancelled.Root, "never.txt")), "cancelled execution cannot publish output"); }
    }

    private sealed class Harness
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "littleloop-real-tools-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public LearningSession Session { get; }
        public WorkspaceToolPolicy Policy { get; }
        public Harness()
        {
            Root = Path.Combine(Parent, "workspace"); Directory.CreateDirectory(Root);
            Session = new(Path.Combine(Parent, "state.json")); Session.Initialize(); Session.EnableScheduling(new(2, 100));
            Policy = new(Root, WorkspaceTools.List().Select(t => t.Id).ToArray());
        }
        public PluginHost Host()
        {
            var host = new PluginHost(); host.Register(new WorkspaceToolPlugin(Policy.AllowedTools)); return host;
        }
        public async Task<LearningEpisode> Run(LearningTask task, bool usePlugin = true, WorkspaceToolPolicy? policy = null)
        {
            Session.Enqueue(task);
            var state = await new MultiTaskRuntime(Session, new WorkspaceToolExecutor(Session, policy ?? Policy), usePlugin ? Host() : null)
                .RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var work = state.Runtime!.Tasks.Single(t => t.Task.Id == task.Id);
            return state.Episodes.Single(e => e.Trace.Input.Id == work.CurrentDecisionId);
        }
    }
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine($"PASS {label}"); }
}
