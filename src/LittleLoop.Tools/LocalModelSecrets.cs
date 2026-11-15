namespace LittleLoop.Tools;

/// <summary>Reads only the requested credential. Never evaluates shell syntax or exports unrelated variables.</summary>
public static class LocalModelSecrets
{
    public static string? FindProjectDirectory(string anchor)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(anchor)); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "LittleLoop.Cli", "LittleLoop.Cli.csproj"))) return directory.FullName;
        return null;
    }
    public static string? Read(string name, string? projectDirectory = null)
    {
        var environment = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(environment)) return environment;
        var root = projectDirectory ?? FindProjectDirectory(Environment.CurrentDirectory) ?? FindProjectDirectory(AppContext.BaseDirectory);
        if (root is null) return null;
        string file = Path.Combine(root, ".env.local");
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 65536) throw new ArgumentException("Local credential file exceeds size limit.");
        string? value = null;
        foreach (var line in File.ReadLines(file))
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            int separator = entry.IndexOf('=');
            if (separator < 1 || entry[..separator].Trim() != name) continue;
            if (value is not null) throw new ArgumentException("Duplicate entry in local credential file.");
            value = entry[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
        }
        return value;
    }
}
