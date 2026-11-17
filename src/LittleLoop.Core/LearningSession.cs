using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleLoop.Core;

public sealed record PersistentRole(string Id, decimal MaximumRisk, RolePreference[] Preferences, RoleBoundary[] Boundaries);
public sealed record LearningTask(string Id, string Goal, Dictionary<string, string> Facts, RoleGoal[] Goals,
    Proposal[] Options, Premise[] Premises, Evidence[] Evidence, int Budget, string[] AllowedCapabilities)
{
    public Dictionary<string, string>? ToolArguments { get; init; }
}
public enum ExecutionStatus { Completed, Failed, Blocked, NotExecuted }
public enum ToolFailure { MissingInput, TargetExists, InvalidPath, InvalidEncoding, LimitExceeded, PermissionDenied, InvalidArguments, StaleTask, IoFailure, StaleContent, CommandFailed }
public sealed record ActionResult(string Id, string DecisionId, string ProposalId, string ProposalSource,
    ExecutionStatus Status, string[] ActualOutcomes, string Source, string Detail, bool ExternalEffect)
{
    public ToolReceipt? ToolReceipt { get; init; }
    public ToolFailure? Failure { get; init; }
    public CommandObservation? Command { get; init; }
}
public sealed record CommandObservation(string Id, int ExitCode, string StandardOutput, string StandardError,
    string BeforeSnapshot, string AfterSnapshot, bool IsTest);
public sealed record ToolArtifact(string Path, string Usage, long Bytes, string Sha256);
public sealed record ToolReceipt(string ToolId, string Version, string WorkspaceRoot, ToolArtifact[] Artifacts, string? OutputText);
public sealed record ResultFeedback(string Id, string DecisionId, string? Supersedes, bool Succeeded,
    string[] ActualOutcomes, string Source, string Reason);
public sealed record LearningEpisode(DeliberationTrace Trace, ActionResult? Result, ResultFeedback[] Feedback);
public sealed record LearningState(int Format, long Revision, PersistentRole Role, LearningEpisode[] Episodes)
{
    public TaskRuntimeState? Runtime { get; init; }
    public GoalRun[] Goals { get; init; } = [];
    public GoalDialogue[] Dialogues { get; init; } = [];
    public DevelopmentRun[] DevelopmentRuns { get; init; } = [];
}

// The executor returns observations, never inferred success from a proposal's claimed outcomes.
public interface IActionExecutor
{
    ActionResult Execute(DeliberationTrace trace);
}

/// <summary>Local single-writer store. Every transition atomically replaces the state file.</summary>
public sealed partial class LearningSession(string path)
{
    private static readonly Dictionary<string, LocalGate> LocalGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private sealed class LocalGate { public int Users; }
    private readonly string path = Path.GetFullPath(path);
    public string StatePath => path;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    public LearningState Initialize(PersistentRole? role = null)
    {
        using var guard = Lock();
        if (File.Exists(path)) throw new IOException("Learning state already exists; refusing to overwrite history.");
        role ??= new("default", .5m, [], []);
        ValidateRole(role);
        var state = new LearningState(1, 0, Clone(role), []);
        Save(state);
        return state;
    }

    public LearningState Read()
    {
        using var guard = Lock();
        return Load();
    }

    public LearningEpisode Begin(LearningTask task, PluginHost? host = null)
    {
        using var guard = Lock();
        var state = Load();
        if (state.Runtime is not null) throw new ArgumentException("This role uses task scheduling; enqueue work instead of bypassing its shared budget.");
        var episode = PrepareEpisode(state, task, host);
        Save(state with { Revision = checked(state.Revision + 1), Episodes = [.. state.Episodes, episode] });
        return Clone(episode);
    }

