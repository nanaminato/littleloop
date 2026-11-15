namespace LittleLoop.Core;

public sealed record DevelopmentStep(string TaskId, string Tool, Dictionary<string, string> Arguments);
public sealed record DevelopmentPermission(string Id, string Kind, string Resource, string Reason);
public sealed record DevelopmentGrantDecision(string RequestId, string Kind, string Resource, bool Approved, string Reason, string OldPolicyHash, string NewPolicyHash);
public sealed record DevelopmentRun(string Id, string Goal, string PolicyHash, int Budget, int MaxModelCalls, int MaxFailures,
    string[] RequiredTests, GoalStatus Status, DevelopmentStep[] Steps, string? PendingTaskId,
    Dictionary<string, string> ReadHashes, Dictionary<string, string> PassedTests, PlanningReview[] Reviews,
    string[] Answers, int Failures, bool Changed, string? VerifiedSnapshot, string Detail)
{
    public Dictionary<string, string> ProtectedHashes { get; init; } = [];
    public DevelopmentPermission? PendingPermission { get; init; }
    public DevelopmentGrantDecision[] GrantDecisions { get; init; } = [];
}

public sealed partial class LearningSession
{
    public DevelopmentRun BeginDevelopment(DevelopmentRun run)
    {
        using var guard = Lock();
        var state = Load(); RequireRuntime(state);
        if (state.DevelopmentRuns.Any(r => r.Id == run.Id)) throw new ArgumentException("Development goal already exists.");
        state = state with { DevelopmentRuns = [.. state.DevelopmentRuns, Clone(run)] };
        ValidateDevelopment(state); Commit(state); return Clone(run);
    }
    public DevelopmentRun SaveDevelopment(DevelopmentRun update, long expectedRevision, LearningTask? task = null)
    {
        using var guard = Lock(); var state = Load();
        if (state.Revision != expectedRevision) throw new ArgumentException("Development state changed during planning; reload before retrying.");
        int index = Array.FindIndex(state.DevelopmentRuns, r => r.Id == update.Id);
        if (index < 0) throw new ArgumentException("Unknown development goal.");
        var old = state.DevelopmentRuns[index];
        if (old.Goal != update.Goal || old.PolicyHash != update.PolicyHash || old.Budget != update.Budget || old.MaxModelCalls != update.MaxModelCalls ||
            old.MaxFailures != update.MaxFailures || !Equal(old.RequiredTests, update.RequiredTests) || !Equal(old.ProtectedHashes, update.ProtectedHashes) || !Equal(old.GrantDecisions, update.GrantDecisions) ||
            update.Reviews.Length < old.Reviews.Length || !Equal(old.Reviews, update.Reviews.Take(old.Reviews.Length).ToArray()) ||
            update.Steps.Length < old.Steps.Length || !Equal(old.Steps, update.Steps.Take(old.Steps.Length).ToArray()))
            throw new ArgumentException("Development constraints and audit history cannot be rewritten.");
        if (task is not null)
        {
            var runtime = RequireRuntime(state);
            if (update.PendingTaskId != task.Id || update.Steps.LastOrDefault()?.TaskId != task.Id || update.Status != GoalStatus.Executing ||
                task.Budget > old.Budget - DevelopmentCost(state, old) || runtime.Tasks.Any(t => t.Task.Id == task.Id))
                throw new ArgumentException("Invalid development child task.");
            TaskInput(state, task);
            var work = new WorkItem(Clone(task), 1, 0, null, runtime.Turn, WorkStatus.Ready, null);
            state = state with { Runtime = Log(runtime with { Tasks = [.. runtime.Tasks, work] }, "enqueued", work, "Development action approved by host.") };
        }
        state.DevelopmentRuns[index] = Clone(update);
        ValidateDevelopment(state); Commit(state); return Clone(update);
    }
    public DevelopmentRun AnswerDevelopment(string id, string text, long revision)
    {
        var state = Read(); var run = state.DevelopmentRuns.SingleOrDefault(r => r.Id == id) ?? throw new ArgumentException("Unknown development goal.");
        if (state.Revision != revision || run.Status != GoalStatus.AwaitingClarification || run.PendingPermission is not null || !Text(text) || text.Length > 4096)
            throw new ArgumentException("Invalid development clarification.");
        return SaveDevelopment(run with { Status = GoalStatus.Ready, Answers = [.. run.Answers, text], Detail = "Clarification received; original permissions remain binding." }, revision);
    }
    // Trusted host-only transition. Model proposals and ordinary answers cannot call this path.
    public DevelopmentRun ResolveDevelopmentPermission(string id, string requestId, bool approved, string newPolicyHash, long revision)
    {
        using var guard = Lock(); var state = Load();
        var run = state.DevelopmentRuns.Single(r => r.Id == id);
        var request = run.PendingPermission;
        if (state.Revision != revision || run.Status != GoalStatus.AwaitingClarification || request?.Id != requestId || !Text(newPolicyHash))
            throw new ArgumentException("Stale or missing permission request.");
        string hash = approved ? newPolicyHash : run.PolicyHash;
        var update = run with { PolicyHash = hash, PendingPermission = null, Status = approved ? GoalStatus.Ready : GoalStatus.Refused,
            PassedTests = [], VerifiedSnapshot = null,
            GrantDecisions = [.. run.GrantDecisions, new(request.Id, request.Kind, request.Resource, approved, request.Reason, run.PolicyHash, hash)],
            Detail = approved ? "Host explicitly granted requested permission; core judgment still applies." : "Host denied requested permission." };
        state.DevelopmentRuns[Array.FindIndex(state.DevelopmentRuns, r => r.Id == id)] = update;
        ValidateDevelopment(state); Commit(state); return Clone(update);
    }
    public static int DevelopmentCost(LearningState state, DevelopmentRun run) => state.Runtime?.Attempts
        .Where(a => a.Status != AttemptStatus.Abandoned && run.Steps.Any(s => s.TaskId == a.TaskId)).Sum(a => a.Cost) ?? 0;
    public DevelopmentRun RetryDevelopmentPlanning(string id, long revision)
    {
        var state = Read(); var run = state.DevelopmentRuns.Single(r => r.Id == id);
        if (state.Revision != revision || run.Status != GoalStatus.Blocked || run.PendingTaskId is not null || run.PendingPermission is not null ||
            run.Reviews.LastOrDefault()?.Verdict is not ("rejected" or "invalid-schema") || run.Reviews.Length >= run.MaxModelCalls)
            throw new ArgumentException("Only a bounded rejected planning response can be retried; execution refusal or unknown effects cannot.");
        return SaveDevelopment(run with { Status = GoalStatus.Ready, Answers = [.. run.Answers, "Host requested planning retry; previous audit and permissions remain binding."],
            Detail = "Host requested bounded planning retry without resetting history or budget." }, revision);
    }
    private static void ValidateDevelopment(LearningState state)
    {
        if (state.DevelopmentRuns is null) throw new InvalidDataException("Missing development collection.");
        var ids = new HashSet<string>();
        foreach (var run in state.DevelopmentRuns)
        {
            if (run is null || !Text(run.Id) || !ids.Add(run.Id) || !Text(run.Goal) || !Text(run.PolicyHash) ||
                run.Budget is < 1 or > 1000 || run.MaxModelCalls is < 1 or > 64 || run.MaxFailures is < 0 or > 32 ||
                !Labels(run.RequiredTests) || run.RequiredTests.Length == 0 || !Enum.IsDefined(run.Status) || run.Steps is null ||
                run.ReadHashes is null || run.PassedTests is null || run.ProtectedHashes is null || run.GrantDecisions is null || run.Reviews is null || run.Reviews.Length > run.MaxModelCalls ||
                run.Answers is null || run.Answers.Any(a => !Text(a)) || run.Failures < 0 || !Text(run.Detail) || DevelopmentCost(state, run) > run.Budget)
                throw new InvalidDataException("Invalid development state.");
            if (run.PendingPermission is { } permission && (run.Status != GoalStatus.AwaitingClarification || run.PendingTaskId is not null ||
                !Text(permission.Id) || !Text(permission.Kind) || !Text(permission.Resource) || !Text(permission.Reason)))
                throw new InvalidDataException("Invalid development permission request.");
            foreach (var step in run.Steps)
                if (step is null || !Text(step.Tool) || step.Arguments is null || state.Runtime?.Tasks.Any(w => w.Task.Id == step.TaskId) != true)
                    throw new InvalidDataException("Missing development child task.");
            if (run.PendingTaskId is not null && (!run.Steps.Any(s => s.TaskId == run.PendingTaskId) || run.Status is not (GoalStatus.Executing or GoalStatus.NeedsReconciliation)))
                throw new InvalidDataException("Invalid pending development step.");
            if (run.Status == GoalStatus.Completed && (!run.Changed || !Text(run.VerifiedSnapshot) || run.PendingTaskId is not null ||
                !run.RequiredTests.All(t => run.PassedTests.GetValueOrDefault(t) == run.VerifiedSnapshot)))
                throw new InvalidDataException("Development completion lacks verified final code and required tests.");
        }
    }
}
