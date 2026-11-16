namespace LittleLoop.Core;

/// <summary>Immutable snapshot reads and bounded Windows rename contention handling.</summary>
public static class AtomicSnapshot
{
    public static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void Replace(string temporary, string destination)
    {
        // Virus scanners and other readers can momentarily deny replacement on Windows.
        // Retry only the already-flushed snapshot rename, never any external action.
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, destination, true); return; }
            catch (Exception e) when (OperatingSystem.IsWindows() && attempt < 10 &&
                e is IOException or UnauthorizedAccessException && (e.HResult & 0xffff) is 5 or 32 or 33)
            { Thread.Sleep(30); }
        }
    }
}