    private static DeliberationInput TaskInput(LearningState state, LearningTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Facts is null || task.Facts.Count == 0 || task.Facts.Any(kv => !Text(kv.Key) || !Text(kv.Value)) ||
            task.Premises is null) throw new ArgumentException("Learning requires nonempty structured context and premises.");
        var input = new DeliberationInput(task.Id, task.Goal, $"{state.Role.Id}/memory-{state.Revision}",
            state.Role.MaximumRisk, task.Budget, task.AllowedCapabilities, task.Options, task.Facts)
        {
            Cognition = new(task.Goals, state.Role.Preferences, state.Role.Boundaries, task.Premises, task.Evidence, []),
            ToolArguments = task.ToolArguments
        };
        DeliberationPolicy.Validate(input);
        return input;
    }

    private static LearningEpisode PrepareEpisode(LearningState state, LearningTask task, PluginHost? host)
    {
        if (state.Episodes.Any(e => e.Trace.Input.Id == task.Id))
            throw new ArgumentException("Decision ID already exists. Inspect its recorded result; do not execute it again.");
        var input = TaskInput(state, task);
        input = input with { Cognition = input.Cognition! with { Experiences = Recall(state, task) } };
        return new LearningEpisode((host ?? new PluginHost()).Run(Clone(input)), null, []);
    }

    public LearningEpisode Complete(ActionResult result)
    {
        using var guard = Lock();
        var state = Load();
        ArgumentNullException.ThrowIfNull(result);
        int index = Find(state, result.DecisionId);
        var episode = state.Episodes[index];
        ValidateResult(episode.Trace, result);
        if (episode.Result is not null)
        {
            if (Equal(episode.Result, result)) return episode;
            throw new ArgumentException("A result is already recorded. Submit a linked feedback correction instead.");
        }
        if (state.Episodes.Any(e => e.Result?.Id == result.Id)) throw new ArgumentException("Duplicate result ID.");
        var updated = episode with { Result = Clone(result) };
        state.Episodes[index] = updated;
        state = ApplyRuntimeResult(state, updated);
        Save(state with { Revision = checked(state.Revision + 1) });
        return Clone(updated);
    }

    public LearningEpisode Feedback(ResultFeedback feedback)
    {
        using var guard = Lock();
        var state = Load();
        ArgumentNullException.ThrowIfNull(feedback);
        int index = Find(state, feedback.DecisionId);
        var episode = state.Episodes[index];
        var duplicate = state.Episodes.SelectMany(e => e.Feedback).FirstOrDefault(f => f.Id == feedback.Id);
        if (duplicate is not null)
        {
            if (Equal(duplicate, feedback)) return episode;
            throw new ArgumentException("Feedback ID was already used with different content.");
        }
        ValidateFeedback(episode, feedback);
        if (feedback.Supersedes != episode.Feedback.LastOrDefault()?.Id)
            throw new ArgumentException("Stale feedback: supersedes must name the latest feedback ID.");
        var updated = episode with { Feedback = [.. episode.Feedback, Clone(feedback)] };
        state.Episodes[index] = updated;
        state = ApplyRuntimeFeedback(state, updated);
        Save(state with { Revision = checked(state.Revision + 1) });
        return Clone(updated);
    }

    public LearningEpisode Run(LearningTask task, IActionExecutor executor, PluginHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var episode = Begin(task, host); // Persist intent before invoking the executor.
        // An exception leaves a visible pending episode. Never retry an unknown external outcome.
        var result = executor.Execute(Clone(episode.Trace));
        if (result is null || result.DecisionId != task.Id)
            throw new ArgumentException("Executor returned a result for a different decision.");
        return Complete(result);
    }

    private static Experience[] Recall(LearningState state, LearningTask task)
    {
        return state.Episodes.Reverse()
            .Where(e => e.Result?.Status is ExecutionStatus.Completed or ExecutionStatus.Failed &&
                e.Trace.Decision.Selected.Proposal.Kind == ChoiceKind.Act &&
                e.Trace.Input.Facts.All(kv => task.Facts.GetValueOrDefault(kv.Key) == kv.Value) &&
                e.Trace.Decision.Selected.Proposal.Meaning!.Premises.All(id =>
                    task.Premises.Any(p => p.Id == id && e.Trace.Input.Cognition!.Premises.Any(old => old == p))))
            .Select(e =>
            {
                var result = e.Result!;
                var feedback = e.Feedback.LastOrDefault();
                var outcomes = feedback?.ActualOutcomes ?? result.ActualOutcomes;
                var success = feedback?.Succeeded ?? (result.Status == ExecutionStatus.Completed &&
                    GoalsMet(e.Trace.Input.Cognition!.Goals, outcomes));
                var action = e.Trace.Decision.Selected.Proposal;
                var missing = action.Meaning!.Outcomes.Except(outcomes, StringComparer.Ordinal).ToArray();
                // Successful memories carry observed effects, not all effects once predicted.
                if (success) action = action with { Meaning = action.Meaning! with { Outcomes = [.. outcomes] } };
                string source = feedback is null ? $"result:{result.Id} ({result.Source})" : $"feedback:{feedback.Id} ({feedback.Source}), result:{result.Id}";
                string lesson = $"{(success ? "Succeeded" : "Failed")} in recorded context. Observed outcomes: [{string.Join(", ", outcomes)}]. " +
                    $"{(feedback?.Reason ?? result.Detail)}" + (missing.Length == 0 ? "" : $" Unmet predictions: {string.Join(", ", missing)}.");
                // Failure is relative to the old task's goals, not a universal property of a strategy.
                bool sameGoals = e.Trace.Input.Cognition!.Goals.Select(g => (g.Outcome, g.Required)).ToHashSet()
                    .SetEquals(task.Goals.Select(g => (g.Outcome, g.Required)));
                // Operational failures can depend on this particular file/content. Explicit feedback
                // may judge the strategy for the semantic context, rather than just those arguments.
                bool parameterScope = action.Tool is null || feedback is not null ||
                    Equal(e.Trace.Input.ToolArguments?.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray(),
                          task.ToolArguments?.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray());
                return success || (sameGoals && parameterScope)
                    ? new Experience($"episode:{e.Trace.Input.Id}", source, new(e.Trace.Input.Facts), action, success, lesson)
                    : null;
            }).Where(e => e is not null).Take(64).Select(e => e!).ToArray();
    }

    private static bool GoalsMet(RoleGoal[] goals, string[] outcomes) =>
        goals.Where(g => g.Required).All(g => outcomes.Contains(g.Outcome, StringComparer.Ordinal)) &&
        goals.Any(g => outcomes.Contains(g.Outcome, StringComparer.Ordinal));

    private static void ValidateResult(DeliberationTrace trace, ActionResult result)
    {
        var selected = trace.Decision.Selected;
        if (!Text(result.Id) || !Text(result.Source) || !Text(result.Detail) || !Labels(result.ActualOutcomes) ||
            !Enum.IsDefined(result.Status) || result.DecisionId != trace.Input.Id ||
            result.ProposalId != selected.Proposal.Id || result.ProposalSource != selected.Source)
            throw new ArgumentException("Invalid result or decision/action linkage.");
        if (result.Status is ExecutionStatus.Blocked or ExecutionStatus.NotExecuted &&
            (result.ActualOutcomes.Length != 0 || result.ExternalEffect))
            throw new ArgumentException("Unexecuted action cannot claim outcomes or external effects.");
        if (selected.Proposal.Kind != ChoiceKind.Act && result.Status != ExecutionStatus.NotExecuted)
            throw new ArgumentException("Non-action decisions cannot produce execution experience.");
        if (result.ToolReceipt is { } tool && (!Text(tool.ToolId) || !Text(tool.Version) || !Text(tool.WorkspaceRoot) ||
            tool.Artifacts is null || tool.Artifacts.Any(a => a is null || !Text(a.Path) ||
                a.Usage is not ("input" or "output") || a.Bytes < 0 || a.Sha256 is null || a.Sha256.Length != 64 || !a.Sha256.All(Uri.IsHexDigit))))
            throw new ArgumentException("Invalid tool execution evidence.");
    }

    private static void ValidateFeedback(LearningEpisode episode, ResultFeedback feedback)
    {
        if (!Text(feedback.Id) || !Text(feedback.Source) || !Text(feedback.Reason) || !Labels(feedback.ActualOutcomes) ||
            episode.Result?.Status is not (ExecutionStatus.Completed or ExecutionStatus.Failed) ||
            episode.Trace.Decision.Selected.Proposal.Kind != ChoiceKind.Act ||
            (feedback.Succeeded && !GoalsMet(episode.Trace.Input.Cognition!.Goals, feedback.ActualOutcomes)))
            throw new ArgumentException("Feedback requires an executed action, provenance, and observed goal outcomes for success.");
    }

    private static void ValidateRole(PersistentRole role)
    {
        if (role is null || !Text(role.Id)) throw new ArgumentException("Invalid persistent role.");
        DeliberationPolicy.Validate(new("validate", "Validate role", role.Id, role.MaximumRisk, 0, [], [], [])
        { Cognition = new([new("validate", "validate", 0, true)], role.Preferences, role.Boundaries, [], [], []) });
    }

    private LearningState Load()
    {
        // Read an immutable old-or-new snapshot while writers atomically replace the path.
        var state = JsonSerializer.Deserialize<LearningState>(AtomicSnapshot.ReadText(path), Json)
            ?? throw new InvalidDataException("Empty learning state.");
        if (state.Format != 1 || state.Revision < 0 || state.Episodes is null)
            throw new InvalidDataException("Unsupported or invalid learning state.");
        ValidateRole(state.Role);
        var decisions = new HashSet<string>(StringComparer.Ordinal);
        var results = new HashSet<string>(StringComparer.Ordinal);
        var feedbackIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var episode in state.Episodes)
        {
            if (episode?.Trace?.Input is null || episode.Trace.Decision?.Selected is null || episode.Feedback is null ||
                !decisions.Add(episode.Trace.Input.Id)) throw new InvalidDataException("Invalid or duplicate episode.");
            DeliberationPolicy.Validate(episode.Trace.Input);
            if (episode.Trace.Input.Cognition is null) throw new InvalidDataException("Learning episode requires cognition.");
            if (!Equal(DeliberationPolicy.Decide(episode.Trace.Input, episode.Trace.Responses), episode.Trace.Decision))
                throw new InvalidDataException("Stored decision does not match its recorded snapshot.");
            if (episode.Result is not null)
            {
                ValidateResult(episode.Trace, episode.Result);
                if (!results.Add(episode.Result.Id)) throw new InvalidDataException("Duplicate result ID.");
            }
            string? previous = null;
            foreach (var feedback in episode.Feedback)
            {
                if (feedback is null || feedback.DecisionId != episode.Trace.Input.Id ||
                    feedback.Supersedes != previous || !feedbackIds.Add(feedback.Id))
                    throw new InvalidDataException("Invalid feedback chain.");
                ValidateFeedback(episode, feedback);
                previous = feedback.Id;
            }
        }
        ValidateRuntime(state);
        ValidateGoals(state);
        ValidateDialogues(state);
        ValidateDevelopment(state);
        return state;
    }

    private IDisposable Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LocalGate gate;
        lock (LocalGates)
        {
            if (!LocalGates.TryGetValue(path, out gate!)) LocalGates.Add(path, gate = new());
            // Count waiters before entering the per-path monitor so a gate cannot
            // be removed while another caller is about to use it.
            gate.Users++;
        }
        bool entered = false;
        try
        {
            Monitor.Enter(gate, ref entered);
            return new StateLease(new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), path, gate);
        }
        catch
        {
            if (entered) Monitor.Exit(gate);
            ReleaseGate(path, gate);
            throw;
        }
    }
    private static void ReleaseGate(string path, LocalGate gate)
    {
        lock (LocalGates)
            if (--gate.Users == 0) LocalGates.Remove(path);
    }
    private sealed class StateLease(FileStream stream, string path, LocalGate gate) : IDisposable
    {
        public void Dispose()
        {
            try { stream.Dispose(); }
            finally
            {
                Monitor.Exit(gate);
                ReleaseGate(path, gate);
            }
        }
    }
    private void Save(LearningState state)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, Json);
                stream.Flush(true);
            }
            AtomicSnapshot.Replace(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static int Find(LearningState state, string id)
    {
        int index = Array.FindIndex(state.Episodes, e => e.Trace.Input.Id == id);
        return index < 0 ? throw new ArgumentException("Unknown decision ID.") : index;
    }
    private static bool Text(string? s) => !string.IsNullOrWhiteSpace(s);
    private static bool Labels(string[]? values) => values is not null && values.All(Text) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static bool Equal<T>(T a, T b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json);
}

