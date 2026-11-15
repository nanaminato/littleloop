using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace LittleLoop.Tools;

public sealed record ProjectDiscoveryReport(DevelopmentPolicy ProposedPolicy, ProjectCommand[] ExtraCommands,
    string[] Evidence, string[] Questions, Dictionary<string, string> ManifestHashes);

public static class ProjectDiscovery
{
    private static readonly string[] Extensions = [".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".py", ".toml", ".json", ".js", ".ts", ".tsx", ".jsx", ".md", ".txt", ".yaml", ".yml"];
    public static ProjectDiscoveryReport Discover(string root)
    {
        var scan = new ProjectFiles(new(root, [], [], Extensions, MaxFileBytes: 262144, MaxFiles: 3000,
            TimeoutMilliseconds: 120000, MaxOutputCharacters: 32768, Budget: 48, MaxModelCalls: 60, MaxFailures: 8), false);
        var paths = scan.List(); var commands = new List<ProjectCommand>(); var extras = new List<ProjectCommand>();
        var evidence = new List<string>(); var questions = new List<string>(); var manifests = new Dictionary<string, string>();
        var protectedFiles = paths.Where(IsTestFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            manifests[path] = ProjectFiles.Hash(scan.Read(path));
            try
            {
                using var input = new StringReader(scan.Text(path));
                using var reader = XmlReader.Create(input, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var document = XDocument.Load(reader);
                foreach (var reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                {
                    string include = reference.Attribute("Include")?.Value ?? "";
                    evidence.Add($"Project reference {path}: {include}");
                    if (include.Contains('$') || include.Contains('*') || include.Length == 0)
                    { questions.Add("Dynamic project reference requires scope clarification: " + path + " -> " + include); continue; }
                    string referenced = Path.GetFullPath(include.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(scan.Resolve(path))!);
                    if (!referenced.StartsWith(scan.Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        questions.Add("Project reference escapes requested root; explicitly choose a common project root before execution: " + referenced);
                }
                bool testSdk = document.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName == "PackageReference" && (e.Attribute("Include")?.Value.Contains("Test.Sdk", StringComparison.OrdinalIgnoreCase) ?? false));
                bool console = document.Descendants().Any(e => e.Name.LocalName == "OutputType" && e.Value.Equals("Exe", StringComparison.OrdinalIgnoreCase));
                bool test = testSdk || console && IsTestFile(path);
                string dir = (Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "") + "/";
                var inputs = paths.Where(p => p.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (test) foreach (var file in inputs) protectedFiles.Add(file);
                evidence.Add($".NET: {path}; test={test}; console={console}; manifest declarations only.");
                string? exe = FindExecutable("dotnet");
                if (exe is null) { questions.Add("Install/configure dotnet before running " + path); continue; }
                string id = "dotnet:" + path;
                commands.Add(new(id, exe, test ? testSdk ? ["test", path, "--no-restore", "-m:1", "--disable-build-servers"] :
                    ["run", "--no-restore", "-p:BuildInParallel=false", "--project", path, "--disable-build-servers"] :
                    ["build", path, "--no-restore", "-m:1", "--disable-build-servers"], test, test ? inputs : [])
                { Description = test ? "Discovered test entry; run project code without dependency restore." : "Build discovered project without dependency restore.", RelatedPaths = [dir] });
                extras.Add(new("restore:" + path, exe, ["restore", path, "--disable-parallel"], false, [])
                { Description = "Dependency restore may access network and run project build hooks; separate permission required.", RelatedPaths = [dir] });
            }
            catch (XmlException) { questions.Add("Cannot parse project manifest: " + path); }
        }
        var pythonTests = paths.Where(p => p.EndsWith(".py", StringComparison.OrdinalIgnoreCase) &&
            (Path.GetFileName(p).StartsWith("test_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p) == "tests.py")).ToArray();
        if (pythonTests.Length > 0)
        {
            evidence.Add("Python test files: " + string.Join(", ", pythonTests));
            foreach (var p in pythonTests) protectedFiles.Add(p);
            string? exe = FindExecutable(OperatingSystem.IsWindows() ? "python" : "python3");
            if (exe is null) questions.Add("Python executable was not found.");
            else foreach (var dir in pythonTests.Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "").Distinct())
            {
                string folder = dir.Length == 0 ? "." : dir;
                var tests = pythonTests.Where(p => (Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "") == dir).ToArray();
                commands.Add(new("unittest:" + folder, exe, ["-B", "-m", "unittest", "discover", "-s", folder, "-p", "test*.py"], true, tests)
                { Description = "Standard-library unittest discovery candidate; pytest-only tests require clarification, not assumed success.", RelatedPaths = [folder], TestEvidence = "unittest" });
            }
            if (pythonTests.Any(p => scan.Text(p).Contains("import pytest", StringComparison.Ordinal)))
                questions.Add("pytest imports found: unittest is not sufficient; provide a supported test adapter before execution.");
        }
        foreach (var path in paths.Where(p => Path.GetFileName(p) == "package.json"))
        {
            manifests[path] = ProjectFiles.Hash(scan.Read(path));
            using var json = JsonDocument.Parse(scan.Text(path));
            evidence.Add("Node package: " + path);
            if (json.RootElement.TryGetProperty("scripts", out var scripts))
                foreach (var script in scripts.EnumerateObject()) evidence.Add($"Declared npm script {path}:{script.Name} = {script.Value}");
            questions.Add("Node scripts discovered but not auto-authorized: this version requires an explicit npm command adapter/review; scripts may run arbitrary shell code.");
        }
        if (!commands.Any(c => c.IsTest)) questions.Add("No executable test entry identified; clarify test framework/entry instead of treating a successful build as task completion.");
        // Source edits are inferred; manifests, tests, credentials and scripts are not writable by default.
        var writable = paths.Where(p => new[] { ".cs", ".py", ".js", ".ts", ".tsx", ".jsx" }.Contains(Path.GetExtension(p)) && !protectedFiles.Contains(p)).ToArray();
        // Every test command protects the complete discovered verifier set.
        commands = commands.Select(c => c.IsTest ? c with { ProtectedFiles = protectedFiles.Order().ToArray() } : c).ToList();
        return new(scan.Policy with { WritableFiles = writable, Commands = commands.ToArray() }, extras.ToArray(), evidence.ToArray(), questions.ToArray(), manifests);
    }
    private static bool IsTestFile(string path) => path.Split('/').Any(p => p.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        p.Contains(".Tests", StringComparison.OrdinalIgnoreCase) || p.Contains(".Checks", StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(path).StartsWith("test_", StringComparison.OrdinalIgnoreCase);
    public static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory.Trim('"'), name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
            path = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            try { ProjectFiles.CheckAncestors(path); return path; } catch (ArgumentException) { }
        }
        return null;
    }
}
