namespace LittleLoop.Core;

public enum ActionKind { Execute, Ask, Defer }
public enum Outcome { Completed, Incorrect, Asked, Deferred, Blocked }

public sealed record Observation(string Source, DateTimeOffset ObservedAt, string? SuggestedRoute, decimal Confidence);
public sealed record RoleSnapshot(string Version, decimal ConfidenceThreshold);
public sealed record Constraints(bool AllowExecution, bool CanAsk, int ActionBudget);
public sealed record DecisionInput(string Id, string Goal, int MinutesRemaining, Observation Observation,
    RoleSnapshot Role, Constraints Constraints);
public sealed record Candidate(ActionKind Action, bool Eligible, string Basis);
public sealed record Decision(string Id, string PolicyVersion, string RoleVersion, Candidate[] Candidates,
    ActionKind Selected, string Basis, decimal? EstimatedErrorRisk);
public sealed record Receipt(string DecisionId, ActionKind Action, Outcome Outcome, string Detail, bool ExternalEffect);
public sealed record DecisionTrace(DecisionInput Input, Decision Decision, Receipt Receipt);

public static class TicketPolicy
{
    public const string Version = "ticket-rules/1";

    public static Decision Decide(DecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.Goal) ||
            input.Observation is null || input.Role is null || input.Constraints is null ||
            string.IsNullOrWhiteSpace(input.Observation.Source) || string.IsNullOrWhiteSpace(input.Role.Version) ||
            input.Observation.Confidence is < 0 or > 1 || input.Role.ConfidenceThreshold is < 0 or > 1 ||
            input.MinutesRemaining < 0 || input.Constraints.ActionBudget < 0)
            throw new ArgumentException("Invalid decision input.");

        var observation = input.Observation;
        var constraints = input.Constraints;
        bool hasRoute = !string.IsNullOrWhiteSpace(observation.SuggestedRoute);
        bool authorized = constraints.AllowExecution && constraints.ActionBudget > 0;
        bool confident = observation.Confidence >= input.Role.ConfidenceThreshold;
        bool urgent = input.MinutesRemaining <= 5;
        bool execute = authorized && hasRoute && (confident || urgent);
        bool ask = constraints.CanAsk && constraints.ActionBudget > 0 && input.MinutesRemaining > 0;
        string executeBasis = !authorized ? "Execution is outside permissions or budget."
            : !hasRoute ? "No route was observed."
            : confident ? "Observed confidence meets the role threshold."
            : urgent ? "Deadline rule accepts the available route despite low confidence."
            : "Observed confidence is below the role threshold.";
        Candidate[] candidates = [
            new(ActionKind.Execute, execute, executeBasis),
            new(ActionKind.Ask, ask, ask ? "There is time and budget to ask." : "Asking is unavailable."),
            new(ActionKind.Defer, true, "Wait for more information or resources.")
        ];
        var chosen = candidates.First(candidate => candidate.Eligible);
        return new(input.Id, Version, input.Role.Version, candidates, chosen.Action, chosen.Basis,
            chosen.Action == ActionKind.Execute ? 1 - observation.Confidence : null);
    }
}

public static class TicketSimulation
{
    public static Receipt Execute(DecisionInput input, Decision decision, string actualRoute)
    {
        if (string.IsNullOrWhiteSpace(actualRoute))
            throw new ArgumentException("Simulation requires an actual route.");

        var constraints = input.Constraints;
        return decision.Selected switch
        {
            ActionKind.Execute when !constraints.AllowExecution || constraints.ActionBudget == 0 =>
                new(decision.Id, decision.Selected, Outcome.Blocked, "Execution permission or budget denied.", false),
            ActionKind.Ask when !constraints.CanAsk || constraints.ActionBudget == 0 =>
                new(decision.Id, decision.Selected, Outcome.Blocked, "Asking permission or budget denied.", false),
            ActionKind.Execute when input.Observation.SuggestedRoute == actualRoute =>
                new(decision.Id, decision.Selected, Outcome.Completed, "Simulated route matches outcome.", false),
            ActionKind.Execute =>
                new(decision.Id, decision.Selected, Outcome.Incorrect,
                    $"Observed route '{input.Observation.SuggestedRoute}' differs from actual route '{actualRoute}'.", false),
            ActionKind.Ask => new(decision.Id, decision.Selected, Outcome.Asked, "Requested route clarification.", false),
            _ => new(decision.Id, decision.Selected, Outcome.Deferred, "No action dispatched.", false)
        };
    }
}
