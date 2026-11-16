using System.Text.Json;
using System.Threading.Channels;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class AgentChecks
{
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    public static async Task Run()
    {
        string parent = Path.Combine(Path.GetTempPath(), "littleloop-agent-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(parent, "project"); Directory.CreateDirectory(project); File.WriteAllText(Path.Combine(project, "README.md"), "Device acceptance pending.");
        var policy = new AgentHostPolicy("unused", project, Commands: [new("read docs", project)]);
        var request = Element(new { threadId = "thread-1", turnId = "turn-1", command = "read docs", cwd = project });
        Check(AgentApproval.Decide(policy, "thread-1", "turn-1", "item/commandExecution/requestApproval", request) == "accept", "exact pre-granted command can be autonomously approved");
        Check(AgentApproval.Decide(policy, "other", "turn-1", "item/commandExecution/requestApproval", request) == "decline", "approval is bound to thread and turn");
        Check(AgentApproval.Decide(policy, "thread-1", "turn-1", "item/fileChange/requestApproval", request) == "user", "ungranted file permissions are escalated to user");
        Check(AgentApproval.Decide(policy with { ApprovalMode = "wait" }, "thread-1", "turn-1", "item/commandExecution/requestApproval", request) == "wait", "user wait instruction disables autonomous approval");
        var network = Element(new { threadId = "thread-1", turnId = "turn-1", command = "read docs", cwd = project, networkApprovalContext = new { host = "example.test" } });
        Check(AgentApproval.Decide(policy, "thread-1", "turn-1", "item/commandExecution/requestApproval", network) == "user", "exact command does not implicitly approve additional network access");

        var host = new AgentDelegationHost(Path.Combine(parent, "host")); host.Create(policy);
        var fake = new FakeAgent(project, repairFirst: true, approval: true);
        var first = await host.RunAsync("Prepare read-only matrix", CancellationToken.None, fake);
        Check(first.Status == "completed" && first.Turns.Length == 2 && first.Turns[0].Issues.Length > 0 && fake.Methods[0] == "thread/start", "agent results drive bounded automatic repair instead of accepting incomplete matrix");
        Check(fake.Replies.Count == 1 && fake.Replies[0].GetProperty("decision").GetString() == "accept", "authorized request is answered through real RPC response shape");
        var memories = new LearningSession(Path.Combine(parent, "host", "learning.json")).Read();
        Check(memories.Episodes.Any(e => e.Feedback.Any(f => !f.Succeeded)) && memories.Episodes.Any(e => e.Feedback.Any(f => f.Succeeded)), "agent reports and independent validation feedback accumulate in core memory");
        var resumed = new FakeAgent(project);
        var second = await host.RunAsync("Review matrix prerequisites", CancellationToken.None, resumed);
        Check(second.ThreadId == first.ThreadId && resumed.Methods[0] == "thread/resume" && second.Turns.Length == 3, "later task restores same session and preserves previous turns");

        string waitingDir = Path.Combine(parent, "waiting"); var waiting = new AgentDelegationHost(waitingDir); waiting.Create(policy with { ApprovalMode = "user" });
        var interactive = new FakeAgent(project, approval: true);
        var pending = waiting.RunAsync("Prepare matrix", CancellationToken.None, interactive);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (waiting.Read().Status != "awaiting-approval") await Task.Delay(20, deadline.Token);
        using var approval = JsonDocument.Parse(File.ReadAllText(Path.Combine(waitingDir, "approval.json")));
        string nonce = approval.RootElement.GetProperty("nonce").GetString()!;
        bool rejected = false; try { waiting.Answer("stale", "accept"); } catch (ArgumentException) { rejected = true; }
        Check(rejected && interactive.Replies.Count == 0, "stale approval cannot answer or execute a pending request");
        waiting.Answer(nonce, "decline"); await pending;
        Check(interactive.Replies.Single().GetProperty("decision").GetString() == "decline", "user decision resumes the specific waiting server request");

        string refuseDir = Path.Combine(parent, "refuse"); var refusing = new AgentDelegationHost(refuseDir); refusing.Create(policy);
        // Separate core role verification uses its ordinary boundary mechanism.
        var roleSession = new LearningSession(Path.Combine(parent, "role.json")); roleSession.Initialize(new("read-only-role", .5m, [], [new("no-agent", "agent.report.received", "No delegation") ]));
        var option = new Proposal("delegate", ChoiceKind.Act, "Delegate", 0, .1m, "test", "agent.delegate", 1) { Meaning = new("test", ["agent.report.received"], [], []) };
        Check(roleSession.Begin(new("role", "test", new() { ["scope"] = "agent" }, [new("report", "agent.report.received", 1, true)], [option], [], [], 1, ["agent.delegate"])).Trace.Decision.Selected.Proposal.Kind != ChoiceKind.Act,
            "core role can refuse external agent delegation");
        var guarded = new AgentDelegationHost(Path.Combine(parent, "guarded")); guarded.Create(policy with { Role = new("guarded", .5m, [], [new("no-delegate", "agent.report.received", "Not allowed")]) });
        var guardedClient = new FakeAgent(project);
        Check((await guarded.RunAsync("Prepare matrix", CancellationToken.None, guardedClient)).Status == "refused" && !guardedClient.Methods.Contains("turn/start"),
            "configured role refuses delegation before sending any agent task");
        var failed = new FakeAgent(project, disconnect: true); rejected = false;
        try { await refusing.RunAsync("Prepare matrix", CancellationToken.None, failed); } catch (IOException) { rejected = true; }
        Check(rejected && refusing.Read().Status == "needs-reconciliation", "lost execution response is retained as unknown, not auto-replayed");
        rejected = false; try { await refusing.RunAsync("Repeat", CancellationToken.None, new FakeAgent(project)); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "unknown agent execution blocks accidental resend");
        await BridgeChecks(parent);
        Check(AgentDelegationHost.ValidateMatrix(Matrix(true).Replace("README.md", "../secret.md"), project).Length > 0, "matrix evidence validation rejects path traversal");
    }
    private static async Task BridgeChecks(string parent)
    {
        string project = Path.Combine(parent, "code"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "code.cs"), "old"); File.WriteAllText(Path.Combine(project, "test.cs"), "protected");
        string host = Path.Combine(parent, "bridge"); Directory.CreateDirectory(host);
        string[] fixtureArgs = Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [typeof(AgentChecks).Assembly.Location, "--agent-command-fixture"] : ["--agent-command-fixture"];
        var policy = new DevelopmentPolicy(project, ["code.cs"], [new("test", Environment.ProcessPath!, fixtureArgs, true, ["test.cs"])], [".cs"]);
        var bridge = new AgentDevelopmentBridge(host, policy, null);
        async Task<JsonElement> Call(string tool, object args, string thread = "t") => Element(await bridge.InvokeAsync(Element(new { callId = Guid.NewGuid().ToString(), threadId = thread, turnId = "u", tool, arguments = args }), "t", "u", CancellationToken.None));
        var denied = await Call("project_read", new { path = "code.cs" }, "foreign");
        Check(!denied.GetProperty("success").GetBoolean(), "host tool rejects cross-thread requests");
        var hash = ProjectFiles.Hash(System.Text.Encoding.UTF8.GetBytes("old"));
        denied = await Call("project_patch", new { path = "code.cs", sha256 = hash, oldText = "old", newText = "new" });
        Check(!denied.GetProperty("success").GetBoolean() && File.ReadAllText(Path.Combine(project, "code.cs")) == "old", "host patch requires previous observed hash");
        await Call("project_read", new { path = "code.cs" });
        var failing = await Call("project_command", new { command = "test" });
        Check(!failing.GetProperty("success").GetBoolean(), "real failed test command is returned to agent");
        var patched = await Call("project_patch", new { path = "code.cs", sha256 = hash, oldText = "old", newText = "new" });
        Check(patched.GetProperty("success").GetBoolean() && File.ReadAllText(Path.Combine(project, "code.cs")) == "new", "dynamic tool performs actual hash-guarded patch through core runtime");
        Check(bridge.Validate().Any(i => i.Contains("Required test")), "agent completion cannot replace actual passing test receipts");
        var passing = await Call("project_command", new { command = "test" });
        Check(passing.GetProperty("success").GetBoolean() && bridge.Validate().Length == 0, "changed source snapshot allows retest after negative experience and verifies final success");
        var replayRequest = Element(new { callId = "replay", threadId = "t", turnId = "u", tool = "project_read", arguments = new { path = "code.cs" } });
        await bridge.InvokeAsync(replayRequest, "t", "u", CancellationToken.None);
        int count = new LearningSession(Path.Combine(host, "tools-learning.json")).Read().Episodes.Length;
        await bridge.InvokeAsync(replayRequest, "t", "u", CancellationToken.None);
        Check(new LearningSession(Path.Combine(host, "tools-learning.json")).Read().Episodes.Length == count, "repeated call ID returns durable result without executing twice");
        var waitingHost = new AgentDelegationHost(Path.Combine(parent, "tool-wait"));
        waitingHost.Create(new("unused", project, ApprovalMode: "wait", MaxRepairs: 0, AttachDocumentation: false, Development: policy));
        var toolAgent = new FakeAgent(project, dynamicTool: true);
        var pendingRun = waitingHost.RunAsync("Run bounded test", CancellationToken.None, toolAgent);
        string pendingFile = Path.Combine(parent, "tool-wait", "approval.json");
        for (int i = 0; i < 100 && !File.Exists(pendingFile); i++) await Task.Delay(20);
        using (var pending = JsonDocument.Parse(File.ReadAllText(pendingFile)))
            waitingHost.Answer(pending.RootElement.GetProperty("nonce").GetString()!, "decline");
        await pendingRun;
        Check(!toolAgent.Replies.Single().GetProperty("success").GetBoolean() &&
            new LearningSession(Path.Combine(parent, "tool-wait", "tools-learning.json")).Read().Episodes.Length == 0,
            "wait policy persists dynamic command approval and honors user decline without execution");
        bool pause = false;
        string suspendFolder = Path.Combine(parent, "tool-suspend");
        var suspendHost = new AgentDelegationHost(suspendFolder, (_, _) => { pause = true; return Task.CompletedTask; },
            _ => Task.FromResult(pause));
        suspendHost.Create(new("unused", project, ApprovalMode: "wait", MaxRepairs: 0, AttachDocumentation: false, Development: policy));
        var suspendedAgent = new FakeAgent(project, dynamicTool: true);
        var suspended = await suspendHost.RunAsync("Run bounded test", CancellationToken.None, suspendedAgent);
        Check(suspended.Status == "suspended" && suspendedAgent.Methods.Contains("turn/interrupt") && suspendedAgent.Replies.Count == 0,
            "pause while awaiting approval interrupts turn before pending tool executes");
        Check(new LearningSession(Path.Combine(suspendFolder, "tools-learning.json")).Read().Episodes.Length == 0 &&
            File.ReadAllText(Path.Combine(suspendFolder, "approval.json")).Contains("expired"),
            "pause retains tool budget and invalidates stale approval");
        pause = false;
        var resumedAgent = new FakeAgent(project);
        await suspendHost.RunAsync("Continue with additional constraints", CancellationToken.None, resumedAgent);
        Check(resumedAgent.Methods[0] == "thread/resume" && suspendHost.Read().Turns.Length == 2,
            "resume keeps same thread and cumulative turn budget");
        await Call("project_patch", new { path = "code.cs", sha256 = hash, oldText = "old", newText = "again" });
        denied = await Call("project_command", new { command = "ungranted" });
        Check(!denied.GetProperty("success").GetBoolean(), "dynamic command cannot select ungranted executable");
        var learning = new LearningSession(Path.Combine(host, "tools-learning.json")).Read();
        Check(learning.Episodes.Any(e => e.Feedback.Any(f => f.Succeeded)) && learning.Episodes.Any(e => e.Feedback.Any(f => !f.Succeeded)), "tool execution outcomes automatically accumulate positive and negative experience");
        File.WriteAllText(Path.Combine(project, "test.cs"), "tampered");
        bool stopped = false; try { await Call("project_read", new { path = "code.cs" }); } catch (IOException) { stopped = true; }
        Check(stopped, "protected test mutation stops dynamic execution");
    }
    private static string Matrix(bool full) => JsonSerializer.Serialize(new { summary = "Not yet device verified", questions = new[] { "Identify test hosts" },
        rows = (from platform in full ? new[] { "desktop", "android" } : new[] { "desktop" }
                from scenario in new[] { "install", "update", "uninstall" }
                select new { platform, scenario, status = "not-run", prerequisites = new[] { "test host" }, expectedEvidence = new[] { "operation receipt and unaffected module health" }, evidencePaths = new[] { "README.md" } }).ToArray() });
    private sealed class FakeAgent(string project, bool repairFirst = false, bool approval = false, bool disconnect = false, bool dynamicTool = false) : IAgentSessionClient
    {
        private readonly Channel<JsonElement> queue = Channel.CreateUnbounded<JsonElement>(); private int turn;
        public List<string> Methods { get; } = []; public List<JsonElement> Replies { get; } = [];
        public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
        {
            Methods.Add(method);
            if (method.StartsWith("thread/")) return Task.FromResult(Element(new { thread = new { id = "thread-1" } }));
            if (method == "turn/interrupt") return Task.FromResult(Element(new { }));
            turn++; string id = "turn-" + turn;
            if (disconnect) throw new IOException("lost response after submission");
            if (dynamicTool) queue.Writer.TryWrite(Element(new { id = 72, method = "item/tool/call",
                @params = new { callId = "command-1", threadId = "thread-1", turnId = id, tool = "project_command", arguments = new { command = "test" } } }));
            if (approval && turn == 1) queue.Writer.TryWrite(Element(new { id = 71, method = "item/commandExecution/requestApproval",
                @params = new { threadId = "thread-1", turnId = id, command = "read docs", cwd = project } }));
            queue.Writer.TryWrite(Element(new { method = "item/completed", @params = new { threadId = "thread-1", turnId = id, item = new { type = "agentMessage", text = Matrix(!repairFirst || turn > 1) } } }));
            queue.Writer.TryWrite(Element(new { method = "turn/completed", @params = new { threadId = "thread-1", turn = new { id, status = "completed" } } }));
            return Task.FromResult(Element(new { turn = new { id } }));
        }
        public Task ReplyAsync(JsonElement id, object result, CancellationToken token) { Replies.Add(Element(result)); return Task.CompletedTask; }
        public async Task<JsonElement> NextAsync(CancellationToken token) => await queue.Reader.ReadAsync(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
}
