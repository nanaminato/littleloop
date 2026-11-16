using LittleLoop.Core;

internal static class AtomicSnapshotChecks
{
    public static async Task Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "littleloop-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "state.json"), next = target + ".tmp";
        File.WriteAllText(target, "old"); File.WriteAllText(next, "new");
        Task replace;
        using (var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            replace = Task.Run(() => AtomicSnapshot.Replace(next, target));
            await Task.Delay(90);
            if (replace.IsCompleted || AtomicSnapshot.ReadText(target) != "old")
                throw new Exception("Contended replacement must retain old snapshot until reader releases.");
        }
        await replace;
        if (AtomicSnapshot.ReadText(target) != "new" || File.Exists(next)) throw new Exception("Snapshot replacement did not finish.");
        Console.WriteLine("PASS Windows transient reader contention preserves old snapshot and retries only atomic rename");
        File.WriteAllText(next, "later");
        using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool failed = false;
        try { AtomicSnapshot.Replace(next, target); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
        if (!failed || AtomicSnapshot.ReadText(target) != "new") throw new Exception("Persistent contention must fail without changing target.");
        Console.WriteLine("PASS persistent snapshot contention fails within bounded retries without false commit");
    }
}
