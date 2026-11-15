using System.Text.Json;
using LittleLoop.Tools;

internal static class AgentCommand
{
    public static bool Valid(string[] args) => args.Length > 0 && args[0] switch
    {
        "agent-run" => args.Length == 4,
        "agent-send" => args.Length == 3,
        "agent-show" => args.Length == 2,
        "agent-decide" => args.Length == 4,
        _ => false
    };
    public static async Task Run(string[] args)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        string directory = args[0] == "agent-run" ? args[2] : args[1];
        var host = new AgentDelegationHost(directory);
        if (args[0] == "agent-show") { Console.WriteLine(JsonSerializer.Serialize(host.Read(), json)); return; }
        if (args[0] == "agent-decide") { host.Answer(args[2], args[3]); Console.WriteLine("Decision recorded for this pending request only."); return; }
        if (args[0] == "agent-run") host.Create(JsonSerializer.Deserialize<AgentHostPolicy>(File.ReadAllText(args[1]), json)!);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); }; Console.CancelKeyPress += handler;
        try
        {
            var result = await host.RunAsync(File.ReadAllText(args[^1]), cancel.Token);
            Console.WriteLine($"{result.Status}; thread={result.ThreadId}; turns={result.Turns.Length}");
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
