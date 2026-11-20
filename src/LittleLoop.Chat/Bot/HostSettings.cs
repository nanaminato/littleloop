interface IHostSettingRequest
{
    bool IsWrite { get; }
    Task<RobotOutcome> Execute(bool allowChange, CancellationToken token);
}

static class HostSettings
{
    public static bool IsWork(string kind) => kind is "setting" or "timezone" or "pair";

    public static bool IsPairingMessage(string text) => text.Trim().TrimEnd('。', '！', '!') == "绑定这台电脑";

    public static IHostSettingRequest? Parse(string text)
    {
        if (text.Trim().TrimEnd('？', '?', '。') is "你能修改哪些系统设置" or "你能改哪些系统设置" or
            "你能帮我改哪些电脑设置" or "这台电脑能改哪些设置" or "你可以改哪些电脑设置" or "支持哪些系统设置")
            return new Capabilities();
        if (HostTimeZone.Parse(text) is { } zone) return zone;
        if (HostPower.Parse(text) is { } power) return power;
        return HostDesktopSettings.Parse(text);
    }

    internal static bool MayContinue(string text)
    {
        text = text.Trim().TrimEnd('。', '！', '!');
        return text.Length is > 0 and <= 24 && (text is "插电" or "插电时" or "使用电池" or "使用电池时" or
            "用电池" or "显示" or "隐藏" || text.StartsWith("改成", StringComparison.Ordinal) ||
            text.StartsWith("设为", StringComparison.Ordinal));
    }

    // Resolve a short answer solely against a host-generated exchange with the same user.
    internal static string? Continue(string earlierRequest, string status, string reply)
    {
        reply = reply.Trim().TrimEnd('。', '！', '!');
        if (!MayContinue(reply)) return null;
        var prior = Parse(earlierRequest);
        string? full = null;
        if (prior is HostPowerRequest { Timer: { } timer } power)
        {
            string label = timer == HostPowerTimer.Display ? "屏幕关闭时间" : "睡眠时间";
            if (status == "needs-input" && power is { Source: null, Minutes: { } minutes })
            {
                string? source = reply switch { "插电" or "插电时" => "插电时", "使用电池" or "使用电池时" or "用电池" => "使用电池时", _ => null };
                if (source is not null) full = $"把本机{source}的{label}设为{minutes}分钟";
            }
            else if (status == "completed" && power.Source is { } source && !power.IsWrite)
                full = $"把本机{(source == HostPowerSource.Battery ? "使用电池时" : "插电时")}的{label}{reply}";
        }
        else if (status == "completed" && prior is HostDesktopRequest { IsWrite: false } desktop)
            full = desktop.Setting switch
            {
                DesktopPreference.DarkApps => "把本机应用主题" + reply,
                DesktopPreference.FileExtensions => "在文件资源管理器中" + reply + "文件扩展名",
                DesktopPreference.HiddenFiles => "在文件资源管理器中" + reply + "隐藏文件",
                _ => null
            };
        else if (status == "completed" && prior is HostTimeZoneRequest { IsWrite: false })
            full = "把本机时区" + reply;
        return full is not null && Parse(full) is { IsWrite: true } ? full : null;
    }

    internal sealed record Capabilities : IHostSettingRequest
    {
        public bool IsWrite => false;
        public Task<RobotOutcome> Execute(bool allowChange, CancellationToken token) => Task.FromResult(new RobotOutcome("completed",
            "我能查看和调整这台 Windows 电脑的时区、插电或使用电池时的关屏和睡眠时间、资源管理器是否显示隐藏文件和文件扩展名，以及应用的深浅色主题。\n" +
            "可以直接说「这台电脑的电源设置是什么？」或「把本机插电时的屏幕关闭时间设为15分钟」。"));
    }
}
