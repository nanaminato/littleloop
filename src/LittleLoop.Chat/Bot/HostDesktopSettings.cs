using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

enum DesktopPreference { HiddenFiles, FileExtensions, DarkApps }

sealed record HostDesktopRequest(DesktopPreference Setting, bool? Value) : IHostSettingRequest
{
    public bool IsWrite => Value.HasValue;
    public Task<RobotOutcome> Execute(bool allowChange, CancellationToken token) =>
        HostDesktopSettings.Execute(this, allowChange, token);
}

static class HostDesktopSettings
{
    private const string Host = @"(?:这台电脑|本机|Windows) *(?:上|中|里)?(?:的)? *";
    private const string Explorer = @"(?:(?:这台电脑|本机|Windows) *(?:的)? *)?(?:文件)?资源管理器(?:中|里)?(?:的)? *";
    private const string Scope = "(?:" + Explorer + "|" + Host + ")";
    private const string FileOption = @"(?<file>隐藏文件(?:和文件夹)?|文件(?:名)?扩展名|文件后缀(?:名)?)";
    private const string Show = @"(?<show>显示|隐藏)";
    private const string Theme = Host + @"(?:默认)?应用(?:主题|颜色模式|模式)";
    private const string SetTo = @"(?:切换|修改|设置|设|改|调)(?:成|为|到)";
    private static Regex Pattern(string body, bool question = false) => new(
        @"\A(?:请(?:帮我)?|帮我|麻烦(?:你)?)? *(?:" + body + (question ? @")[？?。]?\z" : @")[。！!]?\z"),
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private static readonly Regex ReadFile = Pattern(@"(?:(?:查(?:看|询)?|看)(?:一下|看)? *)?(?:当前|现在)? *" + Scope +
        @"(?:现在|当前)?(?:是否显示|有没有显示|显示)" + FileOption + @"(?:吗)?", true);
    private static readonly Regex SetFile = Pattern(@"(?:在)?" + Scope + Show + FileOption + "|" +
        Show + " *" + Scope + FileOption + @"|(?:把|将) *" + Scope + FileOption + SetTo + Show);
    private static readonly Regex ReadTheme = Pattern(@"(?:(?:查(?:看|询)?|看)(?:一下|看)? *)?(?:当前|现在)? *" + Theme +
        @"(?:是什么|是什么颜色|是深色还是浅色)", true);
    private static readonly Regex SetTheme = Pattern(@"(?:把|将)? *" + Theme + SetTo + @"(?<mode>深色|暗色|浅色|亮色)(?:模式|主题)?");

    public static HostDesktopRequest? Parse(string text)
    {
        text = text.Trim();
        if (text.Length > 140) return null;
        var file = SetFile.Match(text);
        if (file.Success) return new(FileSetting(file), file.Groups["show"].Value == "显示");
        file = ReadFile.Match(text);
        if (file.Success) return new(FileSetting(file), null);
        var theme = SetTheme.Match(text);
        if (theme.Success) return new(DesktopPreference.DarkApps, theme.Groups["mode"].Value is "暗色" or "深色");
        return ReadTheme.IsMatch(text) ? new(DesktopPreference.DarkApps, null) : null;

        static DesktopPreference FileSetting(Match match) => match.Groups["file"].Value.StartsWith("隐藏", StringComparison.Ordinal)
            ? DesktopPreference.HiddenFiles : DesktopPreference.FileExtensions;
    }

    public static Task<RobotOutcome> Execute(HostDesktopRequest request, bool allowChange, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new RobotOutcome("refused", "这些桌面设置需要 Windows 宿主。"));
        return ExecuteWindows(request, allowChange, token);
    }

    [SupportedOSPlatform("windows")]
    private static Task<RobotOutcome> ExecuteWindows(HostDesktopRequest request, bool allowChange, CancellationToken token) =>
        Task.Run(() => Apply(request, allowChange, DesktopAccount(), Read, Write, token), token);

