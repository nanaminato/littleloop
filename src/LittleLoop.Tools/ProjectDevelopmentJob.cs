using System.Text.Json;
using LittleLoop.Core;

namespace LittleLoop.Tools;

public sealed record ProjectDevelopmentJob(string Goal, string ModelFile, ProjectDiscoveryReport Discovery,
    string RequestId, string Snapshot, string Status, Dictionary<string, DevelopmentPolicy> Policies);

// Host workflow, separate from the model/executor API. A grant is a durable, explicit CLI action.
public sealed class ProjectDevelopmentHost(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string StatePath => Path.Combine(DirectoryPath, "learning.json");
    private string JobPath => Path.Combine(DirectoryPath, "job.json");
    public ProjectDevelopmentJob Read() => JsonSerializer.Deserialize<ProjectDevelopmentJob>(File.ReadAllText(JobPath), Json) ?? throw new InvalidDataException("Missing project job.");
    public ProjectDevelopmentJob Create(string project, string goal, string modelFile)
    {
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > 8192) throw new ArgumentException("Provide a bounded natural-language goal.");
        var report = ProjectDiscovery.Discover(project);
        if (ProjectFiles.Same(DirectoryPath, report.ProposedPolicy.Root) || DirectoryPath.StartsWith(report.ProposedPolicy.Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Keep host state outside the target project.");
        Directory.CreateDirectory(DirectoryPath);
        using var lease = Lease();
        if (File.Exists(JobPath)) throw new ArgumentException("Project job already exists.");
        var files = new ProjectFiles(report.ProposedPolicy, false);
        var job = new ProjectDevelopmentJob(goal, Path.GetFullPath(modelFile), report, Guid.NewGuid().ToString("N"), files.Snapshot(),
            report.Questions.Length == 0 ? "awaiting-permission" : "needs-clarification", []);
        Write(job); return job;
    }
    public void Decide(string requestId, bool approve)
    {
        using var lease = Lease(); var job = Read();
        if (job.Status == "awaiting-permission")
        {
            if (job.RequestId != requestId) throw new ArgumentException("Stale permission request ID.");
            var files = new ProjectFiles(job.Discovery.ProposedPolicy);
            if (approve && files.Snapshot() != job.Snapshot) throw new ArgumentException("Project changed after discovery; rediscover before approving.");
            Write(job with { Status = approve ? "approved" : "denied", Policies = approve ? new() { [files.PolicyHash] = files.Policy } : [] });
            return;
        }
        if (job.Status != "approved" || !File.Exists(StatePath)) throw new ArgumentException("No active request to approve.");
        var session = new LearningSession(StatePath); var state = session.Read(); var run = state.DevelopmentRuns.Single(r => r.Id == "project");
        var request = run.PendingPermission;
        if (request?.Id != requestId) throw new ArgumentException("Stale permission request ID.");
        var policy = job.Policies[run.PolicyHash]; var filesBefore = new ProjectFiles(policy);
        if (run.ProtectedHashes.Any(p => ProjectFiles.Hash(filesBefore.Read(p.Key)) != p.Value)) throw new ArgumentException("Verifier inputs changed; approval cannot override them.");
        if (approve)
        {
            if (request.Kind == "command")
            {
                // No arbitrary executable/arguments accepted from model output.
                var command = job.Discovery.ExtraCommands.Single(c => c.Id == request.Resource);
                foreach (var manifest in job.Discovery.ManifestHashes)
                    if (ProjectFiles.Hash(filesBefore.Read(manifest.Key)) != manifest.Value) throw new ArgumentException("Manifest changed since command discovery; rediscover command before granting.");
                policy = policy with { Commands = [.. policy.Commands, command] };
            }
            else if (request.Kind == "write")
            {
                filesBefore.Resolve(request.Resource);
                if (run.ProtectedHashes.Keys.Any(p => ProjectFiles.Same(p, request.Resource))) throw new ArgumentException("Protected tests cannot be granted writable.");
                policy = policy with { WritableFiles = [.. policy.WritableFiles, request.Resource] };
            }
            else throw new ArgumentException("Unsupported grant kind.");
        }
        var next = new ProjectFiles(policy);
        // Persist candidate grant first; only the atomic core transition makes its hash active.
        var policies = new Dictionary<string, DevelopmentPolicy>(job.Policies) { [next.PolicyHash] = next.Policy };
        Write(job with { Policies = policies });
        session.ResolveDevelopmentPermission(run.Id, requestId, approve, next.PolicyHash, state.Revision);
    }
    public async Task<DevelopmentRun> ResumeAsync(ISemanticModelClient model, int maxInput = 100000, int maxObservation = 6000, CancellationToken token = default)
    {
        using var lease = Lease(); var job = Read();
        if (job.Status != "approved") throw new ArgumentException("Project scope has not been explicitly approved.");
        var session = new LearningSession(StatePath);
        if (!File.Exists(StatePath)) session.Initialize();
        if (session.Read().Runtime is null) session.EnableScheduling(new(1, 100));
        var run = session.Read().DevelopmentRuns.SingleOrDefault(r => r.Id == "project");
        var policy = run is null ? job.Discovery.ProposedPolicy : job.Policies[run.PolicyHash];
        var files = new ProjectFiles(policy);
        if (run is null && files.Snapshot() != job.Snapshot) throw new ArgumentException("Project changed between approval and first execution; rediscover.");
        var runner = new DevelopmentRunner(session, files, model, maxInput, maxObservation, job.Discovery.ExtraCommands);
        if (run is null) runner.Start("project", job.Goal);
        return await runner.RunAsync("project", token);
    }
    private FileStream Lease() => new(Path.Combine(DirectoryPath, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private void Write(ProjectDevelopmentJob job)
    {
        string temp = JobPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(job, Json)); File.Move(temp, JobPath, true);
    }
}
