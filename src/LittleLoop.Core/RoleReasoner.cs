namespace LittleLoop.Core;

public sealed record ActionMeaning(string Strategy, string[] Outcomes, string[] Traits, string[] Premises);
public sealed record RoleGoal(string Id, string Outcome, int Priority, bool Required);
public sealed record RolePreference(string Id, string Trait, int Priority, bool Prefer);
public sealed record RoleBoundary(string Id, string Outcome, string Reason);
public sealed record Premise(string Id, string Claim, bool Expected, bool EssentialToGoal);
public sealed record Evidence(string Id, string Claim, bool Value, decimal Confidence, string Source);
public sealed record Experience(string Id, string Source, Dictionary<string, string> Context,
    Proposal Action, bool Succeeded, string Lesson);
public sealed record RoleDeliberation(RoleGoal[] Goals, RolePreference[] Preferences,
    RoleBoundary[] Boundaries, Premise[] Premises, Evidence[] Evidence, Experience[] Experiences);
public sealed record ReasoningStep(string Stage, string Conclusion, string[] References);

// Structured, defeasible deliberation: premises and role commitments precede comparison.
// This is not a natural-language understanding model. All semantic links are explicit.
public static class RoleReasoner
{
    public const string Version = "deliberation/2";
    private static void Require(bool condition, string message)
    { if (!condition) throw new ArgumentException(message); }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Labels(string[]? values) => values is not null && values.All(Text) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Ids(IEnumerable<string> ids) => Labels(ids.ToArray());

    internal static void ValidateMeaning(ActionMeaning meaning) => Require(Text(meaning.Strategy) &&
        Labels(meaning.Outcomes) && Labels(meaning.Traits) && Labels(meaning.Premises), "Invalid action meaning.");

    internal static void Validate(DeliberationInput input)
    {
        var c = input.Cognition!;
        Require(c.Goals is not null && c.Preferences is not null && c.Boundaries is not null &&
            c.Premises is not null && c.Evidence is not null && c.Experiences is not null,
            "Cognition collections are required.");
        Require(c.Goals!.Length is > 0 and <= 64 && c.Goals.All(g => g is not null && Text(g.Id) && Text(g.Outcome) && g.Priority >= 0) && Ids(c.Goals.Select(g => g.Id)), "Invalid goals.");
        Require(c.Preferences!.Length <= 64 && c.Preferences.All(p => p is not null && Text(p.Id) && Text(p.Trait) && p.Priority >= 0) && Ids(c.Preferences.Select(p => p.Id)), "Invalid preferences.");
        Require(c.Boundaries!.Length <= 64 && c.Boundaries.All(b => b is not null && Text(b.Id) && Text(b.Outcome) && Text(b.Reason)) && Ids(c.Boundaries.Select(b => b.Id)), "Invalid boundaries.");
        Require(c.Premises!.Length <= 64 && c.Premises.All(p => p is not null && Text(p.Id) && Text(p.Claim)) && Ids(c.Premises.Select(p => p.Id)), "Invalid premises.");
        Require(c.Evidence!.Length <= 256 && c.Evidence.All(e => e is not null && Text(e.Id) && Text(e.Claim) && Text(e.Source) && e.Confidence is >= 0 and <= 1) && Ids(c.Evidence.Select(e => e.Id)), "Invalid evidence.");
        Require(c.Experiences!.Length <= 64 && c.Experiences.All(e => e is not null && Text(e.Id) && Text(e.Source) && Text(e.Lesson) && e.Context is { Count: > 0 } && e.Context.All(kv => Text(kv.Key) && Text(kv.Value)) && e.Action?.Meaning is not null) && Ids(c.Experiences.Select(e => e.Id)), "Invalid experiences.");
        foreach (var e in c.Experiences) DeliberationPolicy.ValidateProposals([e.Action]);
        foreach (var action in input.Options.Concat(c.Experiences.Select(e => e.Action)))
            Require(action.Meaning is null || action.Meaning.Premises.All(id => c.Premises.Any(p => p.Id == id)), "Unknown premise reference.");
    }

