using System.Text.Json;
using LittleLoop.Core;

internal static class LearningCommand
{
    public static bool Valid(string[] args) => args.Length >= 2 && args[0] switch
    {
        "learn-init" => args.Length is 2 or 3,
        "learn-show" => args.Length == 2,
        "learn-run" or "learn-decide" => args.Length >= 3,
        "learn-result" or "learn-feedback" or "learn-replay" => args.Length == 3,
        _ => false
    };

    public static void Run(string[] args, JsonSerializerOptions json)
    {
        var session = new LearningSession(args[1]);
        object output;
        switch (args[0])
        {
            case "learn-init":
                output = session.Initialize(args.Length == 3 ? Read<PersistentRole>(args[2]) : null);
                break;
            case "learn-decide":
                output = session.Begin(Read<LearningTask>(args[2]), DeliberationCommand.CreateHost(args.Skip(3)));
                break;
            case "learn-run":
                var scenario = Read<LearningScenario>(args[2]);
                if (scenario.OutcomesByStrategy is null || scenario.OutcomesByStrategy.Any(x => x.Value is null))
                    throw new ArgumentException("Simulation outcomes must be provided.");
                output = session.Run(scenario.Task, new OutcomeSimulation(scenario.OutcomesByStrategy),
                    DeliberationCommand.CreateHost(args.Skip(3)));
                break;
            case "learn-result": output = session.Complete(Read<ActionResult>(args[2])); break;
            case "learn-feedback": output = session.Feedback(Read<ResultFeedback>(args[2])); break;
            case "learn-replay":
                var episode = session.Read().Episodes.SingleOrDefault(e => e.Trace.Input.Id == args[2])
                    ?? throw new ArgumentException("Unknown decision ID.");
                var decision = DeliberationPolicy.Decide(episode.Trace.Input, episode.Trace.Responses);
                if (JsonSerializer.Serialize(decision, json) != JsonSerializer.Serialize(episode.Trace.Decision, json))
                    throw new InvalidDataException("Learning trace replay mismatch.");
                output = episode; // Original receipt and feedback are records, never re-executed.
                break;
            default: output = session.Read(); break;
        }
        Console.WriteLine(JsonSerializer.Serialize(output, json));
        T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), json)
            ?? throw new JsonException("Expected a JSON object.");
    }
    private sealed record LearningScenario(LearningTask Task, Dictionary<string, string[]> OutcomesByStrategy);
}
