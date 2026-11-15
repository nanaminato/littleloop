using System.Text.Json;
using System.Text.Json.Serialization;
using LittleLoop.Core;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    RespectRequiredConstructorParameters = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
};

if (!AgentCommand.Valid(args) && !GuidanceCommand.Valid(args) && !ProjectDevelopmentCommand.Valid(args) && !(args.Length > 0 && DevelopmentCommand.Valid(args)) && !LanguageCommand.Valid(args) && !GoalsCommand.Valid(args) && !RealToolsCommand.Valid(args) && !SchedulingCommand.Valid(args) && !LearningCommand.Valid(args) && (args.Length < 2 || args[0] is not ("run" or "replay" or "deliberate" or "replay-deliberation") ||
    (args[0] != "deliberate" && args.Length != 2)))
{
    Console.Error.WriteLine("Usage: run <scenario.json> | replay <trace.json> | deliberate <input.json> [trusted-plugin.dll ...] | replay-deliberation <trace.json>");
    Console.Error.WriteLine("Learning: learn-init <state.json> [role.json] | learn-show <state.json> | learn-run <state.json> <scenario.json> [trusted-plugin.dll ...] | learn-decide <state.json> <task.json> [trusted-plugin.dll ...] | learn-result <state.json> <result.json> | learn-feedback <state.json> <feedback.json> | learn-replay <state.json> <decision-id>");
    Console.Error.WriteLine("Tasks: tasks-enable <state> <limits.json> | tasks-add <state> <submission.json> | tasks-revise <state> <revision.json> | tasks-cancel <state> <task-id> <version> | tasks-budget <state> <amount> <revision> | tasks-think <state> | tasks-dispatch <state> | tasks-run <state> <environments.json> [trusted-plugin.dll ...]");
    Console.Error.WriteLine("Real tools: tools-list | tasks-run-tools <state> <workspace-policy.json> [trusted-plugin.dll ...]");
    Console.Error.WriteLine("Goals: goals-add <state> <document-goal.json> | goals-run <state> <workspace-policy.json>");
    Console.Error.WriteLine("Planning: goals-run-model <state> <workspace-policy.json> <model.json> | goals-answer <state> <goal-id> <answer>");
    Console.Error.WriteLine("Language: goal-say <state> <conversation-id> <text> | goal-chat <state> <workspace-policy.json> [conversation-id]");
    Console.Error.WriteLine("Model: goal-model-say <state> <model.json> <conversation-id> <text> | goal-model-chat <state> <workspace-policy.json> <model.json> [conversation-id]");
    Console.Error.WriteLine("Model replay: goal-model-replay <state> <conversation-id>");
    Console.Error.WriteLine("Development: dev-map <policy.json> [prefix offset] | dev-run <state> <project-policy.json> <model.json> <id> [goal text] | dev-answer <state> <id> <answer>");
    Console.Error.WriteLine("Project: dev-project <path> <goal> [model.json] [session-dir] | dev-project-show <session-dir> | dev-permit <session-dir> <request-id> approve|deny | dev-project-resume <session-dir> | dev-project-retry <session-dir>");
    Console.Error.WriteLine("Guidance: dev-guide <project> <goal> <model.json> <output.json>");
    Console.Error.WriteLine("Agent: agent-run <policy.json> <session-dir> <task.txt> | agent-send <session-dir> <task.txt> | agent-show <session-dir> | agent-decide <session-dir> <nonce> accept|decline");
    return 2;
}

try
{
    if (AgentCommand.Valid(args)) { await AgentCommand.Run(args); return 0; }
    if (GuidanceCommand.Valid(args)) { await GuidanceCommand.Run(args); return 0; }
    if (ProjectDevelopmentCommand.Valid(args)) { await ProjectDevelopmentCommand.Run(args); return 0; }
    if (DevelopmentCommand.Valid(args))
    {
        await DevelopmentCommand.Run(args, json);
        return 0;
    }
    if (LanguageCommand.Valid(args))
    {
        await LanguageCommand.Run(args, json);
        return 0;
    }
    if (GoalsCommand.Valid(args))
    {
        await GoalsCommand.Run(args, json);
        return 0;
    }
    if (RealToolsCommand.Valid(args))
    {
        await RealToolsCommand.Run(args, json);
        return 0;
    }
    if (SchedulingCommand.Valid(args))
    {
        await SchedulingCommand.Run(args, json);
        return 0;
    }
    if (LearningCommand.Valid(args))
    {
        LearningCommand.Run(args, json);
        return 0;
    }
    if (args[0] is "deliberate" or "replay-deliberation")
    {
        DeliberationCommand.Run(args, json);
        return 0;
    }
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
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Driver cancelled. Check pending execution outcomes before resuming.");
    return 130;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or OverflowException
    or BadImageFormatException or TypeLoadException or MissingMethodException or System.Reflection.TargetInvocationException
    or System.Reflection.ReflectionTypeLoadException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

T Read<T>(string content) => JsonSerializer.Deserialize<T>(content, json)
    ?? throw new JsonException("Expected a JSON object.");

sealed record Scenario(DecisionInput Input, string ActualRoute);
