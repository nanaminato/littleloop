using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class DevelopmentCommand
{
    public static bool Valid(string[] args) => args.Length >= 1 &&
        (args[0] == "dev-run" && args.Length is 5 or 6 ||
         args[0] == "dev-answer" && args.Length == 4 ||
         args[0] == "dev-map" && args.Length is 2 or 4);
    public static async Task Run(string[] args, JsonSerializerOptions json)
    {
        if (args[0] == "dev-map")
        {
            string mapPolicyFile = Path.GetFullPath(args[1]);
            string mapPrefix = "";
            int mapOffset = 0;
            if (args.Length is 3 or 4) mapPrefix = args[2];
            if (args.Length == 3)
                throw new ArgumentException("Provide both prefix and offset, or neither.");
            if (args.Length == 4 &&
                (!int.TryParse(args[3], out mapOffset) || mapOffset < 0))
                throw new ArgumentException("Page offset must be a non-negative integer.");
            var mapPolicy = JsonSerializer.Deserialize<DevelopmentPolicy>(File.ReadAllText(mapPolicyFile), json) ?? throw new JsonException("Expected development policy.");
            mapPolicy = mapPolicy with { Root = Path.GetFullPath(mapPolicy.Root, Path.GetDirectoryName(mapPolicyFile)!) };
            var mapFiles = new ProjectFiles(mapPolicy);
            Console.WriteLine(ProjectInspection.Inspect(mapFiles, mapPrefix, mapOffset));
            return;
        }
        var session = new LearningSession(args[1]);
        if (args[0] == "dev-answer")
        { Console.WriteLine(JsonSerializer.Serialize(session.AnswerDevelopment(args[2], args[3], session.Read().Revision), json)); return; }
        string policyFile = Path.GetFullPath(args[2]), modelFile = Path.GetFullPath(args[3]);
        var policy = JsonSerializer.Deserialize<DevelopmentPolicy>(File.ReadAllText(policyFile), json) ?? throw new JsonException("Expected development policy.");
        policy = policy with { Root = Path.GetFullPath(policy.Root, Path.GetDirectoryName(policyFile)!) };
        var files = new ProjectFiles(policy);
        var config = JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(modelFile), json) ?? throw new JsonException("Expected model configuration.");
        using var client = new HttpSemanticModelClient(config, projectDirectory: LocalModelSecrets.FindProjectDirectory(Path.GetDirectoryName(modelFile)!));
        var runner = new DevelopmentRunner(session, files, client, config.MaxInputCharacters, config.MaxObservationCharacters);
        var existing = session.Read().DevelopmentRuns.SingleOrDefault(r => r.Id == args[4]);
        if (existing is null)
        {
            if (args.Length != 6) throw new ArgumentException("Provide a natural-language goal when starting development work.");
            runner.Start(args[4], args[5]);
        }
        else if (args.Length == 6 && args[5] != existing.Goal) throw new ArgumentException("Existing goal cannot be silently changed; choose a new ID.");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var result = await runner.RunAsync(args[4], cancel.Token);
            Console.WriteLine($"开发任务：{result.Status}\n{result.Detail}\n动作：{result.Steps.Length}，失败：{result.Failures}，模型审议：{result.Reviews.Length}");
            if (result.Status == GoalStatus.AwaitingClarification) Console.WriteLine("使用 dev-answer 记录回答，再运行相同 dev-run 恢复。");
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