    internal static DeliberationDecision Decide(DeliberationInput input, List<AssessedProposal> initial)
    {
        var c = input.Cognition!;
        var steps = new List<ReasoningStep>();
        var candidates = new List<AssessedProposal>();
        var memories = c.Experiences.Where(e => e.Context.All(kv => input.Facts.GetValueOrDefault(kv.Key) == kv.Value))
            .OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var goals = c.Goals.OrderByDescending(g => g.Priority).ThenBy(g => g.Id, StringComparer.Ordinal).ToArray();
        var preferences = c.Preferences.OrderByDescending(p => p.Priority).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        steps.Add(new("goals", $"Consider role {input.RoleVersion}: {string.Join(", ", goals.Select(g => g.Outcome))}.", goals.Select(g => $"goal:{g.Id}").ToArray()));
        foreach (var memory in memories)
            steps.Add(new("recall", $"{(memory.Succeeded ? "Successful" : "Failed")} experience from {memory.Source}: {memory.Lesson}", [$"experience:{memory.Id}"]));

        // Recall a successful strategy as a new candidate for this goal, not an imposed choice.
        foreach (var memory in memories.Where(e => e.Succeeded && e.Action.Kind == ChoiceKind.Act &&
                     goals.Any(g => e.Action.Meaning!.Outcomes.Contains(g.Outcome))))
        {
            var proposal = memory.Action with { Id = $"recall:{memory.Id}",
                Reason = $"Proposed for '{input.Goal}' from experience {memory.Id}: {memory.Lesson}" };
            initial.Add(new("core", proposal, true, "Recalled strategy; current constraints still apply."));
            steps.Add(new("propose", proposal.Reason, [$"experience:{memory.Id}", $"candidate:core/{proposal.Id}"]));
        }

        var premiseProblems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var premise in c.Premises)
        {
            var evidence = c.Evidence.Where(e => e.Claim == premise.Claim && e.Confidence >= .75m).ToArray();
            bool support = evidence.Any(e => e.Value == premise.Expected);
            bool oppose = evidence.Any(e => e.Value != premise.Expected);
            string state = oppose && support ? "Conflicting evidence" : oppose ? "Contradicted" : !support ? "Unsupported" : "Supported";
            if (!support || oppose) premiseProblems.Add(premise.Id, state);
            steps.Add(new("premise", $"{state}: {premise.Claim} (expected {premise.Expected}).",
                [$"premise:{premise.Id}", .. evidence.Select(e => $"evidence:{e.Id}")]));
        }

        foreach (var candidate in initial)
        {
            var p = candidate.Proposal;
            var meaning = p.Meaning;
            var objections = new List<string>();
            if (p.Risk > input.MaximumRisk) objections.Add("Role risk limit exceeded");
            if (p.Cost > input.Budget) objections.Add("Action budget exceeded");
            if (p.Kind == ChoiceKind.Act && !input.AllowedCapabilities.Contains(p.Capability, StringComparer.Ordinal)) objections.Add("Capability is not authorized");
            if (p.Kind == ChoiceKind.Act)
            {
                if (p.Tool is { } tool && tool.Bindings.Values.Any(key => input.ToolArguments?.ContainsKey(key) != true))
                    objections.Add("Missing task tool arguments: " + string.Join(", ", tool.Bindings.Values.Where(key => input.ToolArguments?.ContainsKey(key) != true)));
                if (meaning is null) objections.Add("No explicit outcome/premise model; cannot justify this action");
                else
                {
                    foreach (var id in meaning.Premises)
                        if (!c.Premises.Any(x => x.Id == id) || premiseProblems.ContainsKey(id)) objections.Add($"Unresolved premise:{id}");
                    foreach (var boundary in c.Boundaries.Where(b => meaning.Outcomes.Contains(b.Outcome)))
                        objections.Add($"Role boundary:{boundary.Id}: {boundary.Reason}");
                    foreach (var goal in goals.Where(g => g.Required && !meaning.Outcomes.Contains(g.Outcome)))
                        objections.Add($"Required goal:{goal.Id} is not addressed");
                    foreach (var memory in memories.Where(e => !e.Succeeded && e.Action.Meaning!.Strategy == meaning.Strategy))
                        objections.Add($"Unresolved counterexample experience:{memory.Id}: {memory.Lesson}");
                    if (!goals.Any(g => meaning.Outcomes.Contains(g.Outcome))) objections.Add("No link to a current goal");
                }
            }
            else if (candidate.Source != "core") objections.Add("Advisory suggestion; core must independently justify non-action");
            var assessment = objections.Count == 0 ? "Admissible under current premises, role commitments and constraints."
                : string.Join("; ", objections);
            candidates.Add(candidate with { Eligible = objections.Count == 0, Assessment = assessment });
            steps.Add(new("assess", assessment, [$"candidate:{candidate.Source}/{p.Id}"]));
        }

