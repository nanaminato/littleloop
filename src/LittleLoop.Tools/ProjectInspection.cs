using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace LittleLoop.Tools;

// Structural evidence only: project manifests are never evaluated or executed.
public static class ProjectInspection
{
    public static string Inspect(ProjectFiles files, string prefix = "", int offset = 0)
    {
        var paths = Paths(files, prefix);
        PageOffset(offset);
        var entries = paths.Skip(offset).Take(12).Select(path => new
        {
            path, writable = files.CanWrite(path),
            kind = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? "dotnet-project" :
                path.Contains("test", StringComparison.OrdinalIgnoreCase) ? "test-candidate" : "file",
            projectReferences = References(files, path)
        }).ToArray();
        return JsonSerializer.Serialize(new { prefix, total = paths.Length, offset,
            nextOffset = offset + entries.Length < paths.Length ? (int?)(offset + entries.Length) : null, entries });
    }

    public static string Search(ProjectFiles files, string query, string prefix, int offset)
    {
        PageOffset(offset);
        if (query.Length > 256) throw new ArgumentException("Search query too long.");
        var hits = new List<object>(); int index = 0; bool more = false;
        foreach (var path in Paths(files, prefix))
        {
            var lines = query.Length == 0 ? new[] { "" } : files.Text(path).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (query.Length != 0 && !lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (index++ < offset) continue;
                if (hits.Count == 8) { more = true; break; }
                hits.Add(new { path, line = query.Length == 0 ? (int?)null : i + 1,
                    text = lines[i][..Math.Min(160, lines[i].Length)], lineTruncated = lines[i].Length > 160 });
            }
            if (more) break;
        }
        return JsonSerializer.Serialize(new { matches = hits, nextOffset = more ? (int?)(offset + hits.Count) : null });
    }

    public static string Range(ProjectFiles files, string path, int startLine, int lineCount)
    {
        if (startLine < 1 || lineCount is < 1 or > 80) throw new ArgumentException("Use positive startLine and lineCount between 1 and 80.");
        var lines = files.Text(path).Split('\n');
        if (startLine > lines.Length) throw new ArgumentException("Start line is outside file.");
        return JsonSerializer.Serialize(new { path, startLine, totalLines = lines.Length,
            nextLine = startLine - 1 + lineCount < lines.Length ? (int?)(startLine + lineCount) : null,
            lines = lines.Skip(startLine - 1).Take(lineCount).Select((text, i) => new { line = startLine + i, text }) });
    }

    private static string[] Paths(ProjectFiles files, string prefix)
    {
        prefix = prefix.Replace('\\', '/');
        if (prefix.Length != 0 && (prefix.StartsWith('/') || prefix.Contains(':') ||
            prefix.TrimEnd('/').Split('/').Any(p => p.Length == 0 || p.StartsWith('.'))))
            throw new ArgumentException("Use a project-relative prefix without hidden or traversal segments.");
        return files.List().Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private static void PageOffset(int offset)
    { if (offset is < 0 or > 1000000) throw new ArgumentException("Invalid page offset."); }
    private static string[] References(ProjectFiles files, string path)
    {
        if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return [];
        try
        {
            using var text = new StringReader(files.Text(path));
            using var reader = XmlReader.Create(text, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader).Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value).OfType<string>().Take(24).ToArray();
        }
        catch (XmlException) { return ["Unparsed manifest; inspect source directly."]; }
    }
}
