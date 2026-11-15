using System.Text.Json;
using LittleLoop.Tools;

internal static class GuidanceCommand
{
    public static bool Valid(string[] args) => args.Length == 5 && args[0] == "dev-guide";
    public static async Task Run(string[] args)
    {
        var context = ProjectGuidance.Gather(args[1], args[2]);
        string output = Path.GetFullPath(args[4]);
        if (output.StartsWith(context.Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Save guidance outside the target project.");
        if (File.Exists(output)) throw new ArgumentException("Guidance output already exists; choose a new file.");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        string configPath = Path.GetFullPath(args[3]);
        var config = JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(configPath), json)!;
        using var model = new HttpSemanticModelClient(config, projectDirectory: LocalModelSecrets.FindProjectDirectory(Path.GetDirectoryName(configPath)!));
        string input = JsonSerializer.Serialize(context, json);
        if (input.Length > config.MaxInputCharacters) throw new ArgumentException("Guidance exceeds configured model input bound.");
        var response = await model.CompleteAsync(ProjectGuidance.Prompt, input, CancellationToken.None);
        using var parsed = JsonDocument.Parse(response.Content);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected guidance object.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(file, new { context, provider = model.Provider, response.Model, guidance = parsed.RootElement,
            execution = "read-only advisory; no delegated work or permissions granted" }, json);
        Console.WriteLine($"只读指导已保存：{output}；依据 {context.Sources.Length} 份文档，未执行项目工具或发送 agent 消息。");
    }
}
