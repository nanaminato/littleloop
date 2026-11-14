using System.Text.Json;
using System.Text.Json.Serialization;
using LittleLoop.Core;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    RespectRequiredConstructorParameters = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
};

if (args.Length != 2 || args[0] is not ("run" or "replay"))
{
    Console.Error.WriteLine("Usage: LittleLoop.Cli run <scenario.json> | replay <trace.json>");
    return 2;
}

try
{
    var content = File.ReadAllText(args[1]);
    if (args[0] == "run")
    {
        var scenario = Read<Scenario>(content);
        var decision = TicketPolicy.Decide(scenario.Input);
        var receipt = TicketSimulation.Execute(scenario.Input, decision, scenario.ActualRoute);
        Console.WriteLine(JsonSerializer.Serialize(new DecisionTrace(scenario.Input, decision, receipt), json));
    }
    else
    {
        var trace = Read<DecisionTrace>(content);
        var decision = TicketPolicy.Decide(trace.Input);
        if (trace.Decision is null || trace.Receipt is null ||
            !string.Equals(JsonSerializer.Serialize(decision, json), JsonSerializer.Serialize(trace.Decision, json),
                StringComparison.Ordinal) ||
            trace.Receipt.DecisionId != decision.Id || trace.Receipt.Action != decision.Selected)
            throw new InvalidDataException("Replay mismatch: policy, decision, or receipt linkage differs.");

        Console.WriteLine(JsonSerializer.Serialize(new DecisionTrace(trace.Input, decision, trace.Receipt), json));
    }
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

T Read<T>(string content) => JsonSerializer.Deserialize<T>(content, json)
    ?? throw new JsonException("Expected a JSON object.");

sealed record Scenario(DecisionInput Input, string ActualRoute);
