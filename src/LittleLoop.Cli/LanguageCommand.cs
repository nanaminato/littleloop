using LittleLoop.Core;
using LittleLoop.Tools;
using System.Text.Json;

internal static class LanguageCommand
{
    public static bool Valid(string[] args) => (args.Length == 4 && args[0] == "goal-say") ||
        (args.Length is 3 or 4 && args[0] == "goal-chat") ||
        (args.Length == 5 && args[0] == "goal-model-say") || (args.Length is 4 or 5 && args[0] == "goal-model-chat") ||
        (args.Length == 3 && args[0] == "goal-model-replay");
    public static async Task Run(string[] args, JsonSerializerOptions json)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { await RunCore(args, json, cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }
    private static async Task RunCore(string[] args, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        var session = new LearningSession(args[1]);
        if (args[0] == "goal-model-replay")
        {
            var recorded = session.Read().Dialogues.SingleOrDefault(d => d.Id == args[2]) ?? throw new ArgumentException("Unknown dialogue.");
            GoalDialogue? prefix = null;
            foreach (var turn in recorded.Turns)
            {
                var replayed = turn.Understanding.ModelExchange is { } exchange
                    ? ModelGoalInterpreter.Validate(prefix, turn.Text, exchange)
                    : new DocumentGoalInterpreter().Interpret(prefix, turn.Text);
                if (JsonSerializer.Serialize(replayed, json) != JsonSerializer.Serialize(turn.Understanding, json))
                    throw new InvalidDataException("Semantic replay mismatch.");
                prefix = new(recorded.Id, [.. prefix?.Turns ?? [], turn], null);
            }
            Console.WriteLine($"语义重放通过：{recorded.Turns.Length} 轮；未调用模型或执行工具。");
            return;
        }
        bool modelMode = args[0].StartsWith("goal-model-", StringComparison.Ordinal);
        var config = modelMode ? JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(args[args[0] == "goal-model-say" ? 2 : 3]), json)
            ?? throw new JsonException("Expected model configuration.") : null;
        string? configPath = modelMode ? Path.GetFullPath(args[args[0] == "goal-model-say" ? 2 : 3]) : null;
        using var client = config is null ? null : new HttpSemanticModelClient(config,
            projectDirectory: LocalModelSecrets.FindProjectDirectory(Path.GetDirectoryName(configPath!)!));
        IAsyncGoalInterpreter? interpreter = client is null ? null : new ModelGoalInterpreter(client, config!.MaxGoalBudget, config.MaxInputCharacters);
        Task<GoalDialogue> Say(string conversationId, string text) => interpreter is null
            ? Task.FromResult(NaturalLanguageGoals.Say(session, conversationId, text))
            : NaturalLanguageGoals.SayAsync(session, conversationId, text, interpreter, cancellationToken);
        if (args[0] is "goal-say" or "goal-model-say")
        {
            Print(await Say(args[modelMode ? 3 : 2], args[modelMode ? 4 : 3]));
            return;
        }
        var file = Path.GetFullPath(args[2]);
        var policy = JsonSerializer.Deserialize<WorkspaceToolPolicy>(File.ReadAllText(file), json) ?? throw new JsonException("Expected workspace policy.");
        policy = policy with { Root = Path.GetFullPath(policy.Root, Path.GetDirectoryName(file)!) };
        _ = new WorkspaceToolExecutor(session, policy); // Validate host policy before starting a conversation.
        int idIndex = modelMode ? 4 : 3;
        string id = args.Length > idIndex ? args[idIndex] : Guid.NewGuid().ToString("N");
        Console.WriteLine("对话 " + id + "；请输入目标，输入 /exit 可退出并保留进度。");
        var dialogue = session.Read().Dialogues.SingleOrDefault(d => d.Id == id);
        if (dialogue is not null) Print(dialogue);
        while (dialogue?.GoalId is null)
        {
            Console.Write("> ");
            var text = Console.ReadLine();
            if (text is null || text == "/exit") return;
            if (string.IsNullOrWhiteSpace(text)) continue;
            dialogue = await Say(id, text);
            Print(dialogue);
        }
        IGoalPlanningAdvisor? advisor = config?.EnablePlanning == true
            ? new ModelPlanningAdvisor(client!, config.MaxPlanningCalls, config.MaxInputCharacters, config.MaxObservationCharacters) : null;
        while (true)
        {
            var state = await new GoalOrchestrator(session, policy, advisor).RunAsync(cancellationToken);
            var goal = state.Goals.Single(g => g.Goal.Id == dialogue.GoalId);
            Console.WriteLine($"结果：{goal.Status}；{goal.Detail}");
            Console.WriteLine("目标输出：" + goal.Resources["target"]);
            if (goal.Status != GoalStatus.AwaitingClarification) return;
            Console.Write("规划追问 > ");
            var answer = Console.ReadLine();
            if (answer is null || answer == "/exit") return;
            if (string.IsNullOrWhiteSpace(answer)) continue;
            session.AnswerGoal(goal.Goal.Id, answer, state.Revision);
        }
    }
    private static void Print(GoalDialogue dialogue)
    {
        Console.WriteLine("对话 ID：" + dialogue.Id);
        var understanding = dialogue.Turns[^1].Understanding;
        if (understanding.Questions.FirstOrDefault() is { } question) { Console.WriteLine(question.Text); return; }
        var fields = understanding.Fields;
        Console.WriteLine($"已理解：{(fields["mode"][0] == "normalize" ? "统一 LF 换行并去掉行尾空白" : "原样保留内容和格式")} → {fields["output"][0]}；读回验证，不覆盖文件。");
        Console.WriteLine($"目标已提交：{dialogue.GoalId}；预算 {fields.GetValueOrDefault("budget", ["12"])[0]} 次工具调用，重规划上限 {fields.GetValueOrDefault("replans", ["3"])[0]}。" );
    }
}
