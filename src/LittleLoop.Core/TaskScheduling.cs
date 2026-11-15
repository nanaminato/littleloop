namespace LittleLoop.Core;

public enum WorkStatus { Ready, WaitingExecution, Executing, WaitingFeedback, Deferred, Refused, Completed, Cancelled, NeedsReconciliation }
public enum AttemptStatus { Planned, Dispatched, Uncertain, Recorded, Abandoned }
public sealed record RuntimeLimits(int MaxConcurrentExecutions, int TotalActionBudget);
public sealed record WorkItem(LearningTask Task, long Version, int Priority, DateTimeOffset? DueAt,
    long ReadyTurn, WorkStatus Status, string? CurrentDecisionId);
public sealed record ExecutionAttempt(string TaskId, long TaskVersion, string DecisionId, int Cost, AttemptStatus Status);
public sealed record RuntimeEvent(long Sequence, string Kind, string TaskId, long TaskVersion,
    string? DecisionId, DateTimeOffset At, string Detail);
public sealed record TaskRuntimeState(RuntimeLimits Limits, long Turn, WorkItem[] Tasks,
    ExecutionAttempt[] Attempts, RuntimeEvent[] Events);
public sealed record ExecutionRequest(string TaskId, long TaskVersion, DeliberationTrace Trace);

public sealed partial class LearningSession
{
    // An executor checks the actual current task, not just the authorization in an old trace.
    // A short shared state gate serializes authorization reads with persisted transitions;
    // the filesystem tool itself runs outside this gate.
    public bool IsExecutionCurrent(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var guard = Lock();
        var state = Load();
        var runtime = state.Runtime;
        if (runtime is null) return false;
        var work = runtime.Tasks.SingleOrDefault(w => w.Task.Id == request.TaskId);
        var attempt = runtime.Attempts.SingleOrDefault(a => a.DecisionId == request.Trace.Input.Id);
        var episode = state.Episodes.SingleOrDefault(e => e.Trace.Input.Id == request.Trace.Input.Id);
        var proposal = request.Trace.Decision.Selected.Proposal;
        return work is not null && attempt is not null && episode is not null && episode.Result is null &&
            work.Version == request.TaskVersion && attempt.TaskVersion == request.TaskVersion &&
            work.CurrentDecisionId == attempt.DecisionId && attempt.Status == AttemptStatus.Dispatched &&
            work.Status == WorkStatus.Executing && Equal(episode.Trace, request.Trace) &&
            work.Task.AllowedCapabilities.Contains(proposal.Capability, StringComparer.Ordinal);
    }

    public LearningState EnableScheduling(RuntimeLimits limits)
    {
        using var guard = Lock();
        var state = Load();
        CheckLimits(limits);
        if (state.Runtime is not null) throw new ArgumentException("Scheduling is already configured.");
        if (state.Episodes.Any(e => e.Result is null)) throw new ArgumentException("Reconcile existing pending decisions before enabling scheduling.");
        state = state with { Runtime = new(limits, 0, [], [], []) };
        Commit(state);
        return Clone(state with { Revision = state.Revision + 1 });
    }

    public WorkItem Enqueue(LearningTask task, int priority = 0, DateTimeOffset? dueAt = null)
    {
        using var guard = Lock();
        var state = Load();
        var runtime = RequireRuntime(state);
        TaskInput(state, task);
        CheckPriority(priority);
        if (runtime.Tasks.Any(w => w.Task.Id == task.Id)) throw new ArgumentException("Task already exists; revise it with its expected version.");
        var work = new WorkItem(Clone(task), 1, priority, dueAt, runtime.Turn, WorkStatus.Ready, null);
        runtime = runtime with { Tasks = [.. runtime.Tasks, work] };
        Commit(state with { Runtime = Log(runtime, "enqueued", work, "Task ready for attention.") });
        return Clone(work);
    }

    public WorkItem ReviseTask(LearningTask task, long expectedVersion, int priority = 0, DateTimeOffset? dueAt = null)
    {
        using var guard = Lock();
        var state = Load();
        var runtime = RequireRuntime(state);
        TaskInput(state, task);
        CheckPriority(priority);
        int index = WorkIndex(runtime, task.Id);
        var old = runtime.Tasks[index];
        CheckVersion(old, expectedVersion);
        state = AbandonPlanned(state, old);
        runtime = state.Runtime!;
        var work = new WorkItem(Clone(task), checked(old.Version + 1), priority, dueAt, runtime.Turn, WorkStatus.Ready, null);
        runtime.Tasks[index] = work;
        Commit(state with { Runtime = Log(runtime, "revised", work, "Older execution results may be archived but cannot overwrite this version.") });
        return Clone(work);
    }

