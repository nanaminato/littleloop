using System.Collections.Concurrent;
using System.Threading.Channels;
using LittleLoop.Core;

internal static class SchedulingChecks
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
    private static LearningSession Session(int slots, int budget)
    {
        string directory = Path.Combine(Path.GetTempPath(), "littleloop-scheduling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var session = new LearningSession(Path.Combine(directory, "state.json"));
        session.Initialize(); session.EnableScheduling(new(slots, budget));
        return session;
    }
    private static LearningTask Work(string id, int budget = 2) => new(id, "Resolve task " + id,
        new() { ["case"] = id }, [new("resolve", "resolved", 1, true)],
        [new("act", ChoiceKind.Act, "Resolve", 0, .1m, "Available action", "resolve", 1)
            { Meaning = new("resolve", ["resolved"], [], []) }], [], [], budget, ["resolve"]);
    private static ActionResult Result(ExecutionRequest request) => new OutcomeSimulation(new() { ["resolve"] = ["resolved"] }).Execute(request.Trace);

    public static async Task Run()
    {
        var session = Session(1, 1);
        session.Enqueue(Work("low")); session.Enqueue(Work("urgent"), 10);
        var thought = session.ThinkNext(Now)!;
        Check(thought.Trace.Input.Id.Contains("urgent"), "priority selects next cognitive task");
        Check(session.Read().Runtime!.Tasks.Single(x => x.Task.Id == "urgent").Status == WorkStatus.WaitingExecution, "thinking and execution are separate states");
        var deferred = session.ThinkNext(Now)!;
        Check(deferred.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "planning reserves shared budget before dispatch");
        var request = session.DispatchNext()!;
        Check(session.DispatchNext() is null, "dispatch concurrency limit enforced");
        session.Complete(Result(request));
        Check(session.Read().Runtime!.Tasks.Single(x => x.Task.Id == "urgent").Status == WorkStatus.Completed, "receipt completes only associated task");
        Throws<ArgumentException>(() => session.Begin(Work("bypass")), "standalone execution cannot bypass scheduled role budget");
        session.ReviseTask(Work("urgent"), 1);
        Check(session.ThinkNext(Now)!.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "revising a task does not reset consumed role budget");
        var state = session.Read(); session.AddRuntimeBudget(1, state.Revision);
        Throws<ArgumentException>(() => session.AddRuntimeBudget(1, state.Revision), "stale budget update rejected");
        session.ReviseTask(Work("low"), 1);
        Check(session.ThinkNext(Now)!.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Act, "explicit budget increment permits a new action");

        var queued = Session(1, 1); queued.Enqueue(Work("a")); queued.Enqueue(Work("b"));
        var oldPlan = queued.ThinkNext(Now)!;
        queued.CancelTask("a", 1);
        Check(queued.Read().Episodes.Single(e => e.Trace.Input.Id == oldPlan.Trace.Input.Id).Result!.Status == ExecutionStatus.NotExecuted,
            "cancelling queued plan records non-execution");
        Check(queued.ThinkNext(Now)!.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Act, "cancelling undelivered plan releases reservation");
        Throws<ArgumentException>(() => queued.ReviseTask(Work("a"), 1), "stale task update rejected");

        var fairness = Session(1, 0); fairness.Enqueue(Work("low"));
        bool chosen = false;
        for (int i = 0; i < 4; i++)
        {
            fairness.Enqueue(Work("high-" + i), 2);
            if (fairness.ThinkNext(Now)!.Trace.Input.Id.Contains("task:low/")) { chosen = true; break; }
        }
        Check(chosen, "waiting task ages past a stream of newer priority work");
        var deadline = Session(1, 0); deadline.Enqueue(Work("normal"), 99); deadline.Enqueue(Work("overdue"), 0, Now.AddMinutes(-1));
        Check(deadline.ThinkNext(Now)!.Trace.Input.Id.Contains("overdue"), "recorded time and deadline affect attention");
        var taskBudget = Session(1, 3); taskBudget.Enqueue(Work("bounded", 1)); taskBudget.ThinkNext(Now);
        taskBudget.Complete(Result(taskBudget.DispatchNext()!)); taskBudget.ReviseTask(Work("bounded", 1), 1);
        Check(taskBudget.ThinkNext(Now)!.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Defer, "task budget is cumulative across input revisions");

        await ConcurrentDriver();
        await UncertainRecovery();
        await Cancellation();
    }

    private static async Task ConcurrentDriver()
    {
        var session = Session(2, 5);
        foreach (var id in new[] { "a", "b", "c" }) session.Enqueue(Work(id));
        var executor = new ControlledExecutor();
        var runtime = new MultiTaskRuntime(session, executor);
        var run = runtime.RunAsync();
        var first = await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var second = await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check(first.TaskId != second.TaskId, "two independent executions overlap");
        try { await new MultiTaskRuntime(session, new ImmediateExecutor()).RunAsync(); throw new Exception("Second driver acquired lease"); }
        catch (IOException) { Check(true, "one role admits only one driver loop"); }
        var observed = await Until(runtime, s => s.Runtime!.Tasks.Single(t => t.Task.Id == "c").Status == WorkStatus.WaitingExecution);
        Check(observed.Episodes.Length == 3, "core thinks about third task while two tools are waiting");
        var workD = await runtime.EnqueueAsync(Work("d"));
        await Until(runtime, s => s.Runtime!.Tasks.Single(t => t.Task.Id == "d").Status == WorkStatus.WaitingExecution);
        Check(workD.Version == 1, "serial inbox accepts new task during execution");
        var oldA = first.TaskId == "a" ? first : second;
        var oldB = first.TaskId == "b" ? first : second;
        try { await runtime.ReconcileAsync(Result(oldA)); throw new Exception("Active execution reconciled prematurely"); }
        catch (ArgumentException) { Check(true, "reconciliation cannot prematurely free an active worker slot"); }
        await runtime.ReviseAsync(Work("a") with { Facts = new() { ["case"] = "a", ["revision"] = "new" } }, 1);
        await Until(runtime, s => s.Runtime!.Tasks.Single(t => t.Task.Id == "a").Status == WorkStatus.WaitingExecution);
        executor.Release(oldB);
        var third = await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check(third.TaskId == "c", "free slot dispatches oldest planned action");
        executor.Release(oldA);
        var fourth = await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check(fourth.TaskId == "d", "out-of-order result frees the right execution slot");
        observed = await runtime.ReadAsync();
        var currentA = observed.Runtime!.Tasks.Single(t => t.Task.Id == "a");
        Check(currentA.Version == 2 && currentA.Status == WorkStatus.WaitingExecution &&
            observed.Runtime.Events.Any(e => e.Kind == "stale-result"), "late old-version result cannot overwrite revised task");
        await runtime.FeedbackAsync(new("old-a-review", oldA.Trace.Input.Id, null, false, [], "reviewer", "Old task result corrected."));
        Check((await runtime.ReadAsync()).Runtime!.Tasks.Single(t => t.Task.Id == "a").Version == 2, "old feedback does not roll back current version");
        executor.Release(third);
        var fifth = await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check(fifth.TaskId == "a" && fifth.TaskVersion == 2, "new task version receives its own execution request");
        executor.Release(fourth); executor.Release(fifth);
        var done = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Check(executor.Peak == 2 && done.Runtime!.Attempts.Sum(a => a.Cost) == 5, "execution cap and global budget maintained across revisions");
        Check(done.Runtime!.Tasks.All(w => w.Status == WorkStatus.Completed), "serial driver drains independent tasks to completion");
        Check(done.Episodes.Select(e => e.Trace.Input.Id).Distinct().Count() == 5, "each attempt retains isolated replay snapshot");
        foreach (var episode in done.Episodes)
            Check(System.Text.Json.JsonSerializer.Serialize(DeliberationPolicy.Decide(episode.Trace.Input, episode.Trace.Responses)) ==
                System.Text.Json.JsonSerializer.Serialize(episode.Trace.Decision), "scheduled decision remains replayable");
        session.AddRuntimeBudget(1, done.Revision);
        session.Enqueue(Work("learned") with { Facts = new() { ["case"] = "b" }, Options = [] });
        var learned = await new MultiTaskRuntime(session, new ImmediateExecutor()).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var learnedTask = learned.Runtime!.Tasks.Single(w => w.Task.Id == "learned");
        var learnedEpisode = learned.Episodes.Single(e => e.Trace.Input.Id == learnedTask.CurrentDecisionId);
        Check(learnedTask.Status == WorkStatus.Completed && learnedEpisode.Trace.Decision.Selected.Proposal.Id.StartsWith("recall:"),
            "later scheduled task automatically uses shared execution experience");
    }

    private static async Task UncertainRecovery()
    {
        var session = Session(1, 2); session.Enqueue(Work("a")); session.Enqueue(Work("b"));
        var broken = new ThrowingExecutor();
        var state = await new MultiTaskRuntime(session, broken).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(state.Runtime!.Attempts.Count(a => a.Status == AttemptStatus.Uncertain) == 1 && broken.Calls == 1, "unknown side effects retain their slot and budget");
        var retry = new ImmediateExecutor();
        await new MultiTaskRuntime(session, retry).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(retry.Calls == 0, "restart does not automatically rerun uncertain execution");
        var attempt = state.Runtime.Attempts.Single(a => a.Status == AttemptStatus.Uncertain);
        var trace = state.Episodes.Single(e => e.Trace.Input.Id == attempt.DecisionId).Trace;
        using var cancellation = new CancellationTokenSource();
        var live = new MultiTaskRuntime(session, retry);
        var service = live.RunAsync(stopWhenIdle: false, cancellationToken: cancellation.Token);
        await live.ReconcileAsync(Result(new(attempt.TaskId, attempt.TaskVersion, trace)));
        await Until(live, s => s.Runtime!.Tasks.All(w => w.Status == WorkStatus.Completed));
        Check(retry.Calls == 1, "reconciliation wakes persistent driver and releases waiting task");
        var current = await live.ReadAsync();
        var increased = await live.AddBudgetAsync(1, current.Revision);
        Check(increased.Runtime!.Limits.TotalActionBudget == 3, "explicit budget update flows through serial inbox");
        cancellation.Cancel();
        try { await service.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Service ignored cancellation"); }
        catch (OperationCanceledException) { }
    }

    private static async Task Cancellation()
    {
        var session = Session(1, 1); session.Enqueue(Work("cancel"));
        var executor = new ControlledExecutor(); using var cancellation = new CancellationTokenSource();
        var run = new MultiTaskRuntime(session, executor).RunAsync(cancellationToken: cancellation.Token);
        await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        try { await run.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(session.Read().Runtime!.Tasks.Single().Status == WorkStatus.NeedsReconciliation, "driver cancellation preserves unknown execution without inventing failure");
    }

    private static async Task<LearningState> Until(MultiTaskRuntime runtime, Func<LearningState, bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            var state = await runtime.ReadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (condition(state)) return state;
            await Task.Yield();
        }
        throw new Exception("Driver did not reach expected state.");
    }
    private sealed class ControlledExecutor : IAsyncActionExecutor
    {
        public readonly Channel<ExecutionRequest> Started = Channel.CreateUnbounded<ExecutionRequest>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> gates = new();
        private int active;
        public int Peak;
        public async Task<ActionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates[request.Trace.Input.Id] = gate;
            int count = Interlocked.Increment(ref active);
            int peak;
            do { peak = Peak; } while (count > peak && Interlocked.CompareExchange(ref Peak, count, peak) != peak);
            Started.Writer.TryWrite(request);
            try { await gate.Task.WaitAsync(cancellationToken); return Result(request); }
            finally { Interlocked.Decrement(ref active); }
        }
        public void Release(ExecutionRequest request) => gates[request.Trace.Input.Id].SetResult();
    }
    private sealed class ThrowingExecutor : IAsyncActionExecutor
    {
        public int Calls;
        public Task<ActionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); throw new IOException("Unknown tool result"); }
    }
    private sealed class ImmediateExecutor : IAsyncActionExecutor
    {
        public int Calls;
        public Task<ActionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(Result(request)); }
    }
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine($"PASS {label}"); }
    private static void Throws<T>(Action action, string label) where T : Exception
    { try { action(); } catch (T) { Console.WriteLine($"PASS {label}"); return; } throw new Exception("Expected failure: " + label); }
}
