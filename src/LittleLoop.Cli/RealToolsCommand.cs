using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class RealToolsCommand
{
    public static bool Valid(string[] args) => (args.Length == 1 && args[0] == "tools-list") ||
        (args.Length >= 3 && args[0] == "tasks-run-tools");
    public static async Task Run(string[] args, JsonSerializerOptions json)
    {
        if (args[0] == "tools-list")
        {
            Console.WriteLine(JsonSerializer.Serialize(WorkspaceTools.List(), json));
            return;
        }
        var session = new LearningSession(args[1]);
        string policyFile = Path.GetFullPath(args[2]);
        var policy = JsonSerializer.Deserialize<WorkspaceToolPolicy>(File.ReadAllText(policyFile), json)
            ?? throw new JsonException("Expected workspace policy.");
        if (string.IsNullOrWhiteSpace(policy.Root)) throw new ArgumentException("Workspace root is required.");
        policy = policy with { Root = Path.GetFullPath(policy.Root, Path.GetDirectoryName(policyFile)!) };
        var executor = new WorkspaceToolExecutor(session, policy);
        var host = DeliberationCommand.CreateHost(args.Skip(3));
        host.Register(new WorkspaceToolPlugin(policy.AllowedTools));
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var state = await new MultiTaskRuntime(session, executor, host).RunAsync(cancellationToken: cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(state, json));
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
