using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LittleLoop.Tools;

public sealed record ProjectCommand(string Id, string Executable, string[] Arguments, bool IsTest, string[] ProtectedFiles)
{
    public string Description { get; init; } = "";
    public string[] RelatedPaths { get; init; } = [];
    public string TestEvidence { get; init; } = "exit-code";
}
public sealed record DevelopmentPolicy(string Root, string[] WritableFiles, ProjectCommand[] Commands,
    string[] ReadExtensions, int MaxFileBytes = 65536, int MaxFiles = 500, int TimeoutMilliseconds = 10000,
    int MaxOutputCharacters = 16384, int Budget = 24, int MaxModelCalls = 32, int MaxFailures = 5);

public sealed class ProjectFiles
{
    public DevelopmentPolicy Policy { get; }
    public string Root { get; }
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Excluded = ["bin", "obj", "node_modules", "__pycache__", "data", "logs", "packages", "dist", "coverage", "TestResults", "venv"];
    public ProjectFiles(DevelopmentPolicy policy, bool requireTests = true)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(policy.Root));
        Policy = policy with { Root = Root };
        if (!Directory.Exists(Root) || Root == Path.GetPathRoot(Root) || policy.WritableFiles is null || policy.Commands is null ||
            policy.ReadExtensions is null || policy.ReadExtensions.Length == 0 || policy.ReadExtensions.Any(e => !e.StartsWith('.') || e.Length > 16) ||
            policy.MaxFileBytes is < 1 or > 1048576 || policy.MaxFiles is < 1 or > 5000 || policy.TimeoutMilliseconds is < 1 or > 120000 ||
            policy.MaxOutputCharacters is < 128 or > 65536 || policy.Budget is < 1 or > 1000 || policy.MaxModelCalls is < 1 or > 64 || policy.MaxFailures is < 0 or > 32 ||
            policy.Commands.Select(c => c.Id).Distinct().Count() != policy.Commands.Length || requireTests && !policy.Commands.Any(c => c.IsTest))
            throw new ArgumentException("Invalid development policy; required test command and bounded scope are mandatory.");
        CheckAncestors(Root);
        foreach (var command in policy.Commands)
        {
            if (string.IsNullOrWhiteSpace(command.Id) || !Path.IsPathFullyQualified(command.Executable) || !File.Exists(command.Executable) ||
                command.Arguments is null || command.Arguments.Any(a => a is null) || command.ProtectedFiles is null ||
                command.Description is null || command.Description.Length > 1000 || command.RelatedPaths is null || command.TestEvidence is not ("exit-code" or "unittest") ||
                command.RelatedPaths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 256 || p.Contains(':') || p.Replace('\\', '/').Split('/').Any(s => s.StartsWith('.'))) ||
                command.IsTest && command.ProtectedFiles.Length == 0) throw new ArgumentException("Commands require an absolute executable, fixed arguments and protected test inputs.");
            CheckAncestors(command.Executable);
            foreach (var path in command.ProtectedFiles) Resolve(path);
        }
        foreach (var path in policy.WritableFiles)
        {
            Resolve(path);
            if (policy.Commands.SelectMany(c => c.ProtectedFiles).Any(p => Same(p, path))) throw new ArgumentException("Verifier inputs cannot be writable.");
        }
    }
    public string PolicyHash => GrantHash(Policy);
    public static string GrantHash(DevelopmentPolicy policy) => Hash(JsonSerializer.SerializeToUtf8Bytes(
        policy with { Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(policy.Root)) }));
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static bool Same(string a, string b) => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl)) throw new ArgumentException("Only project-relative paths allowed.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p.StartsWith('.') || p.EndsWith('.') || p.EndsWith(' ') || Excluded.Contains(p, StringComparer.OrdinalIgnoreCase) ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDevice(p))) throw new ArgumentException("Excluded or invalid project path.");
        if (!Policy.ReadExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("File extension is outside read scope.");
        var full = Path.GetFullPath(Path.Combine(Root, Path.Combine(parts)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("Path escapes project.");
        CheckAncestors(full); return full;
    }
    private static bool IsDevice(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (char.IsDigit(stem[3]) || "¹²³".Contains(stem[3]));
    }
    internal static void CheckAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Project paths and executables cannot traverse links or junctions.");
    }
    public string[] List()
    {
        var files = new List<string>(); var pending = new Queue<string>(); pending.Enqueue(Root); int visited = 0;
        while (pending.TryDequeue(out var directory))
        {
            if (++visited > 10000) throw new ArgumentException("Project directory limit exceeded.");
            CheckAncestors(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') || Excluded.Contains(name, StringComparer.OrdinalIgnoreCase) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(entry)) pending.Enqueue(entry);
                else if (Policy.ReadExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
                {
                    var relative = Path.GetRelativePath(Root, entry).Replace('\\', '/'); Resolve(relative); files.Add(relative);
                    if (files.Count > Policy.MaxFiles) throw new ArgumentException("Project file limit exceeded.");
                }
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }
    public byte[] Read(string path)
    {
        using var stream = new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > Policy.MaxFileBytes) throw new ArgumentException("Project file byte limit exceeded.");
        using var output = new MemoryStream(); byte[] buffer = new byte[4096]; int read;
        while ((read = stream.Read(buffer)) != 0)
        { if (output.Length + read > Policy.MaxFileBytes) throw new ArgumentException("Project file grew beyond limit."); output.Write(buffer, 0, read); }
        var bytes = output.ToArray(); Utf8.GetString(bytes); return bytes;
    }
    public string Text(string path) => Utf8.GetString(Read(path));
    public string Snapshot() => Hash(Encoding.UTF8.GetBytes(string.Join("\n", List().Select(p => p + ":" + Hash(Read(p))))));
    public bool CanWrite(string path) => Policy.WritableFiles.Any(p => Same(p, path));
}
