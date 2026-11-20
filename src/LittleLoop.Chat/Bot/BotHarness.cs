using System.Text.Json;
using LittleLoop.Core;

interface IBotHarness
{
    string Name { get; }
    string Description => Name;
    string? ProjectRoot { get; }
    Task<HarnessReceipt> SendAsync(string sessionDirectory, Guid dispatchId, string instruction,
        Func<CancellationToken, Task<bool>>? shouldInterrupt, CancellationToken token, string? workspace = null);
    HarnessRunState GetStatus(string sessionDirectory, Guid dispatchId);
    void Interrupt(string sessionDirectory, Guid dispatchId);
}

sealed record HarnessRunState(string Status, string? SessionId = null);
sealed record HarnessToolResult(string Id, string Kind, string Status, string Detail);
sealed record HarnessReceipt(string Status, string? SessionId, string Report, HarnessToolResult[] Tools);

// Each goal owns one directory per harness. A dispatch identifies exactly one native prompt.
// Journal submission before writing to the pipe. A lost reply must never replay side effects.
abstract class NativeBotHarness : IBotHarness
{
    public abstract string Name { get; }
    public virtual string Description => Name;
    public abstract string ProjectRoot { get; }

    public async Task<HarnessReceipt> SendAsync(string sessionDirectory, Guid dispatchId, string instruction,
        Func<CancellationToken, Task<bool>>? shouldInterrupt, CancellationToken token, string? workspace = null)
    {
        string directory = Directory.CreateDirectory(sessionDirectory).FullName;
        var journal = new HarnessTurn(directory, dispatchId, shouldInterrupt);
        using var lease = await LockSession(directory, token);
        if (Recover(journal) is { } prior) return prior;
        try
        {
            string workingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace ?? ProjectRoot));
            string binding = Path.Combine(directory, "workspace.json");
            string scope = JsonSerializer.Serialize(new { harness = Name, workspace = workingDirectory, isolated = workspace is not null });
            if (File.Exists(binding) && AtomicSnapshot.ReadText(binding) != scope)
                return journal.Finish("failed", "此目标的 Harness 工作目录或部署范围已变化，无法在原会话继续。");
            HarnessTurn.Save(binding, scope);
            journal.Opening();
            if (await journal.ShouldInterrupt(token))
                return journal.Finish("interrupted", "本轮指令在发送前取消。");
            return await Run(directory, workingDirectory, workspace is not null, instruction, journal, token);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            string detail = e is OperationCanceledException
                ? token.IsCancellationRequested ? "服务取消了本轮调用。" : "等待 Harness 回执超时。"
                : e.Message;
            // Closing the protocol process may fail after its terminal reply has already been journaled.
            var saved = journal.Read();
            return saved is { Status: "returned" or "interrupted" or "failed" } ? saved :
                journal.Finish(journal.Submitted ? "uncertain" : "failed", detail);
        }
    }

    protected abstract Task<HarnessReceipt> Run(string directory, string workspace, bool isolated,
        string instruction, HarnessTurn turn, CancellationToken token);

    // Reading evidence needs neither the original executable nor an active user's project grant.
    // The worker owns the service lease before it examines a recovered native dispatch.
    public static HarnessReceipt? RecoverReceipt(string sessionDirectory, Guid dispatchId)
    {
        try
        {
            using var lease = new FileStream(Path.Combine(sessionDirectory, "harness.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            return Recover(new HarnessTurn(sessionDirectory, dispatchId, null));
        }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new("uncertain", null, "无法核对已派发指令：" + error.Message, []); }
    }

    private static HarnessReceipt? Recover(HarnessTurn journal)
    {
        try
        {
            return journal.Read() switch
            {
                null or { Status: "opening" } => null,
                { Status: "running" } => journal.Finish("uncertain", "服务在发出原生指令后中断，缺少最终回执。"),
                { Status: "returned" or "failed" or "interrupted" or "uncertain" } terminal => terminal,
                _ => journal.Finish("uncertain", "原生指令的状态无法识别，未重新发送。")
            };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return journal.Finish("uncertain", "原生指令记录无法解析，未重新发送。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new("uncertain", journal.SessionId, "无法读取原生指令记录：" + error.Message, []);
        }
    }

    private static async Task<FileStream> LockSession(string directory, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "harness.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33) { await Task.Delay(100, token); }
        }
    }

    public HarnessRunState GetStatus(string sessionDirectory, Guid dispatchId)
    {
        var journal = new HarnessTurn(sessionDirectory, dispatchId, null);
        HarnessReceipt? record;
        try { record = journal.Read(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { return new("uncertain"); }
        if (record is null) return new("idle");
        if (record.Status is "opening" or "running")
        {
            try
            {
                using var lease = new FileStream(Path.Combine(sessionDirectory, "harness.lock"), FileMode.Open, FileAccess.Write, FileShare.None);
                record = journal.Read()!;
                return new(record.Status switch { "running" => "uncertain", "opening" => "idle", _ => record.Status }, record.SessionId);
            }
            catch (IOException) { } // A worker still holds the session.
        }
        return new(record.Status is "opening" or "running" or "returned" or "failed" or "interrupted" ? record.Status : "uncertain",
            record.SessionId);
    }

    public void Interrupt(string sessionDirectory, Guid dispatchId)
    {
        Directory.CreateDirectory(sessionDirectory);
        using var flag = new FileStream(Path.Combine(sessionDirectory, $"{dispatchId:N}.stop"), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.ReadWrite);
    }
}

sealed class HarnessTurn(string directory, Guid dispatchId, Func<CancellationToken, Task<bool>>? shouldInterrupt)
{
    private readonly string file = Path.Combine(directory, $"{dispatchId:N}.json");
    private HarnessReceipt current = new("opening", null, "", []);
    public Guid DispatchId => dispatchId;
    public bool Submitted { get; private set; }
    public string? SessionId => current.SessionId;
    public const string Instructions = "LittleLoop 是决策主体。执行本轮委派，向 LittleLoop 返回实际结果、测试失败、阻碍和仍需完成的工作。";

    public HarnessReceipt? Read()
    {
        string content;
        try { content = AtomicSnapshot.ReadText(file); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        var saved = JsonSerializer.Deserialize<HarnessReceipt>(content);
        if (saved is not { Status: not null, Report: not null, Tools: not null } ||
            saved.Tools.Any(t => t is not { Id: not null, Kind: not null, Status: not null, Detail: not null }))
            throw new InvalidDataException("Invalid Harness dispatch record.");
        return current = saved;
    }

    public void Opening() => Write(current with { Status = "opening" });
    public void Opened(string id) => Write(current with { SessionId = id });
    public void Sending()
    {
        Write(current with { Status = "running" });
        Submitted = true;
    }

    public async Task<bool> ShouldInterrupt(CancellationToken token) =>
        File.Exists(Path.Combine(directory, $"{dispatchId:N}.stop")) || shouldInterrupt is not null && await shouldInterrupt(token);

    public void Tool(string id, string kind, string status, string detail)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256) return;
        var existing = current.Tools.FirstOrDefault(t => t.Id == id);
        if (existing is null && current.Tools.Length >= 64) return;
        if (detail.Length == 0 && existing is not null) detail = existing.Detail;
        var result = new HarnessToolResult(id, Limit(kind, 512), status, Limit(detail, 1000));
        if (result == existing) return;
        Write(current with { Tools = existing is null ? [.. current.Tools, result] :
            current.Tools.Select(t => t.Id == id ? result : t).ToArray() });
    }

    public HarnessReceipt Finish(string status, string report) => Write(current with { Status = status, Report = Limit(report, 12000) });

    private HarnessReceipt Write(HarnessReceipt value)
    {
        Save(file, JsonSerializer.Serialize(value));
        return current = value;
    }

    internal static void Save(string path, string value)
    {
        File.WriteAllText(path + ".tmp", value);
        AtomicSnapshot.Replace(path + ".tmp", path);
    }

    internal static string Limit(string value, int max) => value.Length <= max ? value :
        value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
}