    public WorkItem CancelTask(string taskId, long expectedVersion)
    {
        using var guard = Lock();
        var state = Load();
        var runtime = RequireRuntime(state);
        int index = WorkIndex(runtime, taskId);
        var old = runtime.Tasks[index];
        CheckVersion(old, expectedVersion);
        state = AbandonPlanned(state, old);
        runtime = state.Runtime!;
        var work = old with { Version = checked(old.Version + 1), Status = WorkStatus.Cancelled, CurrentDecisionId = null };
        runtime.Tasks[index] = work;
        Commit(state with { Runtime = Log(runtime, "cancelled", work, "Queued action abandoned; already dispatched side effects are not undone.") });
        return Clone(work);
    }

    // One cognitive turn, serialized with every other role state transition.
    // Planning does not wait for execution slots; it reserves both role and task budgets atomically.
    public LearningEpisode? ThinkNext(DateTimeOffset now, PluginHost? host = null)
    {
        using var guard = Lock();
        var state = Load();
        var runtime = RequireRuntime(state);
        var work = runtime.Tasks.Where(w => w.Status == WorkStatus.Ready)
            .OrderByDescending(w => Attention(w, runtime.Turn, now))
            .ThenBy(w => w.ReadyTurn).ThenBy(w => w.Task.Id, StringComparer.Ordinal).FirstOrDefault();
        if (work is null) return null;
        int available = Math.Max(0, Math.Min(runtime.Limits.TotalActionBudget - Used(runtime),
            work.Task.Budget - Used(runtime, work.Task.Id)));
        string decisionId = $"task:{work.Task.Id}/v{work.Version}/turn{runtime.Turn}";
        var episode = PrepareEpisode(state, work.Task with { Id = decisionId, Budget = available }, host);
        var selected = episode.Trace.Decision.Selected.Proposal;
        var status = selected.Kind switch
        {
            ChoiceKind.Act => WorkStatus.WaitingExecution,
            ChoiceKind.Refuse => WorkStatus.Refused,
            ChoiceKind.Defer => WorkStatus.Deferred,
            _ => WorkStatus.WaitingFeedback
        };
        var updated = work with { Status = status, CurrentDecisionId = decisionId };
        runtime.Tasks[WorkIndex(runtime, work.Task.Id)] = updated;
        runtime = Log(runtime, "thought", updated,
            $"Attention={Attention(work, runtime.Turn, now)}; priority={work.Priority}; waitingTurns={runtime.Turn - work.ReadyTurn}; availableBudget={available}.", now);
        runtime = runtime with { Turn = checked(runtime.Turn + 1) };
        if (selected.Kind == ChoiceKind.Act)
        {
            runtime = runtime with { Attempts = [.. runtime.Attempts, new(work.Task.Id, work.Version, decisionId, selected.Cost, AttemptStatus.Planned)] };
            runtime = Log(runtime, "reserved", updated, $"Reserved {selected.Cost} action units; waiting for an executor slot.", now);
        }
        else episode = episode with { Result = new($"no-execution:{decisionId}", decisionId, selected.Id,
            episode.Trace.Decision.Selected.Source, ExecutionStatus.NotExecuted, [], "task-runtime/1", selected.Reason, false) };
        Commit(state with { Runtime = runtime, Episodes = [.. state.Episodes, episode] });
        return Clone(episode);
    }

    public ExecutionRequest? DispatchNext()
    {
        using var guard = Lock();
        var state = Load();
        var runtime = RequireRuntime(state);
        if (runtime.Attempts.Count(a => a.Status is AttemptStatus.Dispatched or AttemptStatus.Uncertain) >= runtime.Limits.MaxConcurrentExecutions)
            return null;
        int index = Array.FindIndex(runtime.Attempts, a => a.Status == AttemptStatus.Planned);
        if (index < 0) return null;
        var attempt = runtime.Attempts[index];
        int workIndex = WorkIndex(runtime, attempt.TaskId);
        var work = runtime.Tasks[workIndex];
        if (work.Version != attempt.TaskVersion || work.CurrentDecisionId != attempt.DecisionId)
            throw new InvalidDataException("Stale queued action must be abandoned before dispatch.");
        var episode = state.Episodes[Find(state, attempt.DecisionId)];
        runtime.Attempts[index] = attempt with { Status = AttemptStatus.Dispatched };
        runtime.Tasks[workIndex] = work with { Status = WorkStatus.Executing };
        Commit(state with { Runtime = Log(runtime, "dispatched", runtime.Tasks[workIndex], "Execution slot acquired; reserved cost is now charged even if outcome becomes unknown.") });
        return new(attempt.TaskId, attempt.TaskVersion, Clone(episode.Trace));
    }

