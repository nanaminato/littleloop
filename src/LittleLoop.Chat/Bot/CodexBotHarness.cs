using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

sealed class CodexBotHarness(string executable, string project, TimeSpan timeout) : NativeBotHarness
{
    public override string Name => "codex";
    public override string Description => "Codex App Server，使用部署配置的工具权限";
    public override string ProjectRoot => project;

    protected override async Task<HarnessReceipt> Run(string directory, string workspace, bool isolated, string instruction,
        HarnessTurn turn, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        if (isolated) Directory.CreateDirectory(workspace);
        await using var client = isolated ? CodexAppServerClient.ForPrivateWorkspace(executable, workspace, directory) :
            new CodexAppServerClient(executable, workspace, directory);
        await client.InitializeAsync(deadline.Token, experimental: true);
        if (isolated)
        {
            if (OperatingSystem.IsWindows() && await CodexPrivateConversation.WindowsSandboxMissing(client, deadline.Token))
                return turn.Finish("failed", "Codex 的 Windows 私聊工作区沙箱尚未就绪。");
            await CodexPrivateConversation.Run(client, new(instruction) { ConversationWorkId = turn.DispatchId }, directory,
                workspace, deadline.Token, delegation: turn);
        }
        else await SendProjectTurn(client, directory, workspace, instruction, turn, deadline.Token);
        return turn.Read()!;
    }

    internal static async Task SendProjectTurn(IAgentSessionClient client, string directory, string workspace, string instruction,
        HarnessTurn turn, CancellationToken token)
    {
        string file = Path.Combine(directory, "codex-thread.json");
        string? previous = File.Exists(file) ? JsonSerializer.Deserialize<string>(AtomicSnapshot.ReadText(file))
            ?? throw new InvalidDataException("Missing saved Codex thread ID.") : null;
        var parameters = new Dictionary<string, object> { ["cwd"] = workspace, ["developerInstructions"] = HarnessTurn.Instructions };
        if (previous is not null)
        {
            parameters["threadId"] = previous;
            parameters["excludeTurns"] = true;
        }
        var opened = await client.CallAsync(previous is null ? "thread/start" : "thread/resume", parameters, token);
        string? id = opened.GetProperty("thread").GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id) || previous is not null && id != previous)
            throw new InvalidDataException("Codex returned a different or empty thread ID.");
        if (previous is null) HarnessTurn.Save(file, JsonSerializer.Serialize(id));
        turn.Opened(id);
        await CodexPrivateConversation.Turn(client, id, null, turn.DispatchId, instruction, token, turn, deployedWorkspace: true);
    }

    internal static void ObserveTool(HarnessTurn turn, JsonElement item, bool started = false)
    {
        string? type = item.GetProperty("type").GetString();
        if (type is not ("commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall" or
            "collabAgentToolCall" or "webSearch" or "imageView" or "imageGeneration")) return;
        string id = item.TryGetProperty("id", out var key) ? key.GetString() ?? "" : "";
        string status = item.TryGetProperty("status", out var nativeStatus) ? nativeStatus.ToString() : started ? "inProgress" : "completed";
        if (item.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.GetInt32() != 0)
            status = "failed";
        var detail = new List<string>();
        foreach (string field in new[] { "exitCode", "command", "aggregatedOutput", "changes", "tool", "result", "error" })
            if (item.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null)
                detail.Add(field + "=" + HarnessTurn.Limit(value.ToString(), 700));
        turn.Tool(id, type, status, string.Join("\n", detail));
    }
}