        var goalConflicts = goals.Where(g => g.Required).SelectMany(g => c.Boundaries
            .Where(b => b.Outcome == g.Outcome).Select(b => (Goal: g, Boundary: b))).ToArray();
        AssessedProposal selected;
        if (goalConflicts.Length > 0)
        {
            var reason = string.Join("; ", goalConflicts.Select(x => $"Required goal {x.Goal.Id} demands {x.Goal.Outcome}, conflicting with role boundary {x.Boundary.Id}: {x.Boundary.Reason}"));
            selected = Meta(ChoiceKind.Refuse, "refuse-goal", reason);
            steps.Add(new("refuse", reason, goalConflicts.SelectMany(x => new[] { $"goal:{x.Goal.Id}", $"boundary:{x.Boundary.Id}" }).ToArray()));
        }
        else if (c.Premises.Any(p => p.EssentialToGoal && premiseProblems.ContainsKey(p.Id)))
        {
            var problems = c.Premises.Where(p => p.EssentialToGoal && premiseProblems.ContainsKey(p.Id)).ToArray();
            selected = Meta(ChoiceKind.Reframe, "question-goal", "Reconsider the goal's assumptions: " +
                string.Join("; ", problems.Select(p => $"{p.Claim}: {premiseProblems[p.Id]}")));
            steps.Add(new("reframe", selected.Proposal.Reason, problems.Select(p => $"premise:{p.Id}").ToArray()));
        }
        else
        {
            var viable = candidates.Where(x => x.Eligible && x.Proposal.Kind == ChoiceKind.Act).ToList();
            viable.Sort(Compare);
            if (viable.Count > 0) selected = viable[0];
            else if (candidates.Any(x => x.Proposal.Tool is { } tool && tool.Bindings.Values.Any(key => input.ToolArguments?.ContainsKey(key) != true)))
                selected = Meta(ChoiceKind.Ask, "seek-tool-arguments", "Provide the current task's missing tool arguments before executing a recalled recipe.");
            else if (premiseProblems.Count > 0 && candidates.Any(x => x.Proposal.Meaning?.Premises.Any(premiseProblems.ContainsKey) == true))
                selected = Meta(ChoiceKind.Ask, "seek-evidence", "No justified action remains; seek evidence for unresolved action premises.");
            else selected = candidates.Single(x => x.Source == "core" && x.Proposal.Id == "defer");
            steps.Add(new("choose", selected.Proposal.Kind == ChoiceKind.Act
                ? "Choose among justified actions by ordered goals, then role preferences, relevant successful experience, lower risk and cost. Plugin value scores are unused."
                : selected.Proposal.Reason,
                [$"candidate:{selected.Source}/{selected.Proposal.Id}", .. goals.Select(g => $"goal:{g.Id}"), .. preferences.Select(p => $"preference:{p.Id}")]));
            foreach (var other in viable.Skip(1))
                steps.Add(new("tradeoff", $"Chose {selected.Proposal.Id} over {other.Proposal.Id}: {Difference(selected, other)}.",
                    [$"candidate:{selected.Source}/{selected.Proposal.Id}", $"candidate:{other.Source}/{other.Proposal.Id}"]));
        }
        return new(Version, input.Id, input.RoleVersion, [.. candidates], selected) { Reasoning = [.. steps] };

        AssessedProposal Meta(ChoiceKind kind, string id, string reason)
        {
            var result = new AssessedProposal("core", new(id, kind, reason, 0, 0, reason), true, "Core conclusion derived from recorded evidence and role context.");
            candidates.Add(result);
            return result;
        }
        int Compare(AssessedProposal a, AssessedProposal b)
        {
            foreach (var goal in goals)
            {
                int d = b.Proposal.Meaning!.Outcomes.Contains(goal.Outcome).CompareTo(a.Proposal.Meaning!.Outcomes.Contains(goal.Outcome));
                if (d != 0) return d;
            }
            foreach (var preference in preferences)
            {
                int d = Fits(b, preference).CompareTo(Fits(a, preference));
                if (d != 0) return d;
            }
            int memory = Remembered(b).CompareTo(Remembered(a));
            if (memory != 0) return memory;
            int risk = a.Proposal.Risk.CompareTo(b.Proposal.Risk);
            if (risk != 0) return risk;
            int cost = a.Proposal.Cost.CompareTo(b.Proposal.Cost);
            if (cost != 0) return cost;
            int source = StringComparer.Ordinal.Compare(a.Source, b.Source);
            return source != 0 ? source : StringComparer.Ordinal.Compare(a.Proposal.Id, b.Proposal.Id);
        }
        bool Fits(AssessedProposal p, RolePreference preference) => p.Proposal.Meaning!.Traits.Contains(preference.Trait) == preference.Prefer;
        bool Remembered(AssessedProposal p) => memories.Any(e => e.Succeeded && e.Action.Meaning!.Strategy == p.Proposal.Meaning!.Strategy);
        string Difference(AssessedProposal a, AssessedProposal b)
        {
            foreach (var g in goals)
                if (a.Proposal.Meaning!.Outcomes.Contains(g.Outcome) != b.Proposal.Meaning!.Outcomes.Contains(g.Outcome)) return $"goal:{g.Id} ({g.Outcome}) takes precedence";
            foreach (var p in preferences)
                if (Fits(a, p) != Fits(b, p)) return $"preference:{p.Id} ({p.Trait}, prefer={p.Prefer}) takes precedence";
            if (Remembered(a) != Remembered(b)) return "relevant successful experience supports the strategy";
            return "lower risk, then cost, then stable identity breaks an otherwise unresolved tie";
        }
    }
}
