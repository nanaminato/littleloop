using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

sealed class HostPairing(string directory)
{
    private readonly string binding = Path.Combine(Path.GetFullPath(directory), "host-pairing.id");
    private readonly SemaphoreSlim pending = new(1, 1);

    public bool IsBound(string accountId)
    {
        if (!Guid.TryParseExact(accountId, "D", out var account) || account == Guid.Empty) return false;
        try { return ReadBinding() == account; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    public Task<RobotOutcome> Confirm(string accountId, string accountName, CancellationToken token,
        Func<Task<bool>>? stillEligible = null)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(new RobotOutcome("refused", "需要在这台 Windows 电脑的桌面上确认绑定。"));
        return Confirm(accountId, accountName, token, HostDesktopSettings.DesktopAccount, AskWindows, stillEligible);
    }

    internal async Task<RobotOutcome> Confirm(string accountId, string accountName, CancellationToken token,
        Func<string?> desktopAccount, Func<string, Task<int>> ask, Func<Task<bool>>? stillEligible)
    {
        if (!Guid.TryParseExact(accountId, "D", out Guid account) || account == Guid.Empty ||
            accountName.Length is < 3 or > 32 || accountName.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            return new("refused", "无法核对这个聊天账号。");
        if (!await pending.WaitAsync(0, token)) return new("refused", "这台电脑上已有绑定请求等待确认。");
        try
        {
            if (stillEligible is not null && !await stillEligible()) return new("refused", "这个账号当前没有绑定资格。");
            if (ReadBinding() is { } existing) return Existing(existing == account);
            string? owner = desktopAccount();
            if (owner is null) return new("refused", "找不到可确认绑定的本机用户桌面。");
            string message = $"允许聊天账号「{accountName}」绑定这台电脑吗？\n\n" +
                "绑定后，该账号可以在 LittleLoop 私聊中查看和修改这台 Windows 电脑的时区、关屏和睡眠超时、" +
                "资源管理器的隐藏文件和文件扩展名显示，以及应用的深浅色主题。需要管理员权限的操作仍由 Windows 提示确认。\n\n" +
                "这项授权在服务重启后继续有效。仅在确认这是你要授权的账号时选择“是”。";
            int answer = await ask(message);
            token.ThrowIfCancellationRequested();
            if (answer != 6) return new("refused", answer == 32000 ? "本地确认已超时，账号没有绑定。" : "未收到本地用户同意，账号没有绑定。");
            if (!string.Equals(desktopAccount(), owner, StringComparison.OrdinalIgnoreCase) ||
                stillEligible is not null && !await stillEligible()) return new("refused", "绑定资格或本机桌面已变化，请重新发起。");
            token.ThrowIfCancellationRequested();
            string temporary = binding + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.ASCII.GetBytes(account.ToString("D")));
                    file.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, binding, overwrite: false); }
                catch (IOException) when (File.Exists(binding)) { return Existing(ReadBinding() == account); }
                return new("completed", "本地用户已确认绑定。现在可以在此私聊中查看或调整已支持的 Windows 设置。");
            }
            finally { File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new("failed", "无法核对或保存这台电脑的绑定记录，请让管理员检查服务数据目录。");
        }
        finally { pending.Release(); }
    }

    private static RobotOutcome Existing(bool same) => same ? new("completed", "这个账号已经绑定这台电脑。") :
        new("refused", "这台电脑已有绑定账号。");

    private Guid? ReadBinding()
    {
        try
        {
            using var file = new FileStream(binding, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> bytes = stackalloc byte[36];
            if (file.Length != bytes.Length) throw new InvalidDataException("Invalid host pairing record length.");
            file.ReadExactly(bytes);
            return Guid.TryParseExact(Encoding.ASCII.GetString(bytes), "D", out Guid id) && id != Guid.Empty ? id :
                throw new InvalidDataException("Invalid host pairing identifier.");
        }
        catch (FileNotFoundException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> AskWindows(string message) => Task.Run(() =>
    {
        const string title = "LittleLoop · 绑定这台电脑";
        const uint yesNoWarningDefaultNo = 0x4 | 0x30 | 0x100;
        using var process = Process.GetCurrentProcess();
        return WTSSendMessageW(IntPtr.Zero, process.SessionId, title, title.Length * sizeof(char), message,
            message.Length * sizeof(char), yesNoWarningDefaultNo, 45, out int response, true) ? response : 0;
    });

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool WTSSendMessageW(IntPtr server, int sessionId, string title, int titleBytes, string message,
        int messageBytes, uint style, uint timeoutSeconds, out int response, bool wait);
}