    public void MarkExecutionUncertain(string decisionId, string detail)
    {
        using var guard = Lock();
        var state = Load();
        var runtime = RequireRuntime(state);
        int index = Array.FindIndex(runtime.Attempts, a => a.DecisionId == decisionId);
        if (index < 0 || !Text(detail)) throw new ArgumentException("Unknown dispatch or missing error detail.");
        var attempt = runtime.Attempts[index];
        if (attempt.Status is AttemptStatus.Uncertain or AttemptStatus.Recorded) return;
        if (attempt.Status != AttemptStatus.Dispatched) throw new ArgumentException("Only dispatched actions can have an unknown outcome.");
        runtime.Attempts[index] = attempt with { Status = AttemptStatus.Uncertain };
        int workIndex = WorkIndex(runtime, attempt.TaskId);
        var work = runtime.Tasks[workIndex];
        if (work.Version == attempt.TaskVersion && work.CurrentDecisionId == decisionId)
            runtime.Tasks[workIndex] = work with { Status = WorkStatus.NeedsReconciliation };
        runtime = LogAttempt(runtime, "uncertain", attempt, detail);
        Commit(state with { Runtime = runtime });
    }

    public LearningState AddRuntimeBudget(int amount, long expectedRevision)
    {
        using var guard = Lock();
        var state = Load();
        var runtime = RequireRuntime(state);
        if (amount <= 0 || state.Revision != expectedRevision) throw new ArgumentException("Positive budget increment and current state revision required.");
        runtime = runtime with { Limits = runtime.Limits with { TotalActionBudget = checked(runtime.Limits.TotalActionBudget + amount) } };
        runtime = runtime with { Events = [.. runtime.Events, new(runtime.Events.LongLength + 1, "budget", "", 0,
            null, DateTimeOffset.UtcNow, $"Explicit budget increase: +{amount}, total={runtime.Limits.TotalActionBudget}.")] };
        Commit(state with { Runtime = runtime });
        return Clone(state with { Revision = state.Revision + 1, Runtime = runtime });
    }

