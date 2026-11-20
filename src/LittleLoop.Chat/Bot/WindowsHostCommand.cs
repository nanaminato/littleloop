using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

static class WindowsHostCommand
{
    [SupportedOSPlatform("windows")]
    public static async Task<ZoneChangeResult> SetPowerTimeout(Guid scheme, bool battery, bool sleep, uint minutes,
        bool elevated, CancellationToken token)
    {
        if (scheme == Guid.Empty || minutes > uint.MaxValue / 60) return ZoneChangeResult.Failed;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var host = Process.GetCurrentProcess();
        bool interactive = Environment.UserInteractive && host.SessionId != 0;
        var operationToken = deadline.Token;
        return await HostTimeZone.Changes.Run(() => ApplyPowerTimeout(scheme, elevated, interactive, operationToken,
            HostPower.ReadScheme, admin => WriteValue(admin), admin => RunPowerCfg(["/setactive", "SCHEME_CURRENT"], admin)), operationToken);

        Task<ZoneChangeResult> WriteValue(bool admin)
        {
            var (group, setting) = HostPower.SettingIds(sleep);
            uint seconds = checked(minutes * 60);
            if (admin) return RunPowerCfg([battery ? "/setdcvalueindex" : "/setacvalueindex", scheme.ToString("D"),
                group.ToString("D"), setting.ToString("D"), seconds.ToString(CultureInfo.InvariantCulture)], true);
            uint error = battery ? PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in group, in setting, seconds) :
                PowerWriteACValueIndex(IntPtr.Zero, in scheme, in group, in setting, seconds);
            return Task.FromResult(FromWindowsError(error));
        }
    }

    // Once a value is stored, a failure to apply it is a partial result. Never repeat that write.
    internal static async Task<ZoneChangeResult> ApplyPowerTimeout(Guid scheme, bool elevated, bool interactive, CancellationToken token,
        Func<Guid> readScheme, Func<bool, Task<ZoneChangeResult>> writeValue, Func<bool, Task<ZoneChangeResult>> activateCurrent)
    {
        if (token.IsCancellationRequested || readScheme() != scheme || elevated && !interactive) return ZoneChangeResult.Failed;
        var written = await writeValue(elevated);
        if (written != ZoneChangeResult.Applied) return written;
        try
        {
            if (token.IsCancellationRequested || readScheme() != scheme) return ZoneChangeResult.Unverified;
            // Reapply the plan active when Windows handles this invocation. Passing the old
            // plan's GUID here could switch the user back after an unrelated external change.
            var activated = await activateCurrent(false);
            if (activated == ZoneChangeResult.PermissionDenied && interactive && !token.IsCancellationRequested && readScheme() == scheme)
                activated = await activateCurrent(true);
            if (activated == ZoneChangeResult.Unknown) return ZoneChangeResult.Unknown;
            return activated == ZoneChangeResult.Applied && readScheme() == scheme ? ZoneChangeResult.Applied : ZoneChangeResult.Unverified;
        }
        catch (Win32Exception) { return ZoneChangeResult.Unverified; }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<ZoneChangeResult> RunPowerCfg(string[] arguments, bool elevated)
    {
        string directory = Environment.SystemDirectory;
        if (!Path.IsPathFullyQualified(directory)) return ZoneChangeResult.Failed;
        var start = new ProcessStartInfo(Path.Combine(directory, "powercfg.exe"))
        {
            WorkingDirectory = directory, UseShellExecute = elevated, CreateNoWindow = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (elevated) start.Verb = "runas";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception ex) { return FromWindowsError(unchecked((uint)ex.NativeErrorCode)); }
        if (process is null) return ZoneChangeResult.Unknown;
        using (process)
        {
            try
            {
                await process.WaitForExitAsync();
                return FromWindowsError(unchecked((uint)process.ExitCode));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return ZoneChangeResult.Unknown; }
        }
    }

    private static ZoneChangeResult FromWindowsError(uint code) => code switch
    {
        0 => ZoneChangeResult.Applied,
        5 or 740 or 1314 => ZoneChangeResult.PermissionDenied,
        1223 => ZoneChangeResult.Cancelled,
        _ => ZoneChangeResult.Failed
    };
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerWriteACValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, uint seconds);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, uint seconds);
}
