using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

enum HostPowerSource { PluggedIn, Battery }
enum HostPowerTimer { Display, Sleep }
sealed record HostPowerRequest(HostPowerSource? Source, HostPowerTimer? Timer, uint? Minutes) : IHostSettingRequest
{
    public bool IsWrite => Minutes.HasValue;
    public Task<RobotOutcome> Execute(bool allowChange, CancellationToken token) => HostPower.Execute(this, allowChange, token);
}

static class HostPower
{
    private const string Prefix = @"\A(?:请(?:帮我)?|帮我|麻烦(?:你)?)?\s*";
    private const string Host = @"(?:这台(?:电脑|计算机|主机|服务器)|本机|宿主机)(?:的)?\s*";
    private const string Supply = @"(?:(?<ac>接通电源|接电源|接电|插着电|插电|交流供电)|(?<dc>使用电池|用电池|电池供电|电池模式|电池))(?:时|下)?(?:的)?\s*";
    private const string Timer = @"(?:(?<display>关闭(?:屏幕|显示器)|(?:屏幕|显示器)关闭|屏幕|熄屏)|(?<sleep>自动睡眠|进入睡眠|睡眠))(?:等待)?(?:超时(?:时间)?|时间)?";
    private const string Duration = @"(?:(?<never>永不|从不)|(?<half>半小时)|(?<number>[0-9]{1,8})\s*(?<unit>分钟|小时))";
    private const string SourceOptional = "(?:" + Supply + ")?";
    private const string ReadEnd = @"(?:是(?:什么|多少)|是多少(?:分钟)?|多久|有哪些)?[？?。！!]?\z";
    private const string SetEnd = @"[。！!]?\z";
    private static readonly Regex Read = new(Prefix + @"(?:(?:查(?:看|询)?|看)(?:一下|看)?|告诉我)?\s*" + Host +
        @"(?:当前|现在)?(?:的)?(?:电源设置|电源超时设置|电源超时|" + SourceOptional + @"(?:多久(?:会|后)?\s*)?" + Timer + ")" + ReadEnd,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Write = new(Prefix + @"(?:以管理员(?:身份|权限)\s*)?(?:把|将)?" + Host + SourceOptional +
        "(?:" + Timer + @"(?:调整|修改|设置|改|设)(?:成|为|到)\s*" + Duration +
        @"|(?:闲置|空闲|不操作)?\s*" + Duration + @"(?:后|以后)(?:自动)?(?:(?<display>关闭(?:屏幕|显示器)|熄屏)|(?<sleep>睡眠|进入睡眠))" +
        @"|(?<never>永不|从不)(?:自动)?(?:(?<display>关闭(?:显示器|屏幕)|熄屏)|(?<sleep>睡眠|进入睡眠)))" + SetEnd,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static HostPowerRequest? Parse(string text)
    {
        text = text.Trim();
        if (text.Length > 160) return null;
        var match = Write.Match(text);
        uint? minutes = null;
        if (match.Success)
        {
            if (match.Groups["never"].Success) minutes = 0;
            else if (match.Groups["half"].Success) minutes = 30;
            else
            {
                uint amount = uint.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
                ulong total = match.Groups["unit"].Value == "小时" ? amount * 60UL : amount;
                if (total > uint.MaxValue / 60) return null;
                minutes = (uint)total;
            }
        }
        else
        {
            match = Read.Match(text);
            if (!match.Success) return null;
        }
        HostPowerSource? source = match.Groups["ac"].Success ? HostPowerSource.PluggedIn :
            match.Groups["dc"].Success ? HostPowerSource.Battery : null;
        HostPowerTimer? timer = match.Groups["display"].Success ? HostPowerTimer.Display :
            match.Groups["sleep"].Success ? HostPowerTimer.Sleep : null;
        return new(source, timer, minutes);
    }

    public static Task<RobotOutcome> Execute(HostPowerRequest request, bool allowChange, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(new RobotOutcome("refused", "这个电源设置需要 Windows 宿主。"));
        using var process = Process.GetCurrentProcess();
        return Execute(request, allowChange, token, Environment.UserInteractive && process.SessionId != 0, ReadScheme,
            ReadSeconds, WindowsHostCommand.SetPowerTimeout);
    }

    internal static async Task<RobotOutcome> Execute(HostPowerRequest request, bool allowChange, CancellationToken token,
        bool interactive, Func<Guid> readScheme, Func<Guid, HostPowerSource, HostPowerTimer, uint?> readSeconds,
        Func<Guid, bool, bool, uint, bool, CancellationToken, Task<ZoneChangeResult>> set)
    {
        token.ThrowIfCancellationRequested();
        if (!request.IsWrite)
        {
            Guid scheme = readScheme();
            var lines = new List<string>();
            foreach (var source in Enum.GetValues<HostPowerSource>().Where(x => request.Source is null || x == request.Source))
                foreach (var timer in Enum.GetValues<HostPowerTimer>().Where(x => request.Timer is null || x == request.Timer))
                    if (readSeconds(scheme, source, timer) is { } seconds)
                        lines.Add(Describe(source, timer, seconds));
            if (readScheme() != scheme) return new("failed", "读取时电源计划已切换，请再查一次当前设置。");
            return lines.Count == 0 ? new("failed", "没有读到当前电源计划里的这项超时设置。") :
                new("completed", string.Join('\n', lines));
        }
        if (!allowChange) return new("refused", "当前授权只允许查看这台电脑的电源设置。");
        if (request.Timer is not { } target || request.Minutes is not { } minutes || minutes > uint.MaxValue / 60)
            return new("refused", "请指定要调整的屏幕或睡眠超时，以及分钟数。");
        if (request.Source is not { } supply)
            return new("needs-input", "要调整插电时还是使用电池时的设置？直接回复「插电时」或「使用电池时」即可。");
        Guid original = readScheme();
        uint? before = readSeconds(original, supply, target);
        if (!before.HasValue) return new("refused", "没有读到这项电源设置，请检查当前电源计划。");
        uint desired = minutes * 60;
        if (before == desired && readScheme() == original) return new("completed", "设置已经符合要求。" + Describe(supply, target, desired));
        if (readScheme() != original) return new("refused", "电源计划已经切换，请重新确认要修改的设置。");
        var result = await Set(elevated: false);
        if (result == ZoneChangeResult.PermissionDenied)
        {
            if (!interactive) return new("refused", "Windows 要求管理员权限，当前宿主没有可确认授权的桌面。");
            token.ThrowIfCancellationRequested();
            if (readScheme() != original || readSeconds(original, supply, target) != before)
                return new("refused", "等待授权期间电源计划或超时已改变，请重新确认。");
            result = await Set(elevated: true);
        }
        return result switch
        {
            ZoneChangeResult.Applied when readScheme() == original && readSeconds(original, supply, target) == desired =>
                new("completed", "已修改并核对。" + Describe(supply, target, desired)),
            ZoneChangeResult.Cancelled => new("refused", "已取消这次 Windows 管理员授权。"),
            ZoneChangeResult.PermissionDenied => new("refused", "Windows 拒绝了这次电源设置修改。"),
            ZoneChangeResult.Failed => new("failed", "Windows 未能修改这项电源设置。"),
            ZoneChangeResult.Busy => new("refused", "还有一项系统设置操作尚未结束，请先处理该操作。"),
            ZoneChangeResult.Unverified => new("needs-reconciliation", "新的超时值已经写入电源计划，但未能确认当前计划已采用它。请核对当前的电源设置。"),
            _ => new("needs-reconciliation", "未能核实修改结果。可以再问我「这台电脑的电源设置是什么？」。")
        };

        Task<ZoneChangeResult> Set(bool elevated) => set(original, supply == HostPowerSource.Battery,
            target == HostPowerTimer.Sleep, minutes, elevated, token);
    }

    private static string TimerName(HostPowerTimer timer) => timer == HostPowerTimer.Display ? "关闭显示器" : "睡眠";

    private static string Describe(HostPowerSource source, HostPowerTimer timer, uint seconds)
    {
        string supply = source == HostPowerSource.Battery ? "使用电池时" : "插电时";
        if (seconds == 0) return $"{supply}不会因闲置自动{TimerName(timer)}。";
        string duration = seconds % 60 == 0 ? $"{seconds / 60} 分钟" : $"{seconds} 秒";
        return $"{supply}闲置 {duration}后{TimerName(timer)}。";
    }

    [SupportedOSPlatform("windows")]
    internal static Guid ReadScheme()
    {
        uint error = PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer);
        if (error != 0) throw new Win32Exception((int)error);
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    [SupportedOSPlatform("windows")]
    private static uint? ReadSeconds(Guid scheme, HostPowerSource source, HostPowerTimer timer)
    {
        var (group, setting) = SettingIds(timer == HostPowerTimer.Sleep);
        uint error = source == HostPowerSource.Battery
            ? PowerReadDCValueIndex(IntPtr.Zero, in scheme, in group, in setting, out uint seconds)
            : PowerReadACValueIndex(IntPtr.Zero, in scheme, in group, in setting, out seconds);
        return error == 0 ? seconds : null;
    }

    internal static (Guid Group, Guid Setting) SettingIds(bool sleep) => sleep ?
        (SleepGroup, SleepTimeout) : (DisplayGroup, DisplayTimeout);

    private static readonly Guid DisplayGroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid DisplayTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SleepGroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid SleepTimeout = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadACValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, out uint seconds);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadDCValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, out uint seconds);
    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
