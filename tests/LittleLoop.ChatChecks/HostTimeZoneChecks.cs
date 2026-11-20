static class HostTimeZoneChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool pass, string name)
        {
            if (!pass) throw new Exception(name);
            checks++;
        }
        Check(HostTimeZone.Parse("这台电脑的时区是什么？") == new HostTimeZoneRequest(null), "current host lookup");
        Check(HostTimeZone.Parse("请把这台电脑的时区改成北京时间。") == new HostTimeZoneRequest("北京时间"), "plain host change");
        Check(HostTimeZone.Parse("以管理员权限把本机时区改成 UTC") == new HostTimeZoneRequest("UTC"), "explicit administrator phrasing selects the same operation");
        Check(HostTimeZone.Parse("帮我把本机时区设为 Pacific Standard Time")?.Zone == "Pacific Standard Time",
            "installed zone name with spaces");
        foreach (string text in new[] { "如果把本机时区改成 UTC 会怎样？", "把本机时区改成 UTC？", "“把本机时区改成 UTC”",
            "把本机时区改成 UTC，然后重启", "把本机时区改成 UTC and reboot", "把时区设为 UTC", "把对方电脑的时区设为 UTC" })
            Check(HostTimeZone.Parse(text) is null, "no host action for " + text);

        const string china = "China Standard Time";
        string current = china;
        var attempts = new List<(string Zone, bool Elevated)>();
        async Task<RobotOutcome> Run(HostTimeZoneRequest request, bool allow = true, bool desktop = true,
            ZoneChangeResult normal = ZoneChangeResult.Applied, ZoneChangeResult elevated = ZoneChangeResult.Applied,
            bool verified = true)
        {
            attempts.Clear();
            return await HostTimeZone.Execute(request, allow, CancellationToken.None, desktop, [china, "UTC"], () => current,
                (id, asAdministrator, _) =>
                {
                    attempts.Add((id, asAdministrator));
                    var result = asAdministrator ? elevated : normal;
                    if (verified && result == ZoneChangeResult.Applied) current = id;
                    return Task.FromResult(result);
                });
        }
        var read = await Run(new(null), allow: false);
        Check(read.Status == "completed" && read.Report!.Contains(china) && attempts.Count == 0, "read works without write grant");
        Check((await Run(new("UTC"), allow: false)).Status == "refused" && attempts.Count == 0, "host grant enforced before mutation");
        Check((await Run(new("Invalid/Zone"))).Status == "refused" && attempts.Count == 0, "unknown zone never dispatched");
        Check((await Run(new("UTC /?"))).Status == "refused" && attempts.Count == 0, "only installed zone IDs ever dispatched");
        Check((await Run(new("Asia/Shanghai"))).Status == "completed" && attempts.Count == 0, "IANA alias and existing value");
        Check((await Run(new("utc"))).Status == "completed" && attempts.SequenceEqual([("UTC", false)]), "write uses installed spelling and normal token first");
        Check((await Run(new("北京时间"), desktop: false, normal: ZoneChangeResult.PermissionDenied)).Status == "refused" &&
            attempts.SequenceEqual([(china, false)]), "headless host cannot prompt");
        Check((await Run(new("北京时间"), normal: ZoneChangeResult.Failed)).Status == "failed" && attempts.Count == 1,
            "failure without permission evidence cannot request elevation");
        Check((await Run(new("北京时间"), normal: ZoneChangeResult.Unknown)).Status == "needs-reconciliation" && attempts.Count == 1,
            "unknown or timed-out execution never retried");
        Check((await Run(new("北京时间"), normal: ZoneChangeResult.PermissionDenied, elevated: ZoneChangeResult.Cancelled)).Status == "refused" &&
            attempts.SequenceEqual([(china, false), (china, true)]), "cancelled desktop consent is final");
        Check((await Run(new("北京时间"), normal: ZoneChangeResult.PermissionDenied)).Status == "completed" &&
            attempts.SequenceEqual([(china, false), (china, true)]) && current == china, "successful consent is applied and independently read back");
        Check((await Run(new("UTC"), verified: false)).Status == "needs-reconciliation" && attempts.Count == 1, "exit zero needs matching readback");
        var gate = new ZoneChangeGate();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<ZoneChangeResult>();
        using var stop = new CancellationTokenSource();
        var first = gate.Run(() => { opened.SetResult(); return closed.Task; }, stop.Token);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        Check(await first == ZoneChangeResult.Unknown, "cancelling the caller does not block on desktop consent");
        Check(await gate.Run(() => throw new Exception("overlapping mutation"), CancellationToken.None) == ZoneChangeResult.Busy,
            "pending consent still prevents a second mutation after caller cancellation");
        closed.SetResult(ZoneChangeResult.Cancelled);
        using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ZoneChangeResult next;
        do
        {
            await Task.Delay(1, gateTimeout.Token);
            next = await gate.Run(() => Task.FromResult(ZoneChangeResult.Applied), gateTimeout.Token);
        } while (next == ZoneChangeResult.Busy);
        Check(next == ZoneChangeResult.Applied, "a new operation can begin after Windows returns");
        Check(await gate.Run(() => Task.FromResult(ZoneChangeResult.Unknown), CancellationToken.None) == ZoneChangeResult.Unknown &&
            await gate.Run(() => throw new Exception("unverified exit"), CancellationToken.None) == ZoneChangeResult.Busy,
            "missing process receipt cannot be followed by another mutation");
        Console.WriteLine($"Windows host time-zone checks passed: {checks} (simulated writes).");
    }
}