    internal static RobotOutcome Apply(HostDesktopRequest request, bool allowChange, string? account,
        Func<DesktopPreference, bool?> read, Action<DesktopPreference, bool> write, CancellationToken token)
    {
        if (account is null) return new("refused", "桌面偏好属于登录的 Windows 账号。请从该账号的交互桌面运行 LittleLoop。");
        if (request.IsWrite && !allowChange) return new("refused", "当前授权只允许查看 Windows 桌面设置。");
        bool mutationStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var value = read(request.Setting);
            string label = request.Setting switch
            {
                DesktopPreference.HiddenFiles => "文件资源管理器的隐藏文件",
                DesktopPreference.FileExtensions => "文件资源管理器的文件扩展名",
                DesktopPreference.DarkApps => "应用主题偏好",
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            string display(bool? mode) => mode is null ? "使用默认设置" : request.Setting == DesktopPreference.DarkApps
                ? mode.Value ? "深色" : "浅色" : mode.Value ? "显示" : "隐藏";
            if (request.Value is not { } target || target == value)
                return new("completed", $"Windows 账号 {account} 的{label}当前为{display(value)}。");
            token.ThrowIfCancellationRequested();
            mutationStarted = true;
            write(request.Setting, target);
            if (read(request.Setting) != target) return new("needs-reconciliation", $"未能核实 Windows 账号 {account} 的{label}变更。");
            string refresh = request.Setting == DesktopPreference.DarkApps
                ? "遵循 Windows 主题的应用可在重新打开后采用该设置。" : "已打开的文件夹可按 F5 刷新。";
            return new("completed", $"已将 Windows 账号 {account} 的{label}设为{display(target)}，并已读回核对。{refresh}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return mutationStarted ? new("needs-reconciliation", $"变更后的桌面设置未能核实，请检查 Windows 账号 {account} 的设置。")
                : new("failed", $"无法读取 Windows 账号 {account} 的桌面设置。");
        }
    }

    [SupportedOSPlatform("windows")]
    internal static string? DesktopAccount()
    {
        using var host = Process.GetCurrentProcess();
        if (!Environment.UserInteractive || host.SessionId == 0 || GetShellWindow() == 0 || !IsActiveSession(host.SessionId)) return null;
        using var impersonation = WindowsIdentity.GetCurrent(ifImpersonating: true);
        if (impersonation is not null) return null;
        using var identity = WindowsIdentity.GetCurrent();
        string? domain = SessionName(host.SessionId, 7); // WTSDomainName
        string? user = SessionName(host.SessionId, 5);   // WTSUserName
        return SessionOwner(identity.Name, domain, user);
    }

    internal static string? SessionOwner(string processAccount, string? domain, string? user) =>
        !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(user) &&
        string.Equals(processAccount, domain + "\\" + user, StringComparison.OrdinalIgnoreCase) ? processAccount : null;

    private static bool IsActiveSession(int sessionId)
    {
        if (!WTSQuerySessionInformationW(0, sessionId, 8, out var value, out uint bytes)) return false; // WTSConnectState
        try { return bytes == sizeof(int) && value != 0 && Marshal.ReadInt32(value) == 0; } // WTSActive
        finally { WTSFreeMemory(value); }
    }

    private static string? SessionName(int sessionId, int field)
    {
        if (!WTSQuerySessionInformationW(0, sessionId, field, out var value, out _)) return null;
        try { return Marshal.PtrToStringUni(value); }
        finally { WTSFreeMemory(value); }
    }

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";
    [SupportedOSPlatform("windows")]
    private static bool? Read(DesktopPreference setting)
    {
        if (setting == DesktopPreference.DarkApps)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue(AppsUseLightTheme) switch
            {
                null => null, 0 => true, 1 => false, _ => throw new IOException("Invalid Windows app color preference.")
            };
        }
        uint mask = ShellMask(setting);
        // The shell API has no error return. Opposite input bits must produce
        // the same result before we can report either state as verified.
        var off = new ShellState();
        var on = new ShellState { Flags = mask };
        SHGetSetSettings(ref off, mask, false);
        SHGetSetSettings(ref on, mask, false);
        if ((off.Flags & mask) != (on.Flags & mask)) throw new IOException("Could not verify the Explorer setting.");
        return (off.Flags & mask) != 0;
    }

    [SupportedOSPlatform("windows")]
    private static void Write(DesktopPreference setting, bool value)
    {
        if (setting == DesktopPreference.DarkApps)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Personalize);
            key.SetValue(AppsUseLightTheme, value ? 0 : 1, RegistryValueKind.DWord);
            // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG. The persisted
            // preference is verifiable even when an application does not redraw.
            SendMessageTimeoutW(0xffff, 0x001a, 0, "ImmersiveColorSet", 2, 100, out _);
            return;
        }
        uint mask = ShellMask(setting);
        var state = new ShellState { Flags = value ? mask : 0 };
        SHGetSetSettings(ref state, mask, true);
    }

    private static uint ShellMask(DesktopPreference setting) => setting switch
    {
        DesktopPreference.HiddenFiles => 1,     // SSF_SHOWALLOBJECTS
        DesktopPreference.FileExtensions => 2,  // SSF_SHOWEXTENSIONS
        _ => throw new ArgumentOutOfRangeException(nameof(setting))
    };

    // SHELLSTATEW contains eight 32-bit words. Only the first two bitfields in
    // its first word are used, and the mask selects exactly one per call.
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ShellState { public uint Flags; }
    [DllImport("shell32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHGetSetSettings(ref ShellState state, uint mask, bool set);
    [DllImport("user32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetShellWindow();
    [DllImport("wtsapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool WTSQuerySessionInformationW(nint server, int session, int field, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(nint buffer);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, string lParam, uint flags,
        uint timeout, out nuint result);
}
