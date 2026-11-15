namespace LittleLoop.Core;

public enum GoalStatus { Ready, Executing, Completed, Blocked, Refused, NeedsReconciliation, AwaitingClarification }
public sealed record CompoundGoal(string Id, string Description, string[] RequiredFacts,
    Dictionary<string, string> Resources, Dictionary<string, string[]> Alternatives,
    string[] AllowedCapabilities, int Budget, int MaxReplans, bool AllowAlternativeOutput = false);
public sealed record PlanOperator(string Id, string ToolId, string[] Requires, string[] Produces, string[] Removes,
    Dictionary<string, string> Bindings, string[] Outcomes);
public sealed record PlanRevision(int Number, string[] InitialFacts, Dictionary<string, string> Resources,
    string[] ExcludedOperators, PlanOperator[] Steps, string Reason, string? EvidenceDecisionId);
public sealed record GoalStep(string TaskId, int PlanRevision, PlanOperator Operator, Dictionary<string, string> Arguments);
public sealed record GoalRun(CompoundGoal Goal, GoalStatus Status, string[] Facts, Dictionary<string, string> Resources,
    Dictionary<string, string> Observations, string[] FailedSources, string[] ExcludedOperators,
    int Replans, int NextStep, PlanRevision[] Plans, GoalStep[] Steps, string? PendingTaskId, string Detail)
{
    public PlanningReview[] PlanningReviews { get; init; } = [];
    public PlanningAnswer[] PlanningAnswers { get; init; } = [];
}
public sealed record PlanningReview(string Version, string Provider, string Model, string Input,
    string? Response, string Verdict, string Reason, string? EvidenceDecisionId);
public sealed record PlanningAnswer(int Review, string Text);

public sealed partial class LearningSession
{
    public GoalRun AnswerGoal(string id, string answer, long expectedRevision)
    {
        using var guard = Lock();
        var state = Load();
        if (state.Revision != expectedRevision || string.IsNullOrWhiteSpace(answer) || answer.Length > 4096)
            throw new ArgumentException("Invalid or stale goal clarification.");
        int index = Array.FindIndex(state.Goals, g => g.Goal.Id == id);
        if (index < 0 || state.Goals[index].Status != GoalStatus.AwaitingClarification)
            throw new ArgumentException("Goal is not awaiting clarification.");
        var run = state.Goals[index];
        run = run with { Status = GoalStatus.Ready, NextStep = -1,
            PlanningAnswers = [.. run.PlanningAnswers, new(run.PlanningReviews.Length, answer)], Detail = "User supplied planning clarification; original constraints remain binding." };
        state.Goals[index] = run;
        ValidateGoals(state);
        Commit(state);
        return Clone(run);
    }
    public GoalRun AddGoal(CompoundGoal goal, string[] initialFacts, Dictionary<string, string> resources)
    {
        using var guard = Lock();
        var state = Load();
        RequireRuntime(state);
        ValidateGoal(goal);
        if (state.Goals.Any(g => g.Goal.Id == goal.Id)) throw new ArgumentException("Goal ID already exists.");
        var run = new GoalRun(Clone(goal), GoalStatus.Ready, [.. initialFacts], new(resources), [], [], [], 0, -1, [], [], null, "Goal accepted; no tool steps supplied by caller.");
        state = state with { Goals = [.. state.Goals, run] };
        ValidateGoals(state);
        Commit(state);
        return Clone(run);
    }

    // Goal progress and its next child task are committed together; a restart cannot enqueue a step twice.
    public GoalRun SaveGoal(GoalRun update, long expectedRevision, LearningTask? nextTask = null)
    {
        using var guard = Lock();
        var state = Load();
        if (state.Revision != expectedRevision) throw new ArgumentException("Goal state changed; reload before updating.");
        int index = Array.FindIndex(state.Goals, g => g.Goal.Id == update.Goal.Id);
        if (index < 0 || !Equal(state.Goals[index].Goal, update.Goal)) throw new ArgumentException("Original goal constraints cannot be silently changed.");
        var previousReviews = state.Goals[index].PlanningReviews;
        if (update.PlanningReviews is null || update.PlanningReviews.Length < previousReviews.Length ||
            !Equal(previousReviews, update.PlanningReviews.Take(previousReviews.Length).ToArray()) ||
            !Equal(state.Goals[index].PlanningAnswers, update.PlanningAnswers))
            throw new ArgumentException("Planning audit history is append-only; answers must use the clarification transition.");
        if (nextTask is not null)
        {
            var runtime = RequireRuntime(state);
            if (update.PendingTaskId != nextTask.Id || update.Status != GoalStatus.Executing ||
                update.Steps.LastOrDefault()?.TaskId != nextTask.Id || runtime.Tasks.Any(w => w.Task.Id == nextTask.Id) ||
                nextTask.Budget > update.Goal.Budget - GoalCost(state, state.Goals[index]) ||
                nextTask.AllowedCapabilities.Any(c => !update.Goal.AllowedCapabilities.Contains(c, StringComparer.Ordinal)))
                throw new ArgumentException("Invalid child task linkage or goal budget/capability escalation.");
            TaskInput(state, nextTask);
            var work = new WorkItem(Clone(nextTask), 1, 0, null, runtime.Turn, WorkStatus.Ready, null);
            runtime = runtime with { Tasks = [.. runtime.Tasks, work] };
            state = state with { Runtime = Log(runtime, "enqueued", work, $"Automatically planned step for goal {update.Goal.Id}.") };
        }
        state.Goals[index] = Clone(update);
        ValidateGoals(state);
        Commit(state);
        return Clone(update);
    }