    // A runner lease prevents two driver loops from claiming one role concurrently.
    internal FileStream AcquireRunnerLease() => new(path + ".runner.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static LearningState ApplyRuntimeResult(LearningState state, LearningEpisode episode)
    {
        if (state.Runtime is not { } runtime) return state;
        int index = Array.FindIndex(runtime.Attempts, a => a.DecisionId == episode.Trace.Input.Id);
        if (index < 0) return state; // Pre-scheduler history and non-action decisions.
        var attempt = runtime.Attempts[index];
        if (attempt.Status is not (AttemptStatus.Dispatched or AttemptStatus.Uncertain))
            throw new ArgumentException("Result requires a dispatched action, not a queued or abandoned plan.");
        runtime.Attempts[index] = attempt with { Status = AttemptStatus.Recorded };
        int workIndex = WorkIndex(runtime, attempt.TaskId);
        var work = runtime.Tasks[workIndex];
        bool current = work.Version == attempt.TaskVersion && work.CurrentDecisionId == attempt.DecisionId;
        if (current)
        {
            bool succeeded = episode.Result!.Status == ExecutionStatus.Completed && GoalsMet(episode.Trace.Input.Cognition!.Goals, episode.Result.ActualOutcomes);
            runtime.Tasks[workIndex] = work with { Status = succeeded ? WorkStatus.Completed : WorkStatus.WaitingFeedback };
        }
        return state with { Runtime = LogAttempt(runtime, current ? "result" : "stale-result", attempt,
            current ? "Execution result applied to this task version and archived for learning." : "Result archived against its original snapshot; newer task state unchanged.") };
    }

    private static LearningState ApplyRuntimeFeedback(LearningState state, LearningEpisode episode)
    {
        if (state.Runtime is not { } runtime) return state;
        var attempt = runtime.Attempts.SingleOrDefault(a => a.DecisionId == episode.Trace.Input.Id);
        if (attempt is null) return state;
        int index = WorkIndex(runtime, attempt.TaskId);
        var work = runtime.Tasks[index];
        bool current = work.Version == attempt.TaskVersion && work.CurrentDecisionId == attempt.DecisionId;
        if (current) runtime.Tasks[index] = work with { Status = episode.Feedback[^1].Succeeded ? WorkStatus.Completed : WorkStatus.WaitingFeedback };
        return state with { Runtime = LogAttempt(runtime, current ? "feedback" : "stale-feedback", attempt,
            $"Feedback {episode.Feedback[^1].Id} recorded; current task state {(current ? "updated" : "preserved")}.") };
    }

    private static LearningState AbandonPlanned(LearningState state, WorkItem work)
    {
        var runtime = state.Runtime!;
        for (int i = 0; i < runtime.Attempts.Length; i++)
        {
            var attempt = runtime.Attempts[i];
            if (attempt.TaskId != work.Task.Id || attempt.Status != AttemptStatus.Planned) continue;
            int episodeIndex = Find(state, attempt.DecisionId);
            var episode = state.Episodes[episodeIndex];
            var selected = episode.Trace.Decision.Selected;
            state.Episodes[episodeIndex] = episode with { Result = new($"abandoned:{attempt.DecisionId}", attempt.DecisionId,
                selected.Proposal.Id, selected.Source, ExecutionStatus.NotExecuted, [], "task-runtime/1", "Task revised or cancelled before dispatch.", false) };
            runtime.Attempts[i] = attempt with { Status = AttemptStatus.Abandoned };
            runtime = LogAttempt(runtime, "abandoned", attempt, "Queued execution removed; budget reservation released.");
        }
        return state with { Runtime = runtime };
    }

    private static long Attention(WorkItem work, long turn, DateTimeOffset now) =>
        checked(work.Priority + (turn - work.ReadyTurn) + (work.DueAt is { } due && due <= now ? 100L : 0L));
    private static int Used(TaskRuntimeState runtime, string? taskId = null) => runtime.Attempts
        .Where(a => a.Status != AttemptStatus.Abandoned && (taskId is null || a.TaskId == taskId)).Sum(a => a.Cost);
    private static void CheckLimits(RuntimeLimits limits)
    {
        if (limits is null || limits.MaxConcurrentExecutions is < 1 or > 64 || limits.TotalActionBudget < 0)
            throw new ArgumentException("Execution concurrency must be 1..64 and total action budget nonnegative.");
    }
    private static void CheckPriority(int priority)
    { if (priority is < 0 or > 100) throw new ArgumentException("Task priority must be 0..100."); }
    private static void CheckVersion(WorkItem work, long expected)
    { if (work.Version != expected) throw new ArgumentException("Task version changed; reload before revising it."); }
    private static TaskRuntimeState RequireRuntime(LearningState state) => state.Runtime ?? throw new ArgumentException("Enable scheduling first.");
    private static int WorkIndex(TaskRuntimeState runtime, string id)
    {
        int index = Array.FindIndex(runtime.Tasks, w => w.Task.Id == id);
        return index < 0 ? throw new ArgumentException("Unknown task ID.") : index;
    }
    private void Commit(LearningState state) => Save(state with { Revision = checked(state.Revision + 1) });
    private static TaskRuntimeState Log(TaskRuntimeState runtime, string kind, WorkItem work, string detail, DateTimeOffset? at = null) =>
        runtime with { Events = [.. runtime.Events, new(runtime.Events.LongLength + 1, kind, work.Task.Id, work.Version, work.CurrentDecisionId, at ?? DateTimeOffset.UtcNow, detail)] };
    private static TaskRuntimeState LogAttempt(TaskRuntimeState runtime, string kind, ExecutionAttempt attempt, string detail) =>
        runtime with { Events = [.. runtime.Events, new(runtime.Events.LongLength + 1, kind, attempt.TaskId, attempt.TaskVersion, attempt.DecisionId, DateTimeOffset.UtcNow, detail)] };

    private static void ValidateRuntime(LearningState state)
    {
        if (state.Runtime is not { } runtime) return;
        CheckLimits(runtime.Limits);
        if (runtime.Turn < 0 || runtime.Tasks is null || runtime.Attempts is null || runtime.Events is null)
            throw new InvalidDataException("Invalid runtime collections.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var work in runtime.Tasks)
        {
            if (work?.Task is null || work.Version < 1 || work.ReadyTurn < 0 || work.ReadyTurn > runtime.Turn ||
                !Enum.IsDefined(work.Status) || !ids.Add(work.Task.Id)) throw new InvalidDataException("Invalid task state.");
            CheckPriority(work.Priority);
            TaskInput(state, work.Task);
            if (work.CurrentDecisionId is not null && !state.Episodes.Any(e => e.Trace.Input.Id == work.CurrentDecisionId))
                throw new InvalidDataException("Task refers to a missing decision.");
            if ((work.Status is WorkStatus.Ready or WorkStatus.Cancelled) != (work.CurrentDecisionId is null))
                throw new InvalidDataException("Task status and current decision disagree.");
        }
        ids.Clear();
        foreach (var attempt in runtime.Attempts)
        {
            if (attempt is null || !ids.Add(attempt.DecisionId) || attempt.Cost < 0 || !Enum.IsDefined(attempt.Status))
                throw new InvalidDataException("Invalid execution attempt.");
            var work = runtime.Tasks.SingleOrDefault(w => w.Task.Id == attempt.TaskId);
            var episode = state.Episodes.SingleOrDefault(e => e.Trace.Input.Id == attempt.DecisionId);
            if (work is null || attempt.TaskVersion < 1 || attempt.TaskVersion > work.Version || episode is null ||
                episode.Trace.Decision.Selected.Proposal.Kind != ChoiceKind.Act || episode.Trace.Decision.Selected.Proposal.Cost != attempt.Cost ||
                ((attempt.Status is AttemptStatus.Recorded or AttemptStatus.Abandoned) != (episode.Result is not null)) ||
                (attempt.Status == AttemptStatus.Planned && (work.Version != attempt.TaskVersion || work.CurrentDecisionId != attempt.DecisionId)))
                throw new InvalidDataException("Execution attempt linkage or lifecycle is invalid.");
            if (attempt.Status == AttemptStatus.Abandoned && episode.Result!.Status != ExecutionStatus.NotExecuted)
                throw new InvalidDataException("Abandoned plan must record non-execution.");
        }
        foreach (var work in runtime.Tasks.Where(w => w.CurrentDecisionId is not null))
        {
            var episode = state.Episodes.Single(e => e.Trace.Input.Id == work.CurrentDecisionId);
            var attempt = runtime.Attempts.SingleOrDefault(a => a.DecisionId == work.CurrentDecisionId);
            WorkStatus expected;
            if (episode.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Act)
            {
                if (attempt is null || attempt.TaskId != work.Task.Id || attempt.TaskVersion != work.Version)
                    throw new InvalidDataException("Current action belongs to a different task version.");
                expected = attempt.Status switch
                {
                    AttemptStatus.Planned => WorkStatus.WaitingExecution,
                    AttemptStatus.Dispatched => WorkStatus.Executing,
                    AttemptStatus.Uncertain => WorkStatus.NeedsReconciliation,
                    AttemptStatus.Recorded => (episode.Feedback.LastOrDefault()?.Succeeded ??
                        (episode.Result!.Status == ExecutionStatus.Completed && GoalsMet(episode.Trace.Input.Cognition!.Goals, episode.Result.ActualOutcomes)))
                        ? WorkStatus.Completed : WorkStatus.WaitingFeedback,
                    _ => throw new InvalidDataException("Current task references an abandoned action.")
                };
            }
            else
            {
                if (episode.Result?.Status != ExecutionStatus.NotExecuted) throw new InvalidDataException("Non-action task has an invalid receipt.");
                expected = episode.Trace.Decision.Selected.Proposal.Kind switch
                {
                    ChoiceKind.Refuse => WorkStatus.Refused, ChoiceKind.Defer => WorkStatus.Deferred,
                    _ => WorkStatus.WaitingFeedback
                };
            }
            if (work.Status != expected) throw new InvalidDataException("Task lifecycle does not match its current decision and execution.");
        }
        if (Used(runtime) > runtime.Limits.TotalActionBudget ||
            runtime.Attempts.Count(a => a.Status is AttemptStatus.Dispatched or AttemptStatus.Uncertain) > runtime.Limits.MaxConcurrentExecutions)
            throw new InvalidDataException("Runtime resources exceed configured limits.");
        for (int i = 0; i < runtime.Events.Length; i++)
            if (runtime.Events[i] is not { } e || e.Sequence != i + 1 || !Text(e.Kind) || !Text(e.Detail) ||
                !(e.Kind == "budget" && e.TaskId == "" && e.TaskVersion == 0) &&
                !runtime.Tasks.Any(w => w.Task.Id == e.TaskId && e.TaskVersion > 0 && e.TaskVersion <= w.Version))
                throw new InvalidDataException("Invalid runtime audit sequence.");
        if (runtime.Turn != runtime.Events.LongCount(e => e.Kind == "thought"))
            throw new InvalidDataException("Cognitive turn counter differs from audit history.");
    }
}
