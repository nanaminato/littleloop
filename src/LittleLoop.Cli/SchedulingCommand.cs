using System.Text.Json;
using LittleLoop.Core;

internal static class SchedulingCommand
{
    public static bool Valid(string[] args) => args.Length >= 2 && args[0] switch
    {
        "tasks-enable" or "tasks-add" or "tasks-revise" => args.Length == 3,
        "tasks-think" or "tasks-dispatch" => args.Length == 2,
        "tasks-cancel" or "tasks-budget" => args.Length == 4,
        "tasks-run" => args.Length >= 3,
        _ => false
    };
    public static async Task Run(string[] args, JsonSerializerOptions json)
    {
        var session = new LearningSession(args[1]);
        object? output;
        switch (args[0])
        {
            case "tasks-enable": output = session.EnableScheduling(Read<RuntimeLimits>(args[2])); break;
            case "tasks-add":
                var add = Read<Submission>(args[2]);
                output = session.Enqueue(add.Task, add.Priority, add.DueAt); break;
            case "tasks-revise":
                var revision = Read<Revision>(args[2]);
                output = session.ReviseTask(revision.Task, revision.ExpectedVersion, revision.Priority, revision.DueAt); break;
            case "tasks-cancel": output = session.CancelTask(args[2], ParseLong(args[3])); break;
            case "tasks-budget":
                if (!int.TryParse(args[2], out int amount)) throw new ArgumentException("Budget must be an integer.");
                output = session.AddRuntimeBudget(amount, ParseLong(args[3])); break;
            case "tasks-think": output = session.ThinkNext(DateTimeOffset.UtcNow); break;
            case "tasks-dispatch": output = session.DispatchNext(); break;
            default:
                var environments = Read<Dictionary<string, Environment>>(args[2]);
                var executor = new SimulatedExecutor(environments);
                using (var cancellation = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try
                    {
                        output = await new MultiTaskRuntime(session, executor, DeliberationCommand.CreateHost(args.Skip(3)))
                            .RunAsync(cancellationToken: cancellation.Token);
                    }
                    finally { Console.CancelKeyPress -= cancel; }
                }
                break;
        }
        Console.WriteLine(JsonSerializer.Serialize(output, json));
        T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), json) ?? throw new JsonException("Expected JSON object.");
    }
    private static long ParseLong(string value) => long.TryParse(value, out var parsed) ? parsed : throw new ArgumentException("Version must be an integer.");
    private sealed record Submission(LearningTask Task, int Priority = 0, DateTimeOffset? DueAt = null);
    private sealed record Revision(LearningTask Task, long ExpectedVersion, int Priority = 0, DateTimeOffset? DueAt = null);
    private sealed record Environment(int DelayMilliseconds, Dictionary<string, string[]> OutcomesByStrategy);
    private sealed class SimulatedExecutor : IAsyncActionExecutor
    {
        private readonly Dictionary<string, (int Delay, OutcomeSimulation Simulation)> simulations;
        public SimulatedExecutor(Dictionary<string, Environment> environments)
        {
            if (environments.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null || kv.Value.DelayMilliseconds is < 0 or > 60000))
                throw new ArgumentException("Simulation delay must be 0..60000 milliseconds.");
            simulations = environments.ToDictionary(kv => kv.Key, kv =>
                (kv.Value.DelayMilliseconds, new OutcomeSimulation(kv.Value.OutcomesByStrategy)), StringComparer.Ordinal);
        }
        public async Task<ActionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        {
            if (!simulations.TryGetValue(request.TaskId, out var environment))
                throw new ArgumentException($"No simulation configured for task {request.TaskId}.");
            await Task.Delay(environment.Delay, cancellationToken);
            return environment.Simulation.Execute(request.Trace);
        }
    }
}