    public static int GoalCost(LearningState state, GoalRun goal) => state.Runtime?.Attempts
        .Where(a => a.Status != AttemptStatus.Abandoned && goal.Steps.Any(s => s.TaskId == a.TaskId)).Sum(a => a.Cost) ?? 0;

    private static void ValidateGoal(CompoundGoal goal)
    {
        if (goal is null || !Text(goal.Id) || !Text(goal.Description) || !Labels(goal.RequiredFacts) || goal.RequiredFacts.Length == 0 ||
            goal.Resources is null || goal.Resources.Any(kv => !Text(kv.Key) || kv.Value is null) ||
            goal.Alternatives is null || goal.Alternatives.Any(kv => !Text(kv.Key) || !Labels(kv.Value)) ||
            !Labels(goal.AllowedCapabilities) || goal.Budget is < 1 or > 1000 || goal.MaxReplans is < 0 or > 32)
            throw new ArgumentException("Invalid compound goal.");
    }
    private static void ValidateGoals(LearningState state)
    {
        if (state.Goals is null) throw new InvalidDataException("Missing goal collection.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in state.Goals)
        {
            if (run is null) throw new InvalidDataException("Null goal progress.");
            ValidateGoal(run.Goal);
            if (run.PlanningReviews is null || run.PlanningAnswers is null || run.PlanningReviews.Length > 64 ||
                run.PlanningReviews.Any(r => r is null || !Text(r.Version) || !Text(r.Provider) || !Text(r.Model) ||
                    !Text(r.Input) || r.Input.Length > 131072 || r.Response?.Length > 131072 || !Text(r.Verdict) || !Text(r.Reason)) ||
                run.PlanningAnswers.Any(a => a is null || a.Review < 1 || a.Review > run.PlanningReviews.Length || !Text(a.Text) || a.Text.Length > 4096))
                throw new InvalidDataException("Invalid planning review history.");
            if (!ids.Add(run.Goal.Id) || !Enum.IsDefined(run.Status) || !Labels(run.Facts) || run.Resources is null ||
                run.Observations is null || !Labels(run.FailedSources) || !Labels(run.ExcludedOperators) ||
                run.Plans is null || run.Steps is null || run.Replans < 0 || run.Replans > run.Goal.MaxReplans ||
                run.NextStep < -1 || !Text(run.Detail) || GoalCost(state, run) > run.Goal.Budget)
                throw new InvalidDataException("Invalid goal progress.");
            for (int i = 0; i < run.Plans.Length; i++)
            {
                var plan = run.Plans[i];
                if (plan is null || plan.Number != i + 1 || !Labels(plan.InitialFacts) || plan.Resources is null ||
                    !Labels(plan.ExcludedOperators) || plan.Steps is null || plan.Steps.Length > 16 || !Text(plan.Reason))
                    throw new InvalidDataException("Invalid plan history.");
                var predicted = plan.InitialFacts.ToHashSet(StringComparer.Ordinal);
                foreach (var op in plan.Steps)
                {
                    if (op is null || !Text(op.Id) || !Text(op.ToolId) || !Labels(op.Requires) || !Labels(op.Produces) ||
                        !Labels(op.Removes) || !Labels(op.Outcomes) || op.Bindings is null ||
                        op.Bindings.Any(b => !Text(b.Key) || !Text(b.Value) || !plan.Resources.ContainsKey(b.Value)) ||
                        !op.Requires.All(predicted.Contains) || !run.Goal.AllowedCapabilities.Contains(op.ToolId))
                        throw new InvalidDataException("Invalid operator or unsatisfied plan dependency.");
                    predicted.ExceptWith(op.Removes);
                    predicted.UnionWith(op.Produces);
                }
                if (!run.Goal.RequiredFacts.All(predicted.Contains)) throw new InvalidDataException("Plan does not reach required goal facts.");
            }
            if (run.NextStep >= 0 && (run.Plans.Length == 0 || run.NextStep > run.Plans[^1].Steps.Length))
                throw new InvalidDataException("Goal plan cursor is invalid.");
            var childIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in run.Steps)
                if (step is null || !childIds.Add(step.TaskId) || step.PlanRevision < 1 || step.PlanRevision > run.Plans.Length ||
                    step.Operator is null || step.Arguments is null || state.Runtime?.Tasks.Any(t => t.Task.Id == step.TaskId) != true)
                    throw new InvalidDataException("Goal step refers to missing work or plan.");
            foreach (var group in run.Steps.GroupBy(s => s.PlanRevision))
                if (!Equal(group.Select(s => s.Operator).ToArray(), run.Plans[group.Key - 1].Steps.Take(group.Count()).ToArray()))
                    throw new InvalidDataException("Executed step history differs from its recorded plan.");
            if (run.PendingTaskId is not null && (!childIds.Contains(run.PendingTaskId) || run.Status is not (GoalStatus.Executing or GoalStatus.NeedsReconciliation)))
                throw new InvalidDataException("Invalid pending goal step.");
            if (run.Status == GoalStatus.Completed && (!run.Goal.RequiredFacts.All(run.Facts.Contains) || run.PendingTaskId is not null))
                throw new InvalidDataException("Completed goal lacks required observed facts.");
        }
    }
}
