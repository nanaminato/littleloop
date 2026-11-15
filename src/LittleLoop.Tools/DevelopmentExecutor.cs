using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LittleLoop.Core;

namespace LittleLoop.Tools;

public static class DevelopmentTools
{
    public static ToolDescriptor[] List() => [
        new("project.inspect", "1", "Browse project structure and declared project references; paginated", ["prefix", "offset"], ["project.searched"], ["read-only"], 1, .01m),
        new("project.search-page", "1", "Search literal text within a path prefix; paginated", ["query", "prefix", "offset"], ["project.searched"], ["read-only"], 1, .01m),
        new("project.read-range", "1", "Read numbered source lines and observe whole-file SHA-256", ["path", "startLine", "lineCount"], ["code.read"], ["read-only"], 1, .01m),
        new("project.search", "1", "List files and search literal text", ["query"], ["project.searched"], ["read-only"], 1, .01m),
        new("project.read", "1", "Read source and observe SHA-256", ["path"], ["code.read"], ["read-only"], 1, .01m),
        new("project.patch", "1", "Replace one exact occurrence under hash guard", ["path", "sha256", "oldText", "newText"], ["code.changed"], ["code-edit"], 1, .15m),
        new("project.command", "1", "Run a fixed host-authorized command", ["command"], ["command.succeeded"], ["process-execution"], 1, .25m)
    ];
}

