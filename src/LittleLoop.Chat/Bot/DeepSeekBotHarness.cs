using System.Text.Json;

sealed class DeepSeekBotHarness(DeepSeekAcpClient adapter) : NativeBotHarness
{
    public override string Name => "deepseek";
    public override string Description => adapter.UseLauncherPermissions ? "ACP，使用启动器配置的工具权限" : "ACP，项目工作区只读";
    public override string ProjectRoot => adapter.ProjectRoot;

    protected override Task<HarnessReceipt> Run(string directory, string workspace, bool isolated, string instruction,
        HarnessTurn turn, CancellationToken token) => adapter.Delegate(instruction, directory, workspace, isolated, turn, token);

    internal static void ObserveTool(HarnessTurn turn, Dictionary<string, string> names, JsonElement update)
    {
        string? type = update.GetProperty("sessionUpdate").GetString();
        if (type is not ("tool_call" or "tool_call_update") || !update.TryGetProperty("toolCallId", out var key) ||
            key.GetString() is not { Length: > 0 and <= 256 } id) return;
        if (!names.ContainsKey(id) && names.Count >= 64) return;
        string name = update.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } provided
            ? provided : names.GetValueOrDefault(id) ?? "tool";
        names[id] = name;
        string status = update.TryGetProperty("status", out var nativeStatus) ? nativeStatus.ToString() : "pending";
        string detail = update.TryGetProperty("rawOutput", out var output) ? output.ToString() :
            update.TryGetProperty("content", out var content) ? content.ToString() :
            update.TryGetProperty("rawInput", out var input) ? input.ToString() : "";
        turn.Tool(id, name, status, detail);
    }
}
