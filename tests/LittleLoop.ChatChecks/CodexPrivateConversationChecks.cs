using System.Text.Json;
using LittleLoop.Tools;

static class CodexPrivateConversationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string folder = Directory.CreateTempSubdirectory("littleloop-native-codex-").FullName;
        string cwd = Directory.CreateDirectory(Path.Combine(folder, "workspace")).FullName;
        string thread = Guid.NewGuid().ToString();
        var client = new Client(thread, cwd);
        var input = new BotInput("current question") { ConversationWorkId = Guid.NewGuid(),
            History = [new("from the previous client", "first", false), new("quoted", "second", true)] };
        try
        {
            string first = await CodexPrivateConversation.Run(client, input, folder, cwd, default);
            check(first.StartsWith("Native answer") && first.Contains("1 次文件补丁") && client.OpenMethod == "thread/start" &&
                client.Prompt.GetProperty("history").GetArrayLength() == 2 && client.Rejected &&
                client.MessageId == input.ConversationWorkId.ToString() && File.ReadAllText(Path.Combine(folder, "thread.json")).Contains(thread),
                "Codex starts a native turn with current context, declines expanded authority and reports one verified patch");
            var restarted = new Client(thread, cwd);
            await CodexPrivateConversation.Run(restarted, input, folder, cwd, default);
            check(restarted.OpenMethod == "thread/resume" && restarted.ResumeId == thread &&
                restarted.Prompt.GetProperty("history").GetArrayLength() == 1 &&
                restarted.Prompt.GetProperty("history")[0].GetProperty("quoted").GetBoolean(),
                "restarted Codex resumes the same private thread with only the explicitly quoted exchange");
            try
            {
                await CodexPrivateConversation.Run(new Client(thread, cwd, true), input, folder, cwd, default);
                check(false, "unexpected workspace permissions must prevent native turn startup");
            }
            catch (ConversationProviderException error)
            {
                check(error.Reason == ConversationFailure.PermissionDenied, "unexpected workspace permissions prevent native turn startup");
            }
            var textClient = new Client(Guid.NewGuid().ToString(), cwd);
            check(await CodexPrivateConversation.WindowsSandboxMissing(textClient, default),
                "native Windows readiness identifies a missing sandbox before sending a user prompt");
            await CodexPrivateConversation.Run(textClient, input, folder, cwd, default, textOnly: true);
            check(textClient.OpenMethod == "thread/start" && textClient.NoEnvironment &&
                textClient.Prompt.GetProperty("history").GetArrayLength() == 2,
                "switching to native text mode boots a new thread with scoped history and no environment tools");
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Client(string thread, string cwd, bool mismatched = false) : IAgentSessionClient
    {
        private readonly Queue<JsonElement> events = new();
        public JsonElement Prompt { get; private set; }
        public string? OpenMethod { get; private set; }
        public string? ResumeId { get; private set; }
        public string? MessageId { get; private set; }
        public bool Rejected { get; private set; }
        public bool NoEnvironment { get; private set; }

        public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            object result;
            switch (method)
            {
                case "windowsSandbox/readiness":
                    result = new { status = "notConfigured" };
                    break;
                case "config/read":
                    result = new { config = new { mcp_servers = new { fixture = new { enabled = true } } } };
                    break;
                case "thread/start" or "thread/resume":
                    OpenMethod = method;
                    if (p.TryGetProperty("threadId", out var id)) ResumeId = id.GetString();
                    var config = p.GetProperty("config");
                    if (p.TryGetProperty("sandbox", out var sandbox) && sandbox.GetString() == "read-only")
                    {
                        result = new { thread = new { id = thread }, runtimeWorkspaceRoots = new[] { cwd },
                            approvalPolicy = "never", sandbox = new { type = "readOnly", networkAccess = false } };
                        break;
                    }
                    string profile = p.GetProperty("permissions").GetString()!;
                    var bounds = config.GetProperty("permissions").GetProperty(profile);
                    if (bounds.GetProperty("filesystem").TryGetProperty(":root", out _) ||
                        bounds.GetProperty("filesystem").GetProperty(cwd).GetProperty(".").GetString() != "write" ||
                        bounds.GetProperty("network").GetProperty("enabled").GetBoolean() ||
                        config.GetProperty("mcp_servers").GetProperty("fixture").GetProperty("enabled").GetBoolean())
                        throw new Exception("Codex profile did not isolate workspace capabilities.");
                    result = new { thread = new { id = thread }, activePermissionProfile = new { id = mismatched ? ":workspace" : profile },
                        runtimeWorkspaceRoots = new[] { cwd }, approvalPolicy = "never", sandbox = new { type = "workspaceWrite", networkAccess = false } };
                    break;
                case "turn/start":
                    const string turn = "turn";
                    MessageId = p.GetProperty("clientUserMessageId").GetString();
                    NoEnvironment = p.TryGetProperty("environments", out var env) && env.GetArrayLength() == 0;
                    using (var doc = JsonDocument.Parse(p.GetProperty("input")[0].GetProperty("text").GetString()!))
                        Prompt = doc.RootElement.Clone();
                    events.Enqueue(JsonSerializer.SerializeToElement(new { id = 12, method = "item/commandExecution/requestApproval",
                        @params = new { threadId = thread, turnId = turn } }));
                    var patch = new { id = "patch", type = "fileChange", status = "completed", changes = new[] { new { path = "message.txt" } } };
                    var final = new { id = "final", type = "agentMessage", phase = "final_answer", text = "Native answer" };
                    if (!NoEnvironment)
                        events.Enqueue(JsonSerializer.SerializeToElement(new { method = "item/completed",
                            @params = new { threadId = thread, turnId = turn, item = patch } }));
                    events.Enqueue(JsonSerializer.SerializeToElement(new { method = "turn/completed",
                        @params = new { threadId = thread, turn = new { id = turn, status = "completed", items = NoEnvironment
                            ? new object[] { final } : [patch, final] } } }));
                    result = new { turn = new { id = turn } };
                    break;
                default: throw new InvalidOperationException(method);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        public Task<JsonElement> NextAsync(CancellationToken token) => Task.FromResult(events.Dequeue());
        public Task ReplyAsync(JsonElement id, object result, CancellationToken token)
        {
            Rejected = id.GetInt32() == 12 && JsonSerializer.SerializeToElement(result).GetProperty("decision").GetString() == "decline";
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