public sealed class DevelopmentExecutor(LearningSession session, ProjectFiles files) : IAsyncActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var selected = request.Trace.Decision.Selected; var proposal = selected.Proposal;
        var artifacts = new List<ToolArtifact>(); bool effect = false; CommandObservation? command = null;
        var tool = DevelopmentTools.List().SingleOrDefault(t => t.Id == proposal.Tool?.Id);
        try
        {
            Current();
            if (tool is null || proposal.Kind != ChoiceKind.Act || !selected.Eligible || proposal.Capability != tool.Id ||
                proposal.Cost != tool.Cost || proposal.Risk < tool.MinimumRisk || !request.Trace.Input.AllowedCapabilities.Contains(tool.Id) ||
                proposal.Tool!.Bindings.Count != tool.Parameters.Length || !tool.Parameters.All(proposal.Tool.Bindings.ContainsKey))
                throw new ArgumentException("Development action contract or permission denied.");
            var args = tool.Parameters.ToDictionary(p => p, p => request.Trace.Input.ToolArguments![proposal.Tool!.Bindings[p]]);
            int Number(string key) => int.TryParse(args[key], out var value) ? value : throw new ArgumentException("Expected integer argument: " + key);
            if (tool.Id == "project.inspect") return Result(ExecutionStatus.Completed, tool.Outcomes, ProjectInspection.Inspect(files, args["prefix"], Number("offset")));
            if (tool.Id == "project.search-page") return Result(ExecutionStatus.Completed, tool.Outcomes, ProjectInspection.Search(files, args["query"], args["prefix"], Number("offset")));
            if (tool.Id == "project.read-range")
            {
                var bytes = files.Read(args["path"]); Add(args["path"], "input", bytes);
                string output = ProjectInspection.Range(files, args["path"], Number("startLine"), Number("lineCount"));
                if (ProjectFiles.Hash(files.Read(args["path"])) != ProjectFiles.Hash(bytes))
                    return Result(ExecutionStatus.Failed, [], "File changed during range read; reread.", ToolFailure.StaleContent);
                return Result(ExecutionStatus.Completed, tool.Outcomes, output);
            }
            if (tool.Id == "project.search")
            {
                if (args["query"].Length > 256) throw new ArgumentException("Search query too long.");
                var matches = new List<object>(); int used = 0;
                foreach (var path in files.List())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (args["query"].Length == 0) { matches.Add(new { path }); used += path.Length + 32; }
                    else
                    {
                        var lines = files.Text(path).Split('\n');
                        for (int i = 0; i < lines.Length; i++) if (lines[i].Contains(args["query"], StringComparison.OrdinalIgnoreCase))
                        {
                            string excerpt = lines[i][..Math.Min(500, lines[i].Length)]; used += excerpt.Length + path.Length + 64;
                            if (used > files.Policy.MaxOutputCharacters) break;
                            matches.Add(new { path, line = i + 1, text = excerpt });
                        }
                    }
                    if (used > files.Policy.MaxOutputCharacters) break;
                }
                return Result(ExecutionStatus.Completed, tool.Outcomes, JsonSerializer.Serialize(new { matches, truncated = used > files.Policy.MaxOutputCharacters }));
            }
            if (tool.Id == "project.read")
            {
                var bytes = files.Read(args["path"]); Add(args["path"], "input", bytes);
                return Result(ExecutionStatus.Completed, tool.Outcomes, Encoding.UTF8.GetString(bytes));
            }
            if (tool.Id == "project.patch")
            {
                string path = args["path"];
                if (!files.CanWrite(path)) throw new ArgumentException("File is not granted for modification.");
                byte[] before = files.Read(path); Add(path, "input", before);
                if (ProjectFiles.Hash(before) != args["sha256"]) return Result(ExecutionStatus.Failed, [], "File changed since observation; reread before patching.", ToolFailure.StaleContent);
                string original = Encoding.UTF8.GetString(before), oldText = args["oldText"], newText = args["newText"];
                int at = oldText.Length == 0 ? -1 : original.IndexOf(oldText, StringComparison.Ordinal);
                if (at < 0 || original.IndexOf(oldText, at + oldText.Length, StringComparison.Ordinal) >= 0 || oldText == newText)
                    return Result(ExecutionStatus.Failed, [], "Patch must match exactly one nonempty occurrence and change it.", ToolFailure.InvalidArguments);
                byte[] changed = new UTF8Encoding(false, true).GetBytes(original[..at] + newText + original[(at + oldText.Length)..]);
                if (changed.Length > files.Policy.MaxFileBytes) throw new ArgumentException("Patched file exceeds byte limit.");
                Current();
                // Exclusive handle protects against cooperating concurrent writers; never truncate until both hash checks pass.
                using (var stream = new FileStream(files.Resolve(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (stream.Length != before.Length) return Result(ExecutionStatus.Failed, [], "Concurrent file change.", ToolFailure.StaleContent);
                    var observed = new byte[before.Length]; stream.ReadExactly(observed);
                    if (!observed.AsSpan().SequenceEqual(before)) return Result(ExecutionStatus.Failed, [], "Concurrent file change.", ToolFailure.StaleContent);
                    Current(); effect = true; stream.Position = 0; stream.Write(changed); stream.SetLength(changed.Length); stream.Flush(true);
                }
                var after = files.Read(path); Add(path, "output", after);
                if (!after.AsSpan().SequenceEqual(changed)) throw new IOException("Patched file changed before read-back.");
                return Result(ExecutionStatus.Completed, tool.Outcomes, "Exact patch applied and read back.");
            }
            var grant = files.Policy.Commands.SingleOrDefault(c => c.Id == args["command"]) ?? throw new ArgumentException("Command ID is not authorized.");
            string snapshot = files.Snapshot();
            var start = new ProcessStartInfo(grant.Executable) { WorkingDirectory = files.Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in grant.Arguments) start.ArgumentList.Add(arg);
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            string temp = Path.Combine(files.Root, ".littleloop-temp");
            ProjectFiles.CheckAncestors(temp); Directory.CreateDirectory(temp); ProjectFiles.CheckAncestors(temp);
            start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp; start.Environment["TMPDIR"] = temp;
            // SDKs need a writable home, without inheriting the user's credentials or settings.
            start.Environment["HOME"] = temp; start.Environment["USERPROFILE"] = temp;
            start.Environment["DOTNET_CLI_HOME"] = temp; start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["APPDATA"] = temp; start.Environment["LOCALAPPDATA"] = temp;
            start.Environment["NUGET_PACKAGES"] = Path.Combine(temp, "packages");
            start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
            using var process = new Process { StartInfo = start };
            Current();
            if (!process.Start()) throw new IOException("Command did not start.");
            effect = true; process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(files.Policy.TimeoutMilliseconds);
            bool truncated = false;
            async Task<string> Drain(StreamReader reader)
            {
                var output = new StringBuilder(); char[] buffer = new char[1024]; int n;
                while ((n = await reader.ReadAsync(buffer)) != 0)
                {
                    int keep = Math.Min(n, Math.Max(0, files.Policy.MaxOutputCharacters - output.Length)); output.Append(buffer, 0, keep);
                    if (keep < n) { truncated = true; timeout.Cancel(); }
                }
                return output.ToString();
            }
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                throw; // Unknown effects: runtime retains reconciliation state, never auto-retries.
            }
            if (truncated) throw new OperationCanceledException("Command output limit exceeded; reconcile effects.");
            string afterSnapshot = files.Snapshot();
            command = new(grant.Id, process.ExitCode, stdout.Result, stderr.Result, snapshot, afterSnapshot, grant.IsTest);
            bool verified = process.ExitCode == 0 && (grant.TestEvidence != "unittest" ||
                System.Text.RegularExpressions.Regex.IsMatch(stderr.Result, @"Ran [1-9][0-9]* tests? in "));
            return Result(verified ? ExecutionStatus.Completed : ExecutionStatus.Failed,
                verified ? grant.IsTest ? ["command.succeeded", "tests.passed"] : tool.Outcomes : [],
                $"Exit {process.ExitCode}\nstdout:\n{stdout.Result}\nstderr:\n{stderr.Result}" +
                    (process.ExitCode == 0 && !verified ? "\nNo positive executed-test count; this is not test success." : ""), verified ? null : ToolFailure.CommandFailed);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return Result(effect ? ExecutionStatus.Failed : ExecutionStatus.Blocked, [], e.Message,
                e is FileNotFoundException ? ToolFailure.MissingInput : effect ? ToolFailure.IoFailure : ToolFailure.PermissionDenied);
        }
        void Current()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.IsExecutionCurrent(request)) throw new ArgumentException("Development task dispatch is no longer current.");
        }
        void Add(string path, string usage, byte[] bytes) => artifacts.Add(new(path.Replace('\\', '/'), usage, bytes.Length, ProjectFiles.Hash(bytes)));
        ActionResult Result(ExecutionStatus status, string[] outcomes, string text, ToolFailure? failure = null) =>
            new("dev:" + request.Trace.Input.Id, request.Trace.Input.Id, proposal.Id, selected.Source, status, outcomes, "development-tools/1", text, effect)
            { Failure = failure, ToolReceipt = new(tool?.Id ?? "unknown", "1", files.Root, artifacts.ToArray(), text), Command = command };
    }
}
