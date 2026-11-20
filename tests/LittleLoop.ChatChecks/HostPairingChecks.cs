static class HostPairingChecks
{
    public static async Task Run()
    {
        string root = Directory.CreateDirectory(Path.Combine("data", "host-pairing-checks", Guid.NewGuid().ToString("N"))).FullName;
        var pairing = new HostPairing(root);
        string first = Guid.NewGuid().ToString(), other = Guid.NewGuid().ToString(), name = "host_candidate";
        string? desktop = "MACHINE\\tester";
        bool eligible = true;
        int prompts = 0, checks = 0;
        void Check(bool pass, string label)
        {
            if (!pass) throw new Exception(label);
            checks++;
        }
        Task<RobotOutcome> Attempt(int response, Action? during = null, string? id = null, string? username = null) =>
            pairing.Confirm(id ?? first, username ?? name, CancellationToken.None, () => desktop, prompt =>
            {
                prompts++;
                Check(prompt.Contains(name) && prompt.Contains("服务重启后") && prompt.Contains("时区") &&
                    prompt.Contains("隐藏文件") && prompt.Contains("管理员权限"), "local confirmation identifies candidate and enduring scoped rights");
                during?.Invoke();
                return Task.FromResult(response);
            }, () => Task.FromResult(eligible));

        Check(!pairing.IsBound(first) && (await Attempt(6, id: "bad")).Status == "refused" &&
            (await Attempt(6, username: "display\nname")).Status == "refused" && prompts == 0, "only valid account identities can prompt");
        eligible = false;
        Check((await Attempt(6)).Status == "refused" && prompts == 0, "ineligible account never prompts");
        eligible = true; desktop = null;
        Check((await Attempt(6)).Status == "refused" && prompts == 0, "headless service cannot grant pairing");
        desktop = "MACHINE\\tester";
        foreach (var response in new[] { 7, 32000, 32001, 0 })
            Check((await Attempt(response)).Status == "refused" && !pairing.IsBound(first), "only an explicit yes can create a binding: " + response);
        Check((await Attempt(6, () => desktop = null)).Status == "refused" && !pairing.IsBound(first), "departed desktop cannot grant binding");
        desktop = "MACHINE\\tester";
        Check((await Attempt(6, () => eligible = false)).Status == "refused" && !pairing.IsBound(first),
            "losing eligibility while local consent is pending prevents persistence");
        eligible = true;
        var clicked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = pairing.Confirm(first, name, cancellation.Token, () => desktop, _ => clicked.Task, null);
            int before = prompts;
            Check((await Attempt(6)).Status == "refused" && prompts == before, "concurrent request does not open a second modal");
            cancellation.Cancel();
            clicked.SetResult(6);
            try { await pending; throw new Exception("Cancelled pairing persisted"); }
            catch (OperationCanceledException) { }
            Check(!pairing.IsBound(first), "late desktop yes after shutdown does not bind");
        }
        Check((await Attempt(6)).Status == "completed" && new HostPairing(root).IsBound(first), "binding survives restart by account ID");
        int shown = prompts;
        Check((await Attempt(6)).Status == "completed" && (await Attempt(6, id: other)).Status == "refused" && prompts == shown &&
            !pairing.IsBound(other), "bound account is idempotent and another principal cannot replace it");

        string corrupt = Directory.CreateDirectory(Path.Combine(root, "malformed")).FullName;
        File.WriteAllText(Path.Combine(corrupt, "host-pairing.id"), "invalid");
        var damaged = new HostPairing(corrupt);
        var refused = await damaged.Confirm(first, name, CancellationToken.None, () => desktop, _ => throw new Exception("Unexpected prompt"), null);
        Check(refused.Status == "failed" && !damaged.IsBound(first) && File.ReadAllText(Path.Combine(corrupt, "host-pairing.id")) == "invalid",
            "corrupt server-owned record stays closed and is not silently overwritten");
        Console.WriteLine($"Local desktop host pairing checks passed: {checks} (simulated confirmations).");
    }
}
