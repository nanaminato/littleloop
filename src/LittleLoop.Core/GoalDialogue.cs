namespace LittleLoop.Core;

public sealed record GoalQuestion(string Field, string Reason, string Text);
public sealed record LanguageEvidence(string Field, string Value, int Turn, string Quote);
public sealed record GoalUnderstanding(string Interpreter, Dictionary<string, string[]> Fields,
    GoalQuestion[] Questions, LanguageEvidence[] Evidence)
{
    public SemanticExchange? ModelExchange { get; init; }
}
public sealed record SemanticExchange(string Provider, string RequestedModel, string? ReturnedModel,
    string PromptVersion, string RequestSha256, string? Response, string? Failure)
{
    public int GoalBudgetLimit { get; init; } = 12;
}
public sealed record GoalLanguageTurn(string Text, string? AnswerTo, GoalUnderstanding Understanding);
public sealed record GoalDialogue(string Id, GoalLanguageTurn[] Turns, string? GoalId);

// Interpreters propose meanings. They cannot dispatch tools or change host permissions.
public interface IGoalInterpreter
{
    GoalUnderstanding Interpret(GoalDialogue? previous, string text);
}
public interface IAsyncGoalInterpreter
{
    Task<GoalUnderstanding> InterpretAsync(GoalDialogue? previous, string text, CancellationToken cancellationToken = default);
}

public sealed partial class LearningSession
{
    public GoalDialogue SaveDialogue(GoalDialogue dialogue, long expectedRevision, CompoundGoal? goal = null,
        string[]? initialFacts = null, Dictionary<string, string>? resources = null)
    {
        using var guard = Lock();
        var state = Load();
        if (state.Revision != expectedRevision) throw new ArgumentException("Dialogue changed; reload before replying.");
        var previous = state.Dialogues.SingleOrDefault(d => d.Id == dialogue.Id);
        if (previous?.GoalId is not null) throw new ArgumentException("Goal already submitted; use a new conversation for a new goal.");
        if (dialogue.Turns.Length != (previous?.Turns.Length ?? 0) + 1 ||
            (previous is not null && !Equal(previous.Turns, dialogue.Turns[..^1])))
            throw new ArgumentException("Dialogue history is append-only.");
        if (goal is not null)
        {
            RequireRuntime(state);
            ValidateGoal(goal);
            if (dialogue.GoalId != goal.Id || dialogue.Turns[^1].Understanding.Questions.Length != 0 ||
                initialFacts is null || resources is null || state.Goals.Any(g => g.Goal.Id == goal.Id))
                throw new ArgumentException("Unresolved dialogue or invalid goal linkage.");
            var run = new GoalRun(Clone(goal), GoalStatus.Ready, [.. initialFacts], new(resources), [], [], [], 0, -1, [], [], null,
                "Goal extracted from user language; tool composition pending.");
            state = state with { Goals = [.. state.Goals, run] };
        }
        else if (dialogue.GoalId is not null) throw new ArgumentException("Dialogue submission must atomically create its goal.");
        state = state with { Dialogues = [.. state.Dialogues.Where(d => d.Id != dialogue.Id), Clone(dialogue)] };
        ValidateGoals(state);
        ValidateDialogues(state);
        Commit(state);
        return Clone(dialogue);
    }

    private static void ValidateDialogues(LearningState state)
    {
        if (state.Dialogues is null) throw new InvalidDataException("Missing dialogue collection.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dialogue in state.Dialogues)
        {
            if (dialogue is null || !Text(dialogue.Id) || !ids.Add(dialogue.Id) || dialogue.Turns is not { Length: > 0 and <= 64 })
                throw new InvalidDataException("Invalid dialogue history.");
            for (int i = 0; i < dialogue.Turns.Length; i++)
            {
                var turn = dialogue.Turns[i];
                var meaning = turn?.Understanding;
                if (turn is null || !Text(turn.Text) || turn.Text.Length > 8192 || meaning is null || !Text(meaning.Interpreter) ||
                    meaning.Fields is null || meaning.Fields.Any(f => !Text(f.Key) || f.Value is null || f.Value.Any(v => v is null)) ||
                    meaning.Questions is null || meaning.Questions.Any(q => q is null || !Text(q.Field) || !Text(q.Text) || !Text(q.Reason)) ||
                    meaning.Evidence is null || meaning.Evidence.Any(e => e is null || !Text(e.Field) || e.Value is null || e.Turn < 0 || e.Turn > i || !Text(e.Quote)) ||
                    turn.AnswerTo != (i == 0 ? null : dialogue.Turns[i - 1].Understanding.Questions.FirstOrDefault()?.Field))
                    throw new InvalidDataException("Invalid language interpretation or answer linkage.");
                if (meaning.ModelExchange is { } model &&
                    (!Text(model.Provider) || !Text(model.RequestedModel) || !Text(model.PromptVersion) ||
                     model.RequestSha256 is null || model.RequestSha256.Length != 64 || !model.RequestSha256.All(Uri.IsHexDigit) ||
                     model.GoalBudgetLimit is < 1 or > 1000 || model.Response?.Length > 131072 ||
                     (model.Failure is null && (!Text(model.ReturnedModel) || !Text(model.Response)))))
                    throw new InvalidDataException("Invalid semantic exchange audit record.");
            }
            if (dialogue.GoalId is not null && (dialogue.Turns[^1].Understanding.Questions.Length != 0 || !state.Goals.Any(g => g.Goal.Id == dialogue.GoalId)))
                throw new InvalidDataException("Submitted dialogue lacks a resolved goal.");
        }
    }
}
