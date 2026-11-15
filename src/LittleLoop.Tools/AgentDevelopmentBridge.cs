using System.Text.Json;
using LittleLoop.Core;

namespace LittleLoop.Tools;

// Host-owned tools do not depend on the desktop's private process-provisioning service.
public sealed class AgentDevelopmentBridge
{
    private readonly ProjectFiles files;
    private readonly LearningSession session;
    private readonly Dictionary<string, string> protectedHashes;
    private readonly string initialSnapshot;
    private readonly string callsDirectory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AgentDevelopmentBridge(string directory, DevelopmentPolicy policy, PersistentRole? role)
    {
        files = new(policy);
        callsDirectory = Path.Combine(directory, "tool-calls"); Directory.CreateDirectory(callsDirectory);
        session = new(Path.Combine(directory, "tools-learning.json"));
        string baseline = Path.Combine(directory, "development-baseline.json");
        if (!File.Exists(baseline))
        {
            if (File.Exists(session.StatePath)) throw new IOException("Missing development baseline; reconcile manually.");
            protectedHashes = policy.Commands.SelectMany(c => c.ProtectedFiles).Distinct()
                .ToDictionary(p => p, p => ProjectFiles.Hash(files.Read(p)));
            initialSnapshot = files.Snapshot();
            File.WriteAllText(baseline, JsonSerializer.Serialize(new Baseline(initialSnapshot, files.PolicyHash, protectedHashes), Json));
            session.Initialize(role); session.EnableScheduling(new(1, policy.Budget));
        }
        else
        {
            var saved = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(baseline), Json)!;
            if (saved.PolicyHash != files.PolicyHash) throw new IOException("Development grant changed; create a new authorized session.");
            protectedHashes = saved.ProtectedHashes; initialSnapshot = saved.Snapshot;
        }
        if (session.Read().Runtime!.Attempts.Any(a => a.Status is AttemptStatus.Uncertain or AttemptStatus.Dispatched))
            throw new IOException("Uncertain tool effects require reconciliation before resuming.");
    }
    private sealed record Baseline(string Snapshot, string PolicyHash, Dictionary<string, string> ProtectedHashes);
    private sealed record ToolCall(string Fingerprint, string TaskId);
    public object[] Specifications => DevelopmentTools.List().Select(t => (object)new
    {
        type = "function", name = t.Id.Replace('.', '_').Replace('-', '_'), description = t.Description,
        inputSchema = new { type = "object", additionalProperties = false, required = t.Parameters,
            properties = t.Parameters.ToDictionary(k => k, _ => new { type = "string" }) }
    }).ToArray();
    public string Instructions => "Use ONLY the supplied project_* tools for project work. Native shell/file tools are unavailable in this standalone desktop build. " +
        "These tools are implemented and authorized by LittleLoop. Do not use browser, CUA or other tools as substitutes. " +
        "Read relevant source before editing; patch needs the actual observed SHA256. Numeric arguments are strings. " +
        "Commands accept only a fixed command ID, never shell text. Diagnose real failures and revise your plan. " +
        "Never change protected verifier files, deploy, access credentials or expand permissions. If scope is insufficient, explain what is needed and why. " +
        "Return a JSON summary and remaining questions. Completion is independently checked against actual edits and tests. Scope: " +
        JsonSerializer.Serialize(new { writableFiles = files.Policy.WritableFiles, commands = files.Policy.Commands.Select(c => new { c.Id, c.Description, c.IsTest }) });

    public async Task<object> InvokeAsync(JsonElement request, string threadId, string turnId, CancellationToken token)
    {
        if (request.GetProperty("threadId").GetString() != threadId || request.GetProperty("turnId").GetString() != turnId)
            return Response(false, "Tool request belongs to another thread/turn.");
        if (request.TryGetProperty("namespace", out var ns) && ns.ValueKind != JsonValueKind.Null)
            return Response(false, "Unexpected tool namespace.");
        var tool = DevelopmentTools.List().SingleOrDefault(t => t.Id.Replace('.', '_').Replace('-', '_') == request.GetProperty("tool").GetString());
        if (tool is null) return Response(false, "Unknown host tool.");
        Dictionary<string, string> args;
        try
        {
            args = request.GetProperty("arguments").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            if (args.Count != tool.Parameters.Length || !tool.Parameters.All(args.ContainsKey) || args.Values.Any(v => v is null || v.Length > 65536))
                return Response(false, "Tool argument schema mismatch.");
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return Response(false, "Invalid arguments."); }
        if (ProtectedChanged()) throw new IOException("Protected verifier changed; stop and reconcile.");
        var before = session.Read();
        string callId = request.GetProperty("callId").GetString() ?? throw new ArgumentException("Missing call ID.");
        string fingerprint = ProjectFiles.Hash(System.Text.Encoding.UTF8.GetBytes(request.GetRawText()));
        string callFile = Path.Combine(callsDirectory, ProjectFiles.Hash(System.Text.Encoding.UTF8.GetBytes(threadId + ":" + callId)) + ".json");
        if (File.Exists(callFile))
        {
            var saved = JsonSerializer.Deserialize<ToolCall>(File.ReadAllText(callFile), Json)!;
            if (saved.Fingerprint != fingerprint) throw new IOException("Tool call ID reused with different payload.");
            var prior = before.Runtime!.Tasks.SingleOrDefault(t => t.Task.Id == saved.TaskId);
            if (prior is null) throw new IOException("Tool dispatch interrupted before durable task enqueue; reconcile.");
            var recorded = before.Episodes.SingleOrDefault(e => e.Trace.Input.Id == prior.CurrentDecisionId)?.Result;
            if (recorded is null) throw new IOException("Repeated tool call has no reconciled result.");
            return Response(recorded.Status == ExecutionStatus.Completed, JsonSerializer.Serialize(recorded, Json));
        }
        if (before.Runtime!.Tasks.Length >= files.Policy.Budget) return Response(false, "Host tool budget exhausted.");
        if (tool.Id == "project.patch" && !before.Episodes.Any(e => e.Result?.ToolReceipt is { } r &&
            r.ToolId is "project.read" or "project.read-range" && r.Artifacts.Any(a => a.Path == args["path"].Replace('\\', '/') && a.Sha256 == args["sha256"])))
            return Response(false, "Read this file and use the observed hash before patching.");
        string id = "agent-tool-" + (before.Runtime.Tasks.Length + 1);
        using (var stream = new FileStream(callFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new ToolCall(fingerprint, id), Json);
        var proposal = new Proposal(tool.Id, ChoiceKind.Act, tool.Description, 0, tool.MinimumRisk, "Agent proposes an action under fixed host scope.", tool.Id, tool.Cost)
        { Tool = new(tool.Id, args.Keys.ToDictionary(k => k, k => k)), Meaning = new("development/" + tool.Id + "/1", tool.Outcomes, tool.Traits, []) };
        session.Enqueue(new(id, "Execute bounded development action", new() { ["workflow"] = "agent-development", ["grant"] = files.PolicyHash, ["projectSnapshot"] = files.Snapshot(), ["tool"] = tool.Id },
            tool.Outcomes.Select((o, i) => new RoleGoal("result-" + i, o, 1, true)).ToArray(), [proposal], [], [], 1, [tool.Id]) { ToolArguments = args });
        var after = await new MultiTaskRuntime(session, new DevelopmentExecutor(session, files)).RunAsync(cancellationToken: token);
        var work = after.Runtime!.Tasks.Single(t => t.Task.Id == id);
        var episode = after.Episodes.SingleOrDefault(e => e.Trace.Input.Id == work.CurrentDecisionId);
        if (work.Status == WorkStatus.NeedsReconciliation) throw new IOException("Tool effects unknown; reconcile before continuing.");
        if (episode?.Result is not { } result) return Response(false, "Core declined or deferred this action: " + work.Status);
        bool success = result.Status == ExecutionStatus.Completed;
        if (result.Status is ExecutionStatus.Completed or ExecutionStatus.Failed)
            session.Feedback(new("feedback:" + id, result.DecisionId, null, success, success ? result.ActualOutcomes : [], "agent-tool-receipt", result.Detail));
        return Response(success, JsonSerializer.Serialize(result, Json));
    }
    public string[] Validate()
    {
        var issues = new List<string>();
        if (ProtectedChanged()) issues.Add("Protected verifier changed.");
        string snapshot = files.Snapshot();
        if (snapshot == initialSnapshot) issues.Add("No source change on disk.");
        var results = session.Read().Episodes.Select(e => e.Result).Where(r => r is not null).ToArray();
        if (!results.Any(r => r!.Status == ExecutionStatus.Completed && r.ToolReceipt?.ToolId == "project.patch")) issues.Add("No successful host patch receipt.");
        foreach (var test in files.Policy.Commands.Where(c => c.IsTest))
        {
            var result = results.LastOrDefault(r => r!.Command?.Id == test.Id);
            if (result?.Status != ExecutionStatus.Completed || result.Command?.BeforeSnapshot != snapshot || result.Command.AfterSnapshot != snapshot)
                issues.Add("Required test has no passing receipt on current snapshot: " + test.Id + "\n" + result?.Detail);
        }
        return issues.ToArray();
    }
    private bool ProtectedChanged() => protectedHashes.Any(p => ProjectFiles.Hash(files.Read(p.Key)) != p.Value);
    private static object Response(bool success, string text) => new { success, contentItems = new[] { new { type = "inputText", text } } };
    public static JsonElement ReportSchema => JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false,
        required = new[] { "summary", "questions" }, properties = new { summary = new { type = "string" }, questions = new { type = "array", items = new { type = "string" } } } });
}
