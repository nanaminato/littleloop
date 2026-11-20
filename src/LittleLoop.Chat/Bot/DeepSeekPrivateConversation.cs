using System.Security.Cryptography;
using System.Text.Json;
using LittleLoop.Core;

sealed class DeepSeekPrivateConversation : IDisposable
{
    private readonly FileStream lease;
    private readonly BotInput input;
    private readonly string checkpoint;
    private readonly HashSet<string> completed = [];
    private readonly Dictionary<string, string> calls = [];
    private string? guardedSession;
    public bool WorkspaceFiles { get; }
    public string Workspace { get; }
    public string Overlay { get; }
    public string Challenge { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public bool Submitted { get; private set; }

    public DeepSeekPrivateConversation(BotInput input, string sessionDir, bool workspaceFiles)
    {
        this.input = input;
        WorkspaceFiles = workspaceFiles;
        string state = SafeDirectory(sessionDir);
        Workspace = SafeDirectory(input.ConversationWorkspace ?? Path.Combine(state, "workspace"));
        string relative = Path.GetRelativePath(Workspace, state);
        if (relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Keep private ACP metadata outside the workspace.");
        checkpoint = Path.Combine(state, $"turn-{input.ConversationWorkId ?? Guid.NewGuid():N}.json");
        Overlay = Path.Combine(state, "deepseek-private.patch.yml");
        lease = new FileStream(Path.Combine(state, "deepseek.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            string plugin = Path.Combine(state, "private-guard.mjs");
            using var source = typeof(DeepSeekPrivateConversation).Assembly.GetManifestResourceStream("LittleLoop.DeepSeek.PrivateGuard")!;
            using (var reader = new StreamReader(source)) Replace(plugin, reader.ReadToEnd());
            string imported = JsonSerializer.Serialize(new Uri(plugin).AbsoluteUri);
            // This patch is applied last, after the trusted operator's ACP configuration.
            string enabled = workspaceFiles ? "false" : "true";
            Replace(Overlay, $"""
                - id: agent-instructions
                  disabled: true
                - id: tool-executor-ptc
                  disabled: true
                - id: tool-fs-search
                  disabled: true
                - id: tool-fs
                  disabled: {enabled}
                  config:
                    read: true
                    readImage: false
                    write: true
                    edit: true
                    applyPatch: false
                - insert:
                    - id: littleloop-private-workspace
                      name: {imported}
                """);
        }
        catch { lease.Dispose(); throw; }
    }

    public string? ExistingAnswer()
    {
        if (!File.Exists(checkpoint)) return null;
        using var prior = JsonDocument.Parse(AtomicSnapshot.ReadText(checkpoint));
        return prior.RootElement.TryGetProperty("answer", out var answer) && answer.GetString() is { Length: > 0 } text
            ? text : throw new ConversationProviderException(ConversationFailure.Interrupted);
    }

    public void ObserveGuard(JsonElement message)
    {
        if (!message.TryGetProperty("method", out var method) || method.GetString() != "littleloop/private-ready") return;
        var p = message.GetProperty("params");
        if (p.GetProperty("nonce").GetString() != Challenge || !string.Equals(p.GetProperty("workspace").GetString(),
            Workspace, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !p.GetProperty("tools").EnumerateArray().Select(x => x.GetString()).SequenceEqual(WorkspaceFiles ? ["read", "write", "edit"] : Array.Empty<string>()))
            throw new ConversationProviderException(ConversationFailure.PermissionDenied);
        guardedSession = p.GetProperty("sessionId").GetString();
    }

    public string Prompt(string sessionId, bool resumed, string? delegatedInstruction = null)
    {
        if (guardedSession != sessionId) throw new ConversationProviderException(ConversationFailure.PermissionDenied);
        var turn = resumed ? input with { History = [.. input.History.Where(x => x.Quoted)] } : input;
        string capabilities = WorkspaceFiles
            ? "你可用本会话原生 read、write、edit 工具读写工作区根目录中的普通 .txt、.md、.csv 文件。使用文件名即可。以工具回执为准。"
            : "当前私聊未启用工具，可以直接回答、解释、写作或计算。";
        return delegatedInstruction is null ? ChatConversation.Instructions + "\n" + capabilities + "\n" + ChatConversation.MessageContext(turn) :
            HarnessTurn.Instructions + "\n" + capabilities + "\n" + delegatedInstruction;
    }

    public void MarkSending()
    {
        Replace(checkpoint, """{"stage":"sending"}""");
        Submitted = true;
    }

    public void ObserveUpdate(JsonElement update)
    {
        string? type = update.GetProperty("sessionUpdate").GetString();
        if (type is not ("tool_call" or "tool_call_update") || !update.TryGetProperty("toolCallId", out var toolId) ||
            toolId.GetString() is not { Length: > 0 and <= 128 } id) return;
        if (type == "tool_call" && calls.Count < 64 && update.TryGetProperty("title", out var title) &&
            title.GetString() is "read" or "write" or "edit") calls.TryAdd(id, title.GetString()!);
        if (type == "tool_call_update" && update.TryGetProperty("status", out var status) && status.GetString() == "completed" &&
            calls.TryGetValue(id, out var tool)) completed.Add(tool);
    }

    public string Finish(string report)
    {
        string receipt = completed.Count == 0 ? "" : "\n\nDeepSeek 工具已完成：" + string.Join("、", completed.Order());
        string answer = report[..Math.Min(report.Length, 12000 - receipt.Length)] + receipt;
        Replace(checkpoint, JsonSerializer.Serialize(new { answer }));
        return answer;
    }

    private static void Replace(string path, string content)
    {
        File.WriteAllText(path + ".tmp", content);
        AtomicSnapshot.Replace(path + ".tmp", path);
    }

    public void Dispose() => lease.Dispose();

    private static string SafeDirectory(string path)
    {
        string absolute = Path.GetFullPath(path);
        for (DirectoryInfo? item = new(absolute); item is not null; item = item.Parent)
            if (item.LinkTarget is not null || (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("Private ACP directories cannot traverse links.");
        return Directory.CreateDirectory(absolute).FullName;
    }
}
