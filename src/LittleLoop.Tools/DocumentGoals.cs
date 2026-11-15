using LittleLoop.Core;

namespace LittleLoop.Tools;

public sealed record DocumentGoal(string Id, string Description, string Output, string[] Sources,
    string? Content = null, bool Normalize = true, int Budget = 12, int MaxReplans = 3, bool AllowAlternativeOutput = false);

/// <summary>Bounded symbolic planning over registered text operations, not natural-language inference.</summary>
public static class DocumentGoals
{
    public static GoalRun Add(LearningSession session, DocumentGoal request)
    {
        var (goal, facts, resources) = Compile(request);
        return session.AddGoal(goal, facts, resources);
    }

    public static (CompoundGoal Goal, string[] Facts, Dictionary<string, string> Resources) Compile(DocumentGoal request)
    {
        if (request.Sources is null || string.IsNullOrWhiteSpace(request.Output)) throw new ArgumentException("Output and source list required.");
        var resources = new Dictionary<string, string> { ["target"] = request.Output };
        var facts = new List<string> { "output.unwritten" };
        if (request.Content is not null) { resources["content"] = request.Content; facts.Add("content.available"); }
        if (request.Sources.Length > 0) { resources["source"] = request.Sources[0]; facts.Add("source.available"); }
        else if (request.Content is not null) { resources["source"] = "input-" + Guid.NewGuid().ToString("N") + ".txt"; facts.Add("source.unwritten"); }
        var goal = new CompoundGoal(request.Id, request.Description,
            ["output.created", request.Normalize ? "output.normalized" : "output.preserved", "output.verified"],
            resources, new() { ["source"] = request.Sources }, WorkspaceTools.List().Select(d => d.Id).ToArray(),
            request.Budget, request.MaxReplans, request.AllowAlternativeOutput);
        return (goal, facts.ToArray(), resources);
    }

    public static PlanOperator[] Operators() => [
        Op("read-source", "text.read", ["source.available"], ["source.readable"], [], new() { ["source"] = "source" }),
        Op("create-source", "text.create", ["content.available", "source.unwritten"], ["source.available", "source.readable"], ["source.unwritten"], new() { ["target"] = "source", ["content"] = "content" }),
        Op("normalize", "text.normalize-lines", ["source.readable", "output.unwritten"], ["output.created", "output.normalized"], ["output.unwritten"], new() { ["source"] = "source", ["target"] = "target" }),
        Op("copy", "text.copy", ["source.readable", "output.unwritten"], ["output.created", "output.preserved"], ["output.unwritten"], new() { ["source"] = "source", ["target"] = "target" }),
        Op("create-output", "text.create", ["content.available", "source.unwritten", "output.unwritten"], ["output.created", "output.preserved"], ["output.unwritten"], new() { ["target"] = "target", ["content"] = "content" }),
        Op("verify-output", "text.read", ["output.created"], ["output.verified"], [], new() { ["source"] = "target" })
    ];
    private static PlanOperator Op(string id, string tool, string[] requires, string[] produces, string[] removes, Dictionary<string, string> bindings) =>
        new(id, tool, requires, produces, removes, bindings, WorkspaceTools.List().Single(d => d.Id == tool).Outcomes);

    public static PlanOperator[]? Plan(GoalRun run, IEnumerable<string> enabled, int remaining)
    {
        var allowed = enabled.ToHashSet(StringComparer.Ordinal);
        var operators = Operators().Where(o => allowed.Contains(o.ToolId) && run.Goal.AllowedCapabilities.Contains(o.ToolId) &&
            !run.ExcludedOperators.Contains(o.Id) && o.Bindings.Values.All(run.Resources.ContainsKey)).ToArray();
        var queue = new Queue<(HashSet<string> Facts, PlanOperator[] Steps)>();
        queue.Enqueue((run.Facts.ToHashSet(), []));
        var seen = new HashSet<string>();
        while (queue.TryDequeue(out var node) && seen.Count < 4096)
        {
            if (!seen.Add(string.Join("|", node.Facts.Order(StringComparer.Ordinal)))) continue;
            if (run.Goal.RequiredFacts.All(node.Facts.Contains)) return node.Steps;
            if (node.Steps.Length >= Math.Min(16, remaining)) continue;
            foreach (var op in operators.Where(o => o.Requires.All(node.Facts.Contains)))
            {
                var next = node.Facts.Except(op.Removes).Concat(op.Produces).ToHashSet();
                queue.Enqueue((next, [.. node.Steps, op]));
            }
        }
        return null;
    }
}

