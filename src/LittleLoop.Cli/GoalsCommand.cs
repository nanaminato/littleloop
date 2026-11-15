using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class GoalsCommand
{
    public static bool Valid(string[] args) => (args.Length == 3 && args[0] is "goals-add" or "goals-run") ||
        (args.Length == 4 && args[0] is "goals-run-model" or "goals-answer");
    public static async Task Run(string[] args, JsonSerializerOptions json)
    {
        var session = new LearningSession(args[1]);
        if (args[0] == "goals-answer")
        {
            Console.WriteLine(JsonSerializer.Serialize(session.AnswerGoal(args[2], args[3], session.Read().Revision), json));
            return;
        }
        if (args[0] == "goals-add")
        {
            var goal = JsonSerializer.Deserialize<DocumentGoal>(File.ReadAllText(args[2]), json) ?? throw new JsonException("Expected document goal.");
            Console.WriteLine(JsonSerializer.Serialize(DocumentGoals.Add(session, goal), json));
            return;
        }
        var file = Path.GetFullPath(args[2]);
        var policy = JsonSerializer.Deserialize<WorkspaceToolPolicy>(File.ReadAllText(file), json) ?? throw new JsonException("Expected workspace policy.");
        policy = policy with { Root = Path.GetFullPath(policy.Root, Path.GetDirectoryName(file)!) };
        var config = args[0] == "goals-run-model" ? JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(args[3]), json)
            ?? throw new JsonException("Expected model configuration.") : null;
        using var client = config is null ? null : new HttpSemanticModelClient(config,
            projectDirectory: LocalModelSecrets.FindProjectDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!));
        IGoalPlanningAdvisor? advisor = config is null ? null : new ModelPlanningAdvisor(client!, config.MaxPlanningCalls, config.MaxInputCharacters, config.MaxObservationCharacters);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { Console.WriteLine(JsonSerializer.Serialize(await new GoalOrchestrator(session, policy, advisor).RunAsync(cancellation.Token), json)); }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
