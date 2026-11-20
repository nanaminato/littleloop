using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

sealed record HostTimeZoneRequest(string? Zone) : IHostSettingRequest
{
    public bool IsWrite => Zone is not null;
    public Task<RobotOutcome> Execute(bool allowChange, CancellationToken token) => HostTimeZone.Execute(this, allowChange, token);
}
enum ZoneChangeResult { Applied, PermissionDenied, Cancelled, Failed, Unknown, Unverified, Busy }

// A caller may time out while Windows still displays UAC. Keep ownership of the
// operation until ShellExecute and the actual child process have both finished.
sealed class ZoneChangeGate
{
    private readonly object sync = new();
    private Task<ZoneChangeResult>? pending;

    public async Task<ZoneChangeResult> Run(Func<Task<ZoneChangeResult>> start, CancellationToken deadline)
    {
        Task<ZoneChangeResult> attempt;
        lock (sync)
        {
            // No process handle or an unexpected fault means its exit cannot be established.
            if (pending is not null && (!pending.IsCompleted || pending.IsFaulted ||
                pending.IsCompletedSuccessfully && pending.Result == ZoneChangeResult.Unknown)) return ZoneChangeResult.Busy;
            pending = attempt = Task.Run(start, deadline);
        }
        try { return await attempt.WaitAsync(deadline); }
        catch (OperationCanceledException) { return ZoneChangeResult.Unknown; }
    }
}

static class HostTimeZone
{
    internal static readonly ZoneChangeGate Changes = new();
    private const string Prefix = @"(?:请(?:帮我)?|帮我|麻烦(?:你)?)? *";
    private const string Host = @"(?:这台(?:电脑|计算机|主机|服务器)|本机|本地(?:电脑|计算机)|宿主机)(?:的)?";
    private static readonly Regex Read = new(
        @"\A" + Prefix + @"(?:(?:查(?:看|询)?|看)(?:一下|看)?|告诉我)? *" + Host +
        @"(?:当前|现在)?时区(?:是什么|是哪个|是多少)?[？?。！!]?\z", RegexOptions.CultureInvariant);
    private static readonly Regex Set = new(
        @"\A" + Prefix + @"(?:以管理员(?:权限|身份) *)?(?:把|将)?" + Host +
        @"时区(?:调整|切换|设置|设|改|修改)(?:成|为|到) *(?<zone>北京时间|中国标准时间|北京|上海(?:时间|时区)?|[A-Za-z0-9_+./() -]{1,80})[。！!]?\z",
        RegexOptions.CultureInvariant);

