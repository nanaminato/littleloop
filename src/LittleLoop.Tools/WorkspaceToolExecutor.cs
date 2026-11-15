using System.Security.Cryptography;
using System.Text;
using LittleLoop.Core;

namespace LittleLoop.Tools;

/// <summary>Bounded local text tools for a trusted workspace, not an OS security sandbox.</summary>
public sealed class WorkspaceToolExecutor : IAsyncActionExecutor
{
    private readonly LearningSession session;
    private readonly string root;
    private readonly HashSet<string> enabled;
    private readonly int maxBytes;
    private readonly int timeoutMilliseconds;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public WorkspaceToolExecutor(LearningSession session, WorkspaceToolPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.Root) || policy.AllowedTools is null ||
            policy.AllowedTools.Any(t => !WorkspaceTools.List().Any(d => d.Id == t)) ||
            policy.AllowedTools.Distinct(StringComparer.Ordinal).Count() != policy.AllowedTools.Length ||
            policy.MaxFileBytes is < 1 or > 1048576 || policy.TimeoutMilliseconds is < 1 or > 60000)
            throw new ArgumentException("Invalid workspace tool policy.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(policy.Root));
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root))
            throw new ArgumentException("Tool root must be an existing dedicated directory, not a filesystem root.");
        if (Within(session.StatePath)) throw new ArgumentException("Keep the learning state outside the tool workspace.");
        CheckAncestors(root);
        this.session = session;
        enabled = new(policy.AllowedTools, StringComparer.Ordinal);
        maxBytes = policy.MaxFileBytes;
        timeoutMilliseconds = policy.TimeoutMilliseconds;
    }

    public async Task<ActionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMilliseconds);
        var token = deadline.Token;
        var selected = request.Trace.Decision.Selected;
        var proposal = selected.Proposal;
        var artifacts = new List<ToolArtifact>();
        ToolDescriptor? descriptor = null;
        bool touched = false;
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var call = proposal.Tool ?? throw new ToolDenied("Action has no executable tool binding.");
            descriptor = WorkspaceTools.List().SingleOrDefault(d => d.Id == call.Id)
                ?? throw new ToolDenied("Unknown tool ID.");
            if (!enabled.Contains(descriptor.Id) || proposal.Kind != ChoiceKind.Act || !selected.Eligible ||
                proposal.Capability != descriptor.Id || !request.Trace.Input.AllowedCapabilities.Contains(descriptor.Id, StringComparer.Ordinal) ||
                proposal.Cost < descriptor.Cost || proposal.Cost > request.Trace.Input.Budget ||
                proposal.Risk < descriptor.MinimumRisk || proposal.Risk > request.Trace.Input.MaximumRisk)
                throw new ToolDenied("Host grant, action capability, resource cost, or risk check denied execution.");
            if (call.Bindings is null || call.Bindings.Count != descriptor.Parameters.Length ||
                !descriptor.Parameters.All(call.Bindings.ContainsKey)) throw new ToolDenied("Tool parameter schema differs from its registered contract.");
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in descriptor.Parameters)
            {
                if (request.Trace.Input.ToolArguments is null ||
                    !request.Trace.Input.ToolArguments.TryGetValue(call.Bindings[parameter], out var value) || value is null)
                    throw new ToolDenied($"Missing task argument for {parameter}.");
                arguments.Add(parameter, value);
            }
            EnsureCurrent();

            string? source = arguments.TryGetValue("source", out var sourceName) ? Resolve(sourceName) : null;
            string? target = arguments.TryGetValue("target", out var targetName) ? Resolve(targetName) : null;
            if (target is not null)
            {
                if (File.Exists(target) || Directory.Exists(target)) throw new ToolDenied("Destination already exists; tools never overwrite files.", ToolFailure.TargetExists);
                if (!Directory.Exists(Path.GetDirectoryName(target))) throw new ToolDenied("Destination directory must already exist.");
            }
            byte[] input = source is not null ? await ReadBounded(source, token) : Utf8.GetBytes(arguments["content"]);
            if (input.Length > maxBytes) throw new ToolDenied("Input exceeds the configured byte limit.");
            if (source is not null) artifacts.Add(Artifact(source, "input", input));
            string text = Utf8.GetString(input); // Reject invalid UTF-8 instead of silently losing bytes.
            if (descriptor.Id == "text.read")
            {
                EnsureCurrent();
                return Receipt(ExecutionStatus.Completed, descriptor.Outcomes, "Read actual UTF-8 file bytes.", text);
            }
            byte[] output = descriptor.Id == "text.normalize-lines"
                ? Utf8.GetBytes(string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                    .Split('\n').Select(line => line.TrimEnd(' ', '\t')))) : input;
            if (output.Length > maxBytes) throw new ToolDenied("Output exceeds the configured byte limit.");
            // Recheck path and current task immediately before starting the write.
            target = Resolve(arguments["target"]);
            EnsureCurrent();
            temporary = Path.Combine(Path.GetDirectoryName(target)!, ".littleloop-" + Guid.NewGuid().ToString("N") + ".tmp");
            if (!Within(temporary)) throw new ToolDenied("Temporary file must stay within workspace.");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                touched = true;
                await stream.WriteAsync(output, token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            // A cancelled/revised task must not publish its prepared output.
            EnsureCurrent();
            target = Resolve(arguments["target"]);
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            { throw new ToolDenied("Destination was created by another writer.", ToolFailure.TargetExists); }
            temporary = null;
            var observed = await ReadBounded(target, token);
            artifacts.Add(Artifact(target, "output", observed));
            if (!observed.AsSpan().SequenceEqual(output)) throw new IOException("Output changed before verification.");
            return Receipt(ExecutionStatus.Completed, descriptor.Outcomes, "Created a real file and verified its bytes by reading it back.", null);
        }
        catch (ToolDenied error)
        {
            return Receipt(touched ? ExecutionStatus.Failed : ExecutionStatus.Blocked, [], error.Message, null, error.Code);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException or EncoderFallbackException)
        {
            var failure = error is DecoderFallbackException or EncoderFallbackException ? ToolFailure.InvalidEncoding
                : !touched && error is FileNotFoundException or DirectoryNotFoundException ? ToolFailure.MissingInput
                : error is UnauthorizedAccessException ? ToolFailure.PermissionDenied : ToolFailure.IoFailure;
            return Receipt(ExecutionStatus.Failed, [], $"{error.GetType().Name}: {error.Message}", null, failure);
        }
        // Cancellation/timeouts are unknown outcomes to the runtime, never invented success/failure.
        finally
        {
            if (temporary is not null)
            {
                CheckAncestors(Path.GetDirectoryName(temporary)!);
                if (Within(temporary) && File.Exists(temporary)) File.Delete(temporary);
            }
        }

        void EnsureCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!session.IsExecutionCurrent(request)) throw new ToolDenied("Task is no longer the current authorized dispatch.", ToolFailure.StaleTask);
            token.ThrowIfCancellationRequested();
        }
        ActionResult Receipt(ExecutionStatus status, string[] outcomes, string detail, string? outputText, ToolFailure? failure = null) =>
            new($"tool:{request.Trace.Input.Id}", request.Trace.Input.Id, proposal.Id, selected.Source,
                status, [.. outcomes], "workspace-tools/1", detail, touched)
            {
                ToolReceipt = descriptor is null ? null : new(descriptor.Id, descriptor.Version, root, [.. artifacts], outputText),
                Failure = failure
            };
    }

    private async Task<byte[]> ReadBounded(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length > maxBytes) throw new ToolDenied("File exceeds the configured byte limit.");
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maxBytes) throw new ToolDenied("File grew beyond the configured byte limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl))
            throw new ToolDenied("Only relative workspace paths are allowed.");
        var parts = relative.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³')))
                throw new ToolDenied("Path contains a traversal, hidden, invalid, or reserved component.");
        }
        string resolved = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!Within(resolved)) throw new ToolDenied("Path escapes workspace.");
        CheckAncestors(root);
        string current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ToolDenied("Symbolic links and junctions are not allowed.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return resolved;
    }
    private bool Within(string path) => Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    private static void CheckAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ToolDenied("Workspace and its ancestors must not be symbolic links or junctions.");
    }
    private ToolArtifact Artifact(string path, string usage, byte[] bytes) =>
        new(Path.GetRelativePath(root, path), usage, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    private sealed class ToolDenied(string message, ToolFailure code = ToolFailure.PermissionDenied) : ArgumentException(message)
    { public ToolFailure Code { get; } = code; }
}
