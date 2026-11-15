using LittleLoop.Core;
using LittleLoop.Tools;

internal static class GoalChecks
{
    public static async Task Run()
    {
        var h = new Harness();
        File.WriteAllText(Path.Combine(h.Root, "source.txt"), "first  \r\nsecond\t\r\n");
        DocumentGoals.Add(h.Session, new("fallback", "Normalize and verify document", "result.txt", ["missing.txt", "source.txt"]));
        var state = await h.Run();
        var goal = state.Goals.Single();
        Check(goal.Status == GoalStatus.Completed && goal.Plans.Length == 2 && goal.Steps.Length == 4 &&
            File.ReadAllText(Path.Combine(h.Root, "result.txt")) == "first\nsecond\n", "goal searches tool chain and replans after actual missing input");
        Check(goal.Plans[1].EvidenceDecisionId is not null && state.Episodes.Length == 4 && state.Episodes[0].Result!.Failure == ToolFailure.MissingInput,
            "replanning preserves failure evidence and every actual execution episode");
        var revision = state.Revision;
        Check((await h.Run()).Revision == revision, "completed goal restart does not rerun tools");

        var inline = new Harness();
        DocumentGoals.Add(inline.Session, new("inline", "Normalize supplied text", "inline.txt", [], "inline  \r\n"));
        var inlineGoal = (await inline.Run()).Goals.Single();
        Check(inlineGoal.Status == GoalStatus.Completed && inlineGoal.Plans[0].Steps.Select(s => s.Id).SequenceEqual(["create-source", "normalize", "verify-output"]),
            "planner composes create normalize read without caller-specified steps");

        var conflict = new Harness();
        File.WriteAllText(Path.Combine(conflict.Root, "existing.txt"), "keep");
        DocumentGoals.Add(conflict.Session, new("conflict", "Preserve existing output", "existing.txt", [], "new", Normalize: false));
        Check((await conflict.Run()).Goals.Single().Status == GoalStatus.Blocked && File.ReadAllText(Path.Combine(conflict.Root, "existing.txt")) == "keep",
            "planner cannot overwrite or silently redirect an exact output goal");
        var alternate = new Harness();
        File.WriteAllText(Path.Combine(alternate.Root, "existing.txt"), "keep");
        DocumentGoals.Add(alternate.Session, new("alternate", "Create a usable output", "existing.txt", [], "new", Normalize: false, AllowAlternativeOutput: true));
        var alternative = (await alternate.Run()).Goals.Single();
        Check(alternative.Status == GoalStatus.Completed && alternative.Replans == 1 &&
            File.ReadAllText(Path.Combine(alternate.Root, alternative.Resources["target"])) == "new", "explicit output flexibility enables recovery from target collision");

        var limited = new Harness();
        DocumentGoals.Add(limited.Session, new("budget", "Insufficient budget", "no.txt", [], "text", Budget: 2));
        var blocked = await limited.Run();
        Check(blocked.Goals.Single().Status == GoalStatus.Blocked && blocked.Episodes.Length == 0, "planner rejects unaffordable chain before side effects");
        var disabled = new Harness();
        DocumentGoals.Add(disabled.Session, new("disabled", "Cannot normalize without tool", "no.txt", [], "text"));
        Check((await new GoalOrchestrator(disabled.Session, disabled.Policy with { AllowedTools = ["text.create", "text.read"] }).RunAsync()).Goals.Single().Status == GoalStatus.Blocked,
            "planner respects host tool grants");
        var exhausted = new Harness();
        DocumentGoals.Add(exhausted.Session, new("exhausted", "Limited retries", "no.txt", ["missing-a.txt", "missing-b.txt"], MaxReplans: 0));
        var exhaustedState = await exhausted.Run();
        Check(exhaustedState.Goals.Single().Status == GoalStatus.Blocked && exhaustedState.Episodes.Length == 1, "actual failure respects replan limit");

        var restart = new Harness();
        DocumentGoals.Add(restart.Session, new("restart", "Resume pending step", "restart.txt", [], "resume", Normalize: false));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new GoalOrchestrator(restart.Session, restart.Policy).RunAsync(cancelled.Token); } catch (OperationCanceledException) { }
        Check((await restart.Run()).Goals.Single().Status == GoalStatus.Completed, "persisted goal resumes after cancellation before execution");
        var multiple = new Harness();
        DocumentGoals.Add(multiple.Session, new("one", "First goal", "one.txt", [], "one", Normalize: false));
        DocumentGoals.Add(multiple.Session, new("two", "Second goal", "two.txt", [], "two", Normalize: false));
        Check((await multiple.Run()).Goals.All(g => g.Status == GoalStatus.Completed), "multiple goals share cognitive core and tool workers");

        var changed = new Harness();
        DocumentGoals.Add(changed.Session, new("changed", "Check actual output", "changed.txt", [], "original", Normalize: false));
        var driver = new GoalOrchestrator(changed.Session, changed.Policy);
        driver.PrepareNextSteps();
        await new MultiTaskRuntime(changed.Session, new WorkspaceToolExecutor(changed.Session, changed.Policy)).RunAsync();
        File.WriteAllText(Path.Combine(changed.Root, "changed.txt"), "external change");
        Check((await changed.Run()).Goals.Single().Status == GoalStatus.Blocked, "verification rejects changed output instead of trusting planned effects");

        var uncertain = new Harness();
        DocumentGoals.Add(uncertain.Session, new("unknown", "Do not repeat unknown writes", "unknown.txt", [], "once", Normalize: false));
        new GoalOrchestrator(uncertain.Session, uncertain.Policy).PrepareNextSteps();
        uncertain.Session.ThinkNext(DateTimeOffset.UtcNow);
        var dispatch = uncertain.Session.DispatchNext()!;
        var receipt = await new WorkspaceToolExecutor(uncertain.Session, uncertain.Policy).ExecuteAsync(dispatch, CancellationToken.None);
        var unknown = await uncertain.Run();
        Check(unknown.Goals.Single().Status == GoalStatus.NeedsReconciliation && unknown.Episodes.Length == 1,
            "restart preserves unknown outcome and never repeats a dispatched write");
        uncertain.Session.Complete(receipt);
        var resumed = await uncertain.Run();
        Check(resumed.Goals.Single().Status == GoalStatus.Completed && resumed.Episodes.Length == 2,
            "reconciled receipt resumes remaining step without duplicate write");
    }
    private sealed class Harness
    {
        public string Root { get; }
        public LearningSession Session { get; }
        public WorkspaceToolPolicy Policy { get; }
        public Harness()
        {
            var parent = Path.Combine(Path.GetTempPath(), "littleloop-goals-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(parent, "workspace"); Directory.CreateDirectory(Root);
            Session = new(Path.Combine(parent, "state.json")); Session.Initialize(); Session.EnableScheduling(new(2, 100));
            Policy = new(Root, WorkspaceTools.List().Select(t => t.Id).ToArray());
        }
        public Task<LearningState> Run() => new GoalOrchestrator(Session, Policy).RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
}