    public static HostTimeZoneRequest? Parse(string text)
    {
        text = text.Trim();
        if (text.Length > 160) return null;
        if (Read.IsMatch(text)) return new(null);
        var match = Set.Match(text);
        if (!match.Success) return null;
        string zone = match.Groups["zone"].Value.Trim();
        if (zone.Length == 0 || (zone.Contains(' ') && !TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out _) &&
            !TimeZoneInfo.GetSystemTimeZones().Any(z => z.Id.Equals(zone, StringComparison.OrdinalIgnoreCase)))) return null;
        return new(zone);
    }

    public static Task<RobotOutcome> Execute(HostTimeZoneRequest request, bool allowChange, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new RobotOutcome("refused", "这个时区操作需要 Windows 宿主。"));
        using var host = Process.GetCurrentProcess();
        return Execute(request, allowChange, token, Environment.UserInteractive && host.SessionId != 0,
            TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id), ReadCurrent, SetWindowsZone);
    }

    internal static async Task<RobotOutcome> Execute(HostTimeZoneRequest request, bool allowChange, CancellationToken token,
        bool interactive, IEnumerable<string> installed, Func<string> read,
        Func<string, bool, CancellationToken, Task<ZoneChangeResult>> set)
    {
        token.ThrowIfCancellationRequested();
        if (request.Zone is null) return new("completed", $"这台电脑当前的时区是 {read()}。");
        if (!allowChange) return new("refused", "当前授权只允许查看这台电脑的时区。");
        string name = request.Zone switch
        {
            "北京" or "北京时间" or "中国标准时间" or "上海" or "上海时间" or "上海时区" => "China Standard Time",
            _ => request.Zone
        };
        if (name.Contains('/') && TimeZoneInfo.TryConvertIanaIdToWindowsId(name, out string? windowsId)) name = windowsId;
        string? target = installed.FirstOrDefault(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase));
        if (target is null) return new("refused", "这台电脑没有找到这个时区。可以使用 Windows 时区名称、北京时间或 UTC。");
        if (read() == target) return new("completed", $"这台电脑已经使用 {target}。");

        var result = await set(target, false, token);
        if (result == ZoneChangeResult.PermissionDenied)
        {
            if (!interactive) return new("refused", "Windows 要求管理员权限，当前宿主没有可以确认系统授权的桌面。");
            token.ThrowIfCancellationRequested();
            result = await set(target, true, token);
        }
        return result switch
        {
            ZoneChangeResult.Applied when read() == target => new("completed", $"已将这台电脑的时区设为 {target}，并已核对。"),
            ZoneChangeResult.PermissionDenied => new("refused", "Windows 拒绝了本次时区修改。"),
            ZoneChangeResult.Cancelled => new("refused", "已取消本次 Windows 管理员授权。"),
            ZoneChangeResult.Failed => new("failed", "Windows 时区工具未能完成修改。"),
            ZoneChangeResult.Busy => new("refused", "上一次时区操作尚未确认结束。请先处理或关闭系统授权窗口，并核对当前时区。"),
            _ => new("needs-reconciliation", "未能核实修改结果。请先关闭仍在等待的授权窗口，再查询这台电脑的当前时区。")
        };
    }

    private static string ReadCurrent()
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local.Id;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<ZoneChangeResult> SetWindowsZone(string id, bool elevated, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        return await Changes.Run(async () =>
        {
            string directory = Environment.SystemDirectory;
            if (!Path.IsPathFullyQualified(directory)) return ZoneChangeResult.Failed;
            var start = new ProcessStartInfo(Path.Combine(directory, "tzutil.exe"))
            {
                WorkingDirectory = directory, UseShellExecute = elevated,
                CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden
            };
            if (elevated) start.Verb = "runas";
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add(id);
            Process? process;
            try { process = Process.Start(start); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return ZoneChangeResult.Cancelled; }
            catch (Win32Exception e) when (e.NativeErrorCode is 5 or 740 or 1314) { return ZoneChangeResult.PermissionDenied; }
            catch (Win32Exception) { return ZoneChangeResult.Failed; }
            if (process is null) return ZoneChangeResult.Unknown;
            using (process)
            {
                await process.WaitForExitAsync();
                return process.ExitCode switch
                {
                    0 => ZoneChangeResult.Applied,
                    5 or 740 or 1314 => ZoneChangeResult.PermissionDenied,
                    _ when !elevated && LacksTimeZonePrivilege() => ZoneChangeResult.PermissionDenied,
                    _ => ZoneChangeResult.Failed
                };
            }
        }, deadline.Token);
    }

    // tzutil only documents zero for success. When it returns a generic failure, verify
    // the process token actually lacks this specific privilege before trying desktop consent.
    [SupportedOSPlatform("windows")]
    private static bool LacksTimeZonePrivilege()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (!LookupPrivilegeValue(null, "SeTimeZonePrivilege", out long zonePrivilege)) return false;
        const int tokenPrivileges = 3;
        GetTokenInformation(identity.AccessToken, tokenPrivileges, null, 0, out int size);
        if (size < sizeof(int) || size > 1024 * 1024) return false;
        var data = new byte[size];
        if (!GetTokenInformation(identity.AccessToken, tokenPrivileges, data, size, out size)) return false;
        // TOKEN_PRIVILEGES contains a DWORD count, followed by { LUID, DWORD attributes } entries.
        const int entrySize = sizeof(long) + sizeof(int);
        int count = BitConverter.ToInt32(data);
        if (count < 0 || count > (size - sizeof(int)) / entrySize) return false;
        for (int i = 0; i < count; i++)
            if (BitConverter.ToInt64(data, sizeof(int) + i * entrySize) == zonePrivilege) return false;
        return true;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll")]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        [Out] byte[]? data, int size, out int requiredSize);
}
