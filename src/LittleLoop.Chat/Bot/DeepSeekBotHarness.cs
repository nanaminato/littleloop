using System.Text.Json;
using LittleLoop.Core;

sealed class DeepSeekBotHarness(DeepSeekAcpClient adapter) : NativeBotHarness
{
    public override string Name => "deepseek";
    public override string Description => adapter.UseLauncherPermissions ? "ACP，使用启动器配置的工具权限" : "ACP，项目工作区只读";
    public override string ProjectRoot => adapter.ProjectRoot;

    public override BotHarnessOption Describe(bool isolated, string workspaceScope)
    {
        (string[] capabilities, string permission) = isolated
            ? adapter.ConversationWorkspaceFiles
                ? (new[] { "text-analysis", "workspace-text-read", "workspace-text-write", "workspace-text-edit" }, "private-text-files")
                : (new[] { "text-analysis" }, "text-only")
            : adapter.UseLauncherPermissions
                ? (new[] { "text-analysis", "configured-tools" }, "deployment-configured")
                : (new[] { "text-analysis", "workspace-read" }, "read-only");
        bool available = adapter.LauncherAvailable;
        return new(Name, Description, capabilities, workspaceScope, permission, available ? "available-to-queue" : "unavailable",
            available ? null : "Configured ACP launcher is unavailable.");
    }

    protected override Task<HarnessReceipt> Run(string directory, string workspace, bool isolated, string instruction,
        HarnessTurn turn, CancellationToken token) => adapter.Delegate(instruction, directory, workspace, isolated, turn, token);

    internal static void ObserveTool(HarnessTurn turn, Dictionary<string, string> names, JsonElement update)
    {
        string? type = update.GetProperty("sessionUpdate").GetString();
        if (type is not ("tool_call" or "tool_call_update")) return;
        string id = update.TryGetProperty("toolCallId", out var key) ? key.GetString() ?? "" : "";
        string name = update.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } provided
            ? provided : names.GetValueOrDefault(id) ?? "tool";
        if (id.Length is > 0 and <= 256 && (names.Count < 64 || names.ContainsKey(id))) names[id] = name;
        string status = update.TryGetProperty("status", out var nativeStatus) ? nativeStatus.ToString() : "pending";
        string detail = update.TryGetProperty("rawOutput", out var output) ? output.ToString() :
            update.TryGetProperty("content", out var content) ? content.ToString() :
            update.TryGetProperty("rawInput", out var input) ? input.ToString() : "";
        turn.Tool(id, name, status, detail);
    }
}
