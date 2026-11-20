static class HostDesktopSettingsChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
            checks++;
        }
        const DesktopPreference hidden = DesktopPreference.HiddenFiles, extensions = DesktopPreference.FileExtensions,
            dark = DesktopPreference.DarkApps;
        foreach (var (text, setting, value) in new (string, DesktopPreference, bool?)[]
        {
            ("请在文件资源管理器中显示隐藏文件", hidden, true), ("隐藏这台电脑的隐藏文件", hidden, false),
            ("文件资源管理器里是否显示隐藏文件？", hidden, null), ("在文件资源管理器中显示文件扩展名", extensions, true),
            ("把本机的文件名扩展名设置为隐藏。", extensions, false), ("这台电脑是否显示文件扩展名？", extensions, null),
            ("当前 Windows 应用主题是什么？", dark, null), ("把这台电脑的应用主题改成深色", dark, true),
            ("请将 Windows 默认应用模式切换为浅色。", dark, false)
        })
            Check(HostDesktopSettings.Parse(text) == new HostDesktopRequest(setting, value), text);
        foreach (string text in new[] { "怎么在文件资源管理器中显示隐藏文件？", "“把这台电脑的应用主题改成深色”",
            "在文件资源管理器中显示隐藏文件，然后删除日志", "把另一台电脑的应用主题改成浅色", "把本机系统主题改成深色",
            "在文件资源管理器中显示受保护的系统文件", "把本机应用主题改成深色，可以吗？", "隐藏 report.txt" })
            Check(HostDesktopSettings.Parse(text) is null, "no desktop mutation for " + text);
        const string account = @"HOST\alice";
        Check(HostDesktopSettings.SessionOwner(account, "host", "Alice") == account &&
            HostDesktopSettings.SessionOwner(@"NT AUTHORITY\SYSTEM", "HOST", "alice") is null &&
            HostDesktopSettings.SessionOwner(@"OTHER\alice", "HOST", "alice") is null &&
            HostDesktopSettings.SessionOwner(account, null, "alice") is null, "must match the signed-in session account and domain");
        var values = new Dictionary<DesktopPreference, bool?> { [hidden] = false, [extensions] = false, [dark] = null };
        var writes = new List<(DesktopPreference, bool)>();
        void Save(DesktopPreference setting, bool value) { values[setting] = value; writes.Add((setting, value)); }
        RobotOutcome Apply(HostDesktopRequest request, bool allow = true, string? owner = account, Action<DesktopPreference, bool>? write = null) =>
            HostDesktopSettings.Apply(request, allow, owner, setting => values[setting], write ?? Save, CancellationToken.None);
        Check(Apply(new(dark, null), false) is { Status: "completed", Report: { } read } &&
            read.Contains("默认设置") && read.Contains(account) && writes.Count == 0, "read reports the target account and absent preference");
        Check(Apply(new(hidden, true), owner: null).Status == "refused" && Apply(new(hidden, true), false).Status == "refused" &&
            writes.Count == 0, "background identity and read-only grant block writes");
        Check(Apply(new(hidden, true)).Status == "completed" && Apply(new(hidden, true)).Status == "completed" &&
            writes.SequenceEqual([(hidden, true)]) && values[extensions] == false, "shell flags are independent and writes idempotent");
        Check(Apply(new(extensions, true)).Status == "completed" && Apply(new(dark, true)).Status == "completed" &&
            Apply(new(dark, false)).Status == "completed" && values[dark] == false && values[extensions] == true,
            "file extension and both application theme settings round-trip");
        Check(Apply(new(hidden, false), write: (_, _) => { }).Status == "needs-reconciliation",
            "update that fails to take effect cannot report success");
        Check(Apply(new(hidden, false), write: (_, _) => throw new IOException("registry unavailable")).Status == "needs-reconciliation",
            "partial write has no automatic replay");
        if (OperatingSystem.IsWindows())
        {
            var result = await HostDesktopSettings.Execute(new(extensions, null), false, CancellationToken.None);
            Check(result.Status is "completed" or "refused", "real Windows shell lookup or desktop owner refusal");
            Console.WriteLine("Windows desktop preference read-only probe: " + result.Status);
        }
        Console.WriteLine($"Windows desktop preference checks passed: {checks} (simulated writes).");
    }
}
