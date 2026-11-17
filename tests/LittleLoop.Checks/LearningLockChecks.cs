using System.Reflection;
using LittleLoop.Core;

internal static class LearningLockChecks
{
    public static async Task Run()
    {
        string root = Path.GetFullPath(Path.Combine("data", "learning-lock-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        int baseline = GateCount();
        for (int i = 0; i < 128; i++)
        {
            var state = new LearningSession(Path.Combine(root, $"{i}.json"));
            state.Initialize(); state.Read();
        }
        if (GateCount() != baseline)
            throw new Exception($"Idle learning lock cache retained {GateCount()-baseline} completed session paths.");
        Console.WriteLine("PASS completed learning sessions do not retain per-path synchronization entries");

        string path = Path.Combine(root, "shared.json");
        new LearningSession(path).Initialize();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (int n = 0; n < 12; n++)
                new LearningSession(path).Begin(new($"{writer}-{n}", "Record task", new() { ["fixture"] = "concurrent" },
                    [new("done", "done", 1, true)], [], [], [], 1, []));
        })));
        if (new LearningSession(path).Read().Episodes.Length != 96 || GateCount() != baseline)
            throw new Exception("Concurrent session transitions lost data or retained idle locks.");
        Console.WriteLine("PASS concurrent independent session instances serialize all 96 updates and release locks");

        using (var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool failed = false;
            try { new LearningSession(path).Read(); } catch (IOException) { failed = true; }
            if (!failed || GateCount() != baseline) throw new Exception("Failed file lease leaked a synchronization entry.");
        }
        new LearningSession(path).Read();
        if (GateCount() != baseline) throw new Exception("Session did not recover after file lease failure.");
        Console.WriteLine("PASS file lease failure releases local synchronization and subsequent reads recover");
    }

    // Inspect retained roots directly: process working set alone can hide a small per-task leak.
    private static int GateCount()
    {
        var gates = typeof(LearningSession).GetField("LocalGates", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return (int)gates.GetType().GetProperty("Count")!.GetValue(gates)!;
    }
}
