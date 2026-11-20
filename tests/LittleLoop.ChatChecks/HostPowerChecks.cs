static class HostPowerChecks
{
    public static async Task Run()
    {
        int checks = 0;
        void Check(bool pass, string label)
        {
            if (!pass) throw new Exception(label);
            checks++;
        }
        var acDisplay = new HostPowerRequest(HostPowerSource.PluggedIn, HostPowerTimer.Display, 15);
        var dcSleep = new HostPowerRequest(HostPowerSource.Battery, HostPowerTimer.Sleep, 30);
        Check(HostPower.Parse("请查看这台电脑的电源设置。") == new HostPowerRequest(null, null, null), "read active power policy");
        Check(HostPower.Parse("本机使用电池时多久会睡眠？") == dcSleep with { Minutes = null }, "read specific power source");
        Check(HostPower.Parse("帮我把这台电脑插电时的屏幕关闭时间设为 15 分钟。") == acDisplay, "set display timeout on AC");
        Check(HostPower.Parse("本机使用电池时半小时后进入睡眠") == dcSleep, "set sleep timeout on battery");
        Check(HostPower.Parse("请将本机接通电源时的睡眠时间改为2小时") ==
            new HostPowerRequest(HostPowerSource.PluggedIn, HostPowerTimer.Sleep, 120), "hour conversion");
        Check(HostPower.Parse("本机用电池时永不关闭显示器") == new HostPowerRequest(HostPowerSource.Battery, HostPowerTimer.Display, 0),
            "never turns off only the requested timer");
        foreach (string text in new[] { "如果把本机插电时的睡眠时间改为2分钟会怎样？", "把本机插电时的睡眠时间改为2分钟？",
            "“本机插电时永不睡眠”", "本机电池供电时10分钟后进入休眠", "把本机插电时的睡眠时间设为99999999小时",
            "把本机插电时的睡眠时间设为-1分钟", "本机使用电池时半小时后睡眠；并运行命令", "把他的电脑睡眠时间设为1分钟" })
            Check(HostPower.Parse(text) is null, "no mutation inferred: " + text);

        Guid plan = Guid.NewGuid(), currentPlan = plan;
        var values = new Dictionary<(HostPowerSource, HostPowerTimer), uint?>
        {
            [(HostPowerSource.PluggedIn, HostPowerTimer.Display)] = 615,
            [(HostPowerSource.PluggedIn, HostPowerTimer.Sleep)] = 0,
            [(HostPowerSource.Battery, HostPowerTimer.Display)] = null,
            [(HostPowerSource.Battery, HostPowerTimer.Sleep)] = 300
        };
        var attempts = new List<(Guid Plan, bool Battery, bool Sleep, uint Minutes, bool Elevated)>();
        Task<RobotOutcome> Run(HostPowerRequest request, bool allow = true, bool desktop = true,
            ZoneChangeResult normal = ZoneChangeResult.Applied, ZoneChangeResult elevated = ZoneChangeResult.Applied,
            bool apply = true, bool changePlan = false)
        {
            attempts.Clear();
            currentPlan = plan;
            return HostPower.Execute(request, allow, CancellationToken.None, desktop, () => currentPlan,
                (_, source, timer) => values[(source, timer)], (id, battery, sleep, minutes, admin, _) =>
                {
                    attempts.Add((id, battery, sleep, minutes, admin));
                    if (changePlan) currentPlan = Guid.NewGuid();
                    var outcome = admin ? elevated : normal;
                    if (outcome == ZoneChangeResult.Applied && apply)
                        values[(battery ? HostPowerSource.Battery : HostPowerSource.PluggedIn,
                            sleep ? HostPowerTimer.Sleep : HostPowerTimer.Display)] = minutes * 60;
                    return Task.FromResult(outcome);
                });
        }
        var overview = await Run(new(null, null, null), allow: false);
        Check(overview.Status == "completed" && overview.Report!.Contains("615 秒") && overview.Report.Contains("5 分钟") &&
            overview.Report.Contains("不会因闲置自动睡眠") && attempts.Count == 0, "read independent AC and DC values, including non-minute and never");
        Check((await Run(acDisplay, allow: false)).Status == "refused" && attempts.Count == 0, "read-only principal cannot change power settings");
        Check((await Run(acDisplay with { Source = null })).Report!.Contains("插电时还是使用电池时") && attempts.Count == 0,
            "an unspecified source asks for clarification before a write");
        Check((await Run(acDisplay with { Source = HostPowerSource.Battery })).Status == "refused" && attempts.Count == 0,
            "unsupported timer not written");
        Check((await Run(acDisplay)).Status == "completed" && attempts.SequenceEqual([(plan, false, false, 15u, false)]),
            "typed ordinary AC display change and independent readback");
        Check((await Run(acDisplay)).Status == "completed" && attempts.Count == 0, "unchanged timeout needs no mutation");
        Check((await Run(dcSleep, desktop: false, normal: ZoneChangeResult.PermissionDenied)).Status == "refused" && attempts.Count == 1,
            "no desktop for power UAC");
        Check((await Run(dcSleep, normal: ZoneChangeResult.Failed)).Status == "failed" && attempts.Count == 1,
            "unrelated power error never requests elevation");
        Check((await Run(dcSleep, normal: ZoneChangeResult.Unknown)).Status == "needs-reconciliation" && attempts.Count == 1,
            "uncertain settings change never retries");
        Check((await Run(dcSleep, normal: ZoneChangeResult.PermissionDenied, elevated: ZoneChangeResult.Cancelled)).Status == "refused" &&
            attempts.SequenceEqual([(plan, true, true, 30u, false), (plan, true, true, 30u, true)]), "cancelled scoped consent is final");
        Check((await Run(dcSleep, normal: ZoneChangeResult.PermissionDenied, changePlan: true)).Status == "refused" &&
            attempts.Count == 1, "plan change while waiting does not dispatch elevated work");
        Check((await Run(dcSleep, apply: false)).Status == "needs-reconciliation", "exit success without readback cannot confirm power change");
        Check((await Run(dcSleep, normal: ZoneChangeResult.PermissionDenied)).Status == "completed" && attempts.Last().Elevated,
            "scoped elevation only after explicit denial and actual setting readback");
        Check(values[(HostPowerSource.PluggedIn, HostPowerTimer.Display)] == 900 &&
            values[(HostPowerSource.PluggedIn, HostPowerTimer.Sleep)] == 0 && values[(HostPowerSource.Battery, HostPowerTimer.Display)] is null,
            "other timers and source preserved");
        var stages = new List<string>();
        async Task<ZoneChangeResult> Apply(ZoneChangeResult written, ZoneChangeResult activation,
            bool switchedAfterWrite = false, bool headless = false)
        {
            stages.Clear();
            currentPlan = plan;
            return await WindowsHostCommand.ApplyPowerTimeout(plan, elevated: false, !headless, CancellationToken.None,
                () => currentPlan, elevated =>
                {
                    stages.Add("write " + elevated);
                    if (switchedAfterWrite) currentPlan = Guid.NewGuid();
                    return Task.FromResult(written);
                }, elevated =>
                {
                    stages.Add("apply " + elevated);
                    return Task.FromResult(elevated ? ZoneChangeResult.Applied : activation);
                });
        }
        Check(await Apply(ZoneChangeResult.PermissionDenied, ZoneChangeResult.Applied) == ZoneChangeResult.PermissionDenied &&
            stages.SequenceEqual(["write False"]), "denied initial write has no activation");
        Check(await Apply(ZoneChangeResult.Applied, ZoneChangeResult.PermissionDenied) == ZoneChangeResult.Applied &&
            stages.SequenceEqual(["write False", "apply False", "apply True"]), "permission retry applies only the pending activation");
        Check(await Apply(ZoneChangeResult.Applied, ZoneChangeResult.Failed) == ZoneChangeResult.Unverified &&
            stages.SequenceEqual(["write False", "apply False"]), "partial update reports unverified without replay");
        Check(await Apply(ZoneChangeResult.Applied, ZoneChangeResult.PermissionDenied, headless: true) == ZoneChangeResult.Unverified &&
            stages.Count == 2, "headless activation cannot obtain desktop consent");
        Check(await Apply(ZoneChangeResult.Applied, ZoneChangeResult.Applied, switchedAfterWrite: true) == ZoneChangeResult.Unverified &&
            stages.Count == 1, "external plan switch after write skips activation");
        Check(await Apply(ZoneChangeResult.Applied, ZoneChangeResult.Unknown) == ZoneChangeResult.Unknown &&
            stages.Count == 2, "unconfirmed process exit holds the shared gate");
        Console.WriteLine($"Windows host power settings checks passed: {checks} (simulated writes).");
    }
}