/// <summary>Deterministic local environment; hidden actual outcomes never enter decision input.</summary>
public sealed class OutcomeSimulation : IActionExecutor
{
    private readonly Dictionary<string, string[]> outcomesByStrategy;
    public OutcomeSimulation(Dictionary<string, string[]> outcomesByStrategy)
    {
        ArgumentNullException.ThrowIfNull(outcomesByStrategy);
        if (outcomesByStrategy.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null ||
            kv.Value.Any(string.IsNullOrWhiteSpace) || kv.Value.Distinct(StringComparer.Ordinal).Count() != kv.Value.Length))
            throw new ArgumentException("Simulation requires distinct, nonempty outcome labels.");
        this.outcomesByStrategy = outcomesByStrategy.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }
    public ActionResult Execute(DeliberationTrace trace)
    {
        var p = trace.Decision.Selected.Proposal;
        var input = trace.Input;
        var status = p.Kind != ChoiceKind.Act ? ExecutionStatus.NotExecuted
            : !trace.Decision.Selected.Eligible || p.Cost > input.Budget || p.Risk > input.MaximumRisk ||
                !input.AllowedCapabilities.Contains(p.Capability, StringComparer.Ordinal) ? ExecutionStatus.Blocked
            : outcomesByStrategy.ContainsKey(p.Meaning!.Strategy) ? ExecutionStatus.Completed : ExecutionStatus.Failed;
        var outcomes = status == ExecutionStatus.Completed ? outcomesByStrategy[p.Meaning!.Strategy] : [];
        return new($"execution:{input.Id}", input.Id, p.Id, trace.Decision.Selected.Source, status, outcomes,
            "outcome-simulator/1", status == ExecutionStatus.Completed ? "Observed simulated environment outcomes." : $"Simulation: {status}.", false);
    }
}
