using System.Text.Json;
using LittleLoop.Core;

internal static class LearningChecks
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "littleloop-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.json");
        var session = new LearningSession(path);
        session.Initialize();
        Proposal fast = new("fast", ChoiceKind.Act, "Direct routing", 0, .05m, "Initial route", "ticket.route", 1)
        { Meaning = new("direct", ["resolved", "archived"], ["fast"], []) };
        Proposal review = new("review", ChoiceKind.Act, "Specialist review", 0, .1m, "Alternative route", "ticket.route", 1)
        { Meaning = new("review", ["resolved"], ["careful"], []) };
        LearningTask Task(string id, Proposal[]? options = null) => new(id, "Resolve billing dispute",
            new() { ["category"] = "billing" }, [new("resolve", "resolved", 10, true)], options ?? [fast, review], [], [], 1, ["ticket.route"]);
        var simulator = new OutcomeSimulation(new() { ["direct"] = [], ["review"] = ["resolved"] });
        var first = session.Run(Task("first"), simulator);
        Check(first.Trace.Decision.Selected.Proposal.Id == "fast" && first.Result!.Status == ExecutionStatus.Completed,
            "first execution observed, completed does not imply goal success");
        Check(first.Trace.Input.Cognition!.Experiences.Length == 0, "starts with no hand-authored experience");
        session = new LearningSession(path); // Independent instance reloads durable memory.
        var second = session.Run(Task("second"), simulator);
        Check(second.Trace.Decision.Selected.Proposal.Meaning!.Strategy == "review", "actual failure changes next decision after restart");
        Check(second.Trace.Input.Cognition!.Experiences.Any(e => !e.Succeeded && e.Lesson.Contains("Unmet predictions")), "automatic prediction error lesson");
        var third = session.Run(Task("third", []), simulator);
        Check(third.Trace.Decision.Selected.Proposal.Meaning!.Strategy == "review", "successful execution recalled without supplied options or role");
        Check(third.Trace.Input.Cognition!.Experiences.Any(e => e.Source.Contains($"result:{second.Result!.Id}")), "learned experience links execution source");
        var before = session.Read().Revision;
        session.Complete(third.Result!);
        Check(session.Read().Revision == before, "result retries are idempotent");
        Throws<ArgumentException>(() => session.Run(Task("third"), simulator), "duplicate decision cannot re-execute");
        Throws<ArgumentException>(() => session.Complete(first.Result! with { ProposalId = "different" }), "receipt linkage enforced");
        var correction = new ResultFeedback("review-first", "first", null, true, ["resolved"], "reviewer", "Delayed confirmation shows the ticket was resolved.");
        session.Feedback(correction);
        before = session.Read().Revision;
        session.Feedback(correction);
        Check(session.Read().Revision == before, "feedback retries do not duplicate learning");
        var afterCorrection = session.Run(Task("after-correction", [fast]), simulator);
        Check(afterCorrection.Trace.Decision.Selected.Proposal.Meaning!.Strategy == "direct", "feedback revises earlier failed judgment");
        var learned = afterCorrection.Trace.Input.Cognition!.Experiences.Single(e => e.Id == "episode:first");
        Check(learned.Succeeded && learned.Action.Meaning!.Outcomes.SequenceEqual(["resolved"]), "observed outcomes replace unfulfilled predicted effects");
        Check(session.Read().Episodes[0].Result!.ActualOutcomes.Length == 0, "correction preserves original receipt");
        Throws<ArgumentException>(() => session.Feedback(correction with { Id = "stale", Succeeded = false }), "stale feedback cannot overwrite newer review");
        Throws<ArgumentException>(() => session.Feedback(correction with { Id = "invalid", Supersedes = correction.Id, ActualOutcomes = [] }), "success feedback requires observed required goals");
        session.Feedback(correction with { Id = "review-again", Supersedes = correction.Id, Succeeded = false, ActualOutcomes = [], Reason = "Confirmation referred to another ticket." });
        var revised = session.Begin(Task("revised", [fast]));
        Check(revised.Trace.Decision.Selected.Proposal.Meaning!.Strategy == "review", "linked feedback correction affects later decision");
        Check(JsonSerializer.Serialize(DeliberationPolicy.Decide(first.Trace.Input, first.Trace.Responses)) == JsonSerializer.Serialize(first.Trace.Decision), "old trace replays independently of later memory and feedback");
        Check(session.Read().Episodes[0].Feedback.Length == 2, "feedback audit chain retained");
        var unrelated = session.Begin(Task("unrelated") with { Facts = new() { ["category"] = "outage" } });
        Check(unrelated.Trace.Input.Cognition!.Experiences.Length == 0, "memory constrained to recorded context");
        var blocked = session.Run(Task("blocked") with { AllowedCapabilities = [] }, simulator);
        Check(blocked.Result!.Status == ExecutionStatus.NotExecuted, "permission denial produces no execution experience");
        Throws<ArgumentException>(() => session.Feedback(new("invented", "blocked", null, true, ["resolved"], "reviewer", "Unsupported")), "cannot turn non-execution into successful experience");
        var noActionMemory = session.Begin(Task("no-action-memory"));
        Check(noActionMemory.Trace.Input.Cognition!.Experiences.All(e => e.Id != "episode:blocked" && e.Id != "episode:revised"), "unexecuted and pending decisions do not teach success or failure");
        Throws<InvalidOperationException>(() => session.Run(Task("crash"), new CrashingExecutor()), "executor exception preserved");
        Check(session.Read().Episodes.Single(e => e.Trace.Input.Id == "crash").Result is null, "executor crash leaves recoverable pending decision");
        Throws<IOException>(() => session.Initialize(), "initialization cannot erase existing history");
        using (var locked = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws<IOException>(() => new LearningSession(path).Read(), "concurrent writer lock prevents lost updates");
        Check(session.Read().Role.Boundaries.Length == 0 && session.Read().Role.Preferences.Length == 0, "execution does not silently rewrite role commitments");
        var invalidTask = Task("invalid") with { Goals = null! };
        before = session.Read().Revision;
        Throws<ArgumentException>(() => session.Begin(invalidTask), "invalid task rejected before memory retrieval");
        Check(session.Read().Revision == before, "invalid transition leaves state intact");
        var otherGoal = session.Begin(Task("other-goal", []) with { Goals = [new("archive", "archived", 1, true)] });
        Check(otherGoal.Trace.Input.Cognition!.Experiences.All(e => e.Succeeded), "failure is scoped to original goal outcomes");
        Check(otherGoal.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "unobserved predicted effects cannot be recalled as success");
        var pending = session.Read().Episodes.Single(e => e.Trace.Input.Id == "crash");
        var recovered = simulator.Execute(pending.Trace);
        session.Complete(recovered);
        Check(session.Read().Episodes.Single(e => e.Trace.Input.Id == "crash").Result is not null, "pending decision accepts correlated recovery receipt");
        Throws<ArgumentException>(() => session.Run(Task("wrong-executor"), new FixedExecutor(second.Result!)), "executor cannot complete another decision");
        Check(session.Read().Episodes.Single(e => e.Trace.Input.Id == "wrong-executor").Result is null, "mismatched executor result leaves original pending");
        Throws<ArgumentException>(() => new OutcomeSimulation(new() { ["bad"] = ["same", "same"] }), "invalid simulated environment rejected before execution");
    }
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine($"PASS {label}"); }
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T) { Console.WriteLine($"PASS {label}"); return; }
        throw new Exception($"Expected {typeof(T).Name}: {label}");
    }
    private sealed class CrashingExecutor : IActionExecutor
    {
        public ActionResult Execute(DeliberationTrace trace) => throw new InvalidOperationException("executor disconnected");
    }
    private sealed class FixedExecutor(ActionResult result) : IActionExecutor
    {
        public ActionResult Execute(DeliberationTrace trace) => result;
    }
}