public sealed class GoalOrchestrator(LearningSession session, WorkspaceToolPolicy policy, IGoalPlanningAdvisor? advisor = null)
{
    // Advances durable receipts and queues dependent steps, without executing tools.
    public void PrepareNextSteps()
    {
        if (advisor is not null) throw new InvalidOperationException("Use PrepareNextStepsAsync for a model-assisted planner.");
        PrepareNextStepsAsync().GetAwaiter().GetResult();
    }
    public async Task PrepareNextStepsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in session.Read().Goals.Where(g => g.Status is GoalStatus.Ready or GoalStatus.Executing or GoalStatus.NeedsReconciliation).Select(g => g.Goal.Id))
            await Advance(id, cancellationToken);
    }
    public async Task<LearningState> RunAsync(CancellationToken cancellationToken = default)
    {
        using var lease = new FileStream(session.StatePath + ".goals.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var executor = new WorkspaceToolExecutor(session, policy);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long before = session.Read().Revision;
            await PrepareNextStepsAsync(cancellationToken);
            await new MultiTaskRuntime(session, executor).RunAsync(cancellationToken: cancellationToken);
            var state = session.Read();
            if (state.Revision == before || state.Goals.All(g => g.Status is GoalStatus.Completed or GoalStatus.Blocked or GoalStatus.Refused or GoalStatus.AwaitingClarification)) return state;
        }
    }

    private async Task Advance(string id, CancellationToken cancellationToken)
    {
        var state = session.Read();
        var run = state.Goals.Single(g => g.Goal.Id == id);
        if (advisor is null && run.PlanningReviews.Length > 0)
            throw new ArgumentException("This goal requires a model planning advisor to resume; local planning cannot silently replace its review.");
        string? evidence = null;
        if (run.PendingTaskId is { } pending)
        {
            var work = state.Runtime!.Tasks.Single(w => w.Task.Id == pending);
            var step = run.Steps.Single(s => s.TaskId == pending);
            if (work.Version != 1) { Save(run with { Status = GoalStatus.Blocked, PendingTaskId = null, Detail = "Generated child was manually revised; plan cannot assume its result." }); return; }
            var episode = state.Episodes.SingleOrDefault(e => e.Trace.Input.Id == work.CurrentDecisionId);
            if (episode?.Result is not { } result)
            {
                if (work.Status == WorkStatus.NeedsReconciliation && run.Status != GoalStatus.NeedsReconciliation)
                    Save(run with { Status = GoalStatus.NeedsReconciliation, Detail = "Execution outcome unknown; reconcile before continuing. No automatic repeat." });
                else if (work.Status == WorkStatus.Cancelled) Save(run with { Status = GoalStatus.Blocked, PendingTaskId = null, Detail = "Child task cancelled." });
                return;
            }
            evidence = result.DecisionId;
            run = run with { PendingTaskId = null, Status = GoalStatus.Ready };
            if (result.Status == ExecutionStatus.Completed && result.ToolReceipt?.ToolId == step.Operator.ToolId &&
                step.Operator.Outcomes.All(result.ActualOutcomes.Contains) && episode.Feedback.LastOrDefault()?.Succeeded != false)
            {
                var receipt = result.ToolReceipt;
                var observation = receipt.Artifacts.SingleOrDefault(a => a.Usage == (step.Operator.Id is "read-source" or "verify-output" ? "input" : "output"));
                string expectedPath = step.Arguments[step.Operator.Id == "read-source" ? "source" : step.Operator.Id == "create-source" ? "source" : "target"];
                if (observation is null || observation.Path.Replace('\\', '/') != expectedPath.Replace('\\', '/') ||
                    (step.Operator.Id == "verify-output" && (!run.Observations.TryGetValue("output.hash", out var hash) || hash != observation.Sha256)))
                { Save(run with { Status = GoalStatus.Blocked, Detail = "Observed artifact differs from planned output; goal verification failed." }); return; }
                var observations = new Dictionary<string, string>(run.Observations);
                if (step.Operator.Produces.Contains("output.created")) observations["output.hash"] = observation.Sha256;
                run = run with { Facts = run.Facts.Except(step.Operator.Removes).Concat(step.Operator.Produces).Distinct().ToArray(), Observations = observations,
                    NextStep = advisor is null ? run.NextStep + 1 : -1, Detail = "Observed success: " + step.Operator.Id };
            }
            else
            {
                if (result.Status == ExecutionStatus.NotExecuted)
                { Save(run with { Status = GoalStatus.Refused, Detail = "Core declined planned action: " + result.Detail }); return; }
                if (result.ExternalEffect || run.Replans >= run.Goal.MaxReplans)
                { Save(run with { Status = GoalStatus.Blocked, Detail = "Automatic recovery stopped: possible side effect or replan limit. " + result.Detail }); return; }
                var resources = new Dictionary<string, string>(run.Resources);
                if (result.Failure is ToolFailure.MissingInput or ToolFailure.InvalidEncoding && step.Operator.Id == "read-source")
                {
                    var failed = run.FailedSources.Append(resources["source"]).Distinct().ToArray();
                    var alternative = run.Goal.Alternatives.GetValueOrDefault("source", []).FirstOrDefault(s => !failed.Contains(s));
                    var facts = run.Facts.Where(f => !f.StartsWith("source.", StringComparison.Ordinal)).ToList();
                    if (alternative is not null) { resources["source"] = alternative; facts.Add("source.available"); }
                    else if (resources.ContainsKey("content")) { resources["source"] = "input-" + Guid.NewGuid().ToString("N") + ".txt"; facts.Add("source.unwritten"); }
                    else { Save(run with { Status = GoalStatus.Blocked, Detail = "No usable source remains. " + result.Detail }); return; }
                    run = run with { FailedSources = failed, Facts = facts.ToArray() };
                }
                else if (result.Failure == ToolFailure.TargetExists && run.Goal.AllowAlternativeOutput && step.Operator.Produces.Contains("output.created"))
                {
                    var target = run.Goal.Resources["target"];
                    resources["target"] = Path.Combine(Path.GetDirectoryName(target) ?? "", Path.GetFileNameWithoutExtension(target) + "-alternative-" + (run.Replans + 1) + Path.GetExtension(target));
                }
                else { Save(run with { Status = GoalStatus.Blocked, Detail = "No authorized recovery: " + result.Detail }); return; }
                run = run with { Resources = resources, NextStep = -1, Replans = run.Replans + 1, Detail = "Replan from actual failure: " + result.Failure + "; " + result.Detail };
            }
        }
        if (run.Goal.RequiredFacts.All(run.Facts.Contains)) { Save(run with { Status = GoalStatus.Completed, Detail = "All required output properties observed and read-back hash verified." }); return; }
        int remaining = run.Goal.Budget - LearningSession.GoalCost(state, run);
        if (run.NextStep < 0)
        {
            PlanOperator[]? plan;
            if (advisor is null) plan = DocumentGoals.Plan(run, policy.AllowedTools, remaining);
            else
            {
                if (run.PlanningReviews.Length >= advisor.MaxCalls)
                { Save(run with { Status = GoalStatus.Blocked, Detail = "Model planning call limit reached; no automatic retry." }); return; }
                var reviewed = await advisor.AdviseAsync(state, run, policy, remaining, evidence, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                run = reviewed.Run with { PlanningReviews = [.. run.PlanningReviews, reviewed.Review], Detail = reviewed.Review.Reason };
                if (reviewed.StopStatus is { } stopped) { Save(run with { Status = stopped }); return; }
                plan = reviewed.Steps;
            }
            if (plan is null) { Save(run with { Status = GoalStatus.Blocked, Detail = "No tool composition satisfies remaining requirements within grants and budget." }); return; }
            run = run with { Plans = [.. run.Plans, new(run.Plans.Length + 1, run.Facts, new(run.Resources), run.ExcludedOperators, plan, run.Detail, evidence)], NextStep = 0 };
        }
        if (remaining <= 0 || run.NextStep >= run.Plans[^1].Steps.Length) { Save(run with { Status = GoalStatus.Blocked, Detail = "Goal budget or executable plan exhausted." }); return; }
        var op = run.Plans[^1].Steps[run.NextStep];
        var descriptor = WorkspaceTools.List().Single(d => d.Id == op.ToolId);
        string childId = $"goal:{id}:step:{run.Steps.Length + 1}";
        var proposal = new Proposal(op.Id, ChoiceKind.Act, op.Id, 0, descriptor.MinimumRisk, "Automatically composed from tool preconditions and goal requirements.", op.ToolId, descriptor.Cost)
        { Tool = new(op.ToolId, op.Bindings), Meaning = new("workspace/" + op.ToolId + "/1", descriptor.Outcomes, descriptor.Traits, []) };
        var task = new LearningTask(childId, run.Goal.Description + ": " + op.Id, new() { ["workflow"] = "document-goal", ["step"] = op.Id },
            op.Outcomes.Select((o, i) => new RoleGoal("outcome-" + i, o, 1, true)).ToArray(), [proposal], [], [], descriptor.Cost, [op.ToolId]) { ToolArguments = new(run.Resources) };
        run = run with { Status = GoalStatus.Executing, PendingTaskId = childId, Steps = [.. run.Steps, new(childId, run.Plans.Length, op, new(run.Resources))] };
        session.SaveGoal(run, state.Revision, task);
        void Save(GoalRun update) => session.SaveGoal(update, state.Revision);
    }
}
