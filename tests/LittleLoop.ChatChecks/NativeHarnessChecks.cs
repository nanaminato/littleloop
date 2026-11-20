using System.Text.Json;
using System.Threading.Channels;
using LittleLoop.Tools;

static class NativeHarnessChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string directory = Directory.CreateTempSubdirectory("littleloop-harness-check-").FullName;
        try
        {
            string project = Directory.CreateDirectory(Path.Combine(directory, "project")).FullName;
            string session = Path.Combine(directory, "session");
            var protocol = new Protocol();
            var harness = new FixtureHarness(project, protocol);
            var firstId = Guid.NewGuid();
            var first = await harness.SendAsync(session, firstId, "检查测试用例", null, default);
            check(first is { Status: "returned", Report: "fixture report", Tools: [{Kind: "commandExecution", Status: "failed"}] } &&
                first.Tools[0].Detail.Contains("exitCode=1") && first.Tools[0].Detail.Contains("fixture test failed"),
                "native Codex turn carries failed test evidence independently of its report");
            var cached = await harness.SendAsync(session, firstId, "检查测试用例", null, default);
            check(cached.Report == first.Report && protocol.Calls.Count(m => m == "turn/start") == 1,
                "replayed dispatch reads the durable receipt without sending another turn");
            var restarted = new FixtureHarness(project, protocol);
            protocol.RequestGrant = true;
            var next = await restarted.SendAsync(session, Guid.NewGuid(), "继续修复测试", null, default);
            check(next.SessionId == first.SessionId && protocol.Calls.Contains("thread/resume") &&
                protocol.Prompts.SequenceEqual(["检查测试用例", "继续修复测试"]),
                "new worker resumes native Codex thread with the Bot's next instruction");
            check(protocol.Declined && next.Tools.Any(t => t.Kind.EndsWith("/requestApproval") && t.Status == "declined"),
                "native permission requests remain ungranted and return a diagnostic to the Bot");
            protocol.RequestGrant = false;
            var stoppedId = Guid.NewGuid();
            protocol.WaitForStop = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var active = restarted.SendAsync(session, stoppedId, "运行直到取消", null, deadline.Token);
            await protocol.Started.Task.WaitAsync(deadline.Token);
            check(restarted.GetStatus(session, stoppedId).Status == "running", "native status reflects the in-flight dispatch");
            restarted.Interrupt(session, stoppedId);
            var stopped = await active;
            check(stopped.Status == "interrupted" && protocol.Calls.Contains("turn/interrupt"),
                "explicit cancellation waits for native Codex's interrupt receipt");
            var lost = Guid.NewGuid();
            var journal = new HarnessTurn(session, lost, null);
            journal.Opened(first.SessionId!);
            journal.Sending();
            journal.Tool("work", "commandExecution", "failed", "before disconnect");
            int calls = protocol.Calls.Count;
            var recovered = NativeBotHarness.RecoverReceipt(session, lost);
            var uncertain = await restarted.SendAsync(session, lost, "unacknowledged", null, default);
            check(recovered is { Status: "uncertain", Tools.Length: 1 } && uncertain.Status == "uncertain" && protocol.Calls.Count == calls,
                "recovery retains pre-crash tool results and never resends a submitted prompt");
            var corrupt = Guid.NewGuid();
            File.WriteAllText(Path.Combine(session, $"{corrupt:N}.json"), "{truncated");
            check(NativeBotHarness.RecoverReceipt(session, corrupt)?.Status == "uncertain" &&
                harness.GetStatus(session, corrupt).Status == "uncertain" &&
                NativeBotHarness.RecoverReceipt(session, firstId)?.Status == "returned" &&
                NativeBotHarness.RecoverReceipt(session, Guid.NewGuid()) is null,
                "offline recovery distinguishes unreadable, completed and never-submitted dispatches");

            var disconnect = new Protocol { DisconnectAfterToolStart = true };
            var broken = await new FixtureHarness(project, disconnect).SendAsync(Path.Combine(directory, "broken"),
                Guid.NewGuid(), "执行后连接断开", null, default);
            check(broken is { Status: "uncertain", Tools: [{ Kind: "commandExecution", Status: "inProgress" }] }
                && disconnect.Calls.Contains("turn/interrupt"),
                "a disconnect after native command startup retains the pending tool and requests interruption");

            var acp = new HarnessTurn(session, Guid.NewGuid(), null);
            var names = new Dictionary<string, string>();
            DeepSeekBotHarness.ObserveTool(acp, names, JsonSerializer.SerializeToElement(new
                { sessionUpdate = "tool_call", toolCallId = "1", title = "read", status = "in_progress" }));
            DeepSeekBotHarness.ObserveTool(acp, names, JsonSerializer.SerializeToElement(new
                { sessionUpdate = "tool_call_update", toolCallId = "1", rawOutput = "no such file", status = "failed" }));
            check(acp.Read()!.Tools is [{ Kind: "read", Status: "failed", Detail: "no such file" }],
                "ACP tool update preserves the native tool identity, failure and diagnostic");

            if (Environment.GetEnvironmentVariable("LITTLELOOP_TEST_NATIVE_ACP") is { Length: > 0 } launcher)
                await LiveDeepSeek(launcher, directory, project, check);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task LiveDeepSeek(string launcher, string directory, string project, Action<bool, string> check)
    {
        string[] args = JsonSerializer.Deserialize<string[]>(Environment.GetEnvironmentVariable("LITTLELOOP_TEST_NATIVE_ACP_ARGS") ?? "[]")!;
        var adapter = new DeepSeekAcpClient(launcher, args, Environment.GetEnvironmentVariable("LITTLELOOP_TEST_NATIVE_ACP_HOME"),
            Environment.GetEnvironmentVariable("LITTLELOOP_TEST_NATIVE_ACP_ELECTRON") == "1", project, TimeSpan.FromSeconds(90));
        var session = Path.Combine(directory, "live-acp");
        string marker = "sample-" + Guid.NewGuid().ToString("N")[..10];
        await File.WriteAllTextAsync(Path.Combine(project, "fixture.txt"), "SAMPLE_CODE=" + marker);
        var one = await new DeepSeekBotHarness(adapter).SendAsync(session, Guid.NewGuid(),
            "Use your read-only search tool to find SAMPLE_CODE in fixture.txt in the working directory. Return the exact code.",
            null, default);
        check(one.Status == "returned" && one.Report.Contains(marker) && one.Tools.Any(t => t.Status == "completed"),
            "live ACP session/new reads a temporary fixture and returns native tool evidence: " + one.Status + " " + one.Report);
        var two = await new DeepSeekBotHarness(adapter).SendAsync(session, Guid.NewGuid(),
            "Return only the sample code from the previous turn. No tool use.", null, default);
        check(two.Status == "returned" && two.SessionId == one.SessionId && two.Report.Contains(marker),
            "live ACP session/resume recalls the previous turn's random sample: " + two.Status + " " + two.Report);
    }

    private sealed class FixtureHarness(string project, Protocol client) : NativeBotHarness
    {
        public override string Name => "codex";
        public override string ProjectRoot => project;
        protected override async Task<HarnessReceipt> Run(string directory, string workspace, bool isolated, string instruction,
            HarnessTurn turn, CancellationToken token)
        {
            await CodexBotHarness.SendProjectTurn(client, directory, workspace, instruction, turn, token);
            return turn.Read()!;
        }
    }

    private sealed class Protocol : IAgentSessionClient
    {
        private readonly string thread = Guid.NewGuid().ToString();
        private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>();
        private string turn = "";
        public List<string> Calls { get; } = [];
        public List<string> Prompts { get; } = [];
        public bool WaitForStop { get; set; }
        public bool RequestGrant { get; set; }
        public bool Declined { get; private set; }
        public bool DisconnectAfterToolStart { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
        {
            Calls.Add(method);
            var args = JsonSerializer.SerializeToElement(parameters);
            if (args.TryGetProperty("approvalPolicy", out _) || args.TryGetProperty("sandbox", out _) ||
                args.TryGetProperty("permissions", out _)) throw new Exception("Codex deployment settings unexpectedly overridden.");
            object response;
            switch (method)
            {
                case "thread/start":
                case "thread/resume":
                    if (method == "thread/resume" && args.GetProperty("threadId").GetString() != thread)
                        throw new Exception("Wrong resumed Codex thread");
                    response = new { thread = new { id = thread } };
                    break;
                case "turn/start":
                    turn = Guid.NewGuid().ToString();
                    Prompts.Add(args.GetProperty("input")[0].GetProperty("text").GetString()!);
                    if (RequestGrant) events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { id = 100, method = "item/commandExecution/requestApproval",
                        @params = new { threadId = thread, turnId = turn } }));
                    if (WaitForStop) Started.TrySetResult();
                    else if (DisconnectAfterToolStart)
                    {
                        events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { method = "item/started", @params = new {
                            threadId = thread, turnId = turn, item = new { type = "commandExecution", id = "execution", command = "fixture" } } }));
                        events.Writer.Complete();
                    }
                    else Complete("completed", [new { id = "test", type = "commandExecution", status = "completed",
                        exitCode = 1, aggregatedOutput = "fixture test failed" }, new { type = "agentMessage", text = "fixture report" }]);
                    response = new { turn = new { id = turn } };
                    break;
                case "turn/interrupt":
                    Complete("interrupted", []);
                    response = new { };
                    break;
                default: throw new Exception(method);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(response));
        }

        private void Complete(string status, object[] items) => events.Writer.TryWrite(JsonSerializer.SerializeToElement(new
            { method = "turn/completed", @params = new { threadId = thread, turn = new { id = turn, status, items } } }));
        public Task<JsonElement> NextAsync(CancellationToken token) => events.Reader.ReadAsync(token).AsTask();
        public Task ReplyAsync(JsonElement id, object result, CancellationToken token)
        {
            Declined = id.GetInt32() == 100 && JsonSerializer.SerializeToElement(result).GetProperty("decision").GetString() == "decline";
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
