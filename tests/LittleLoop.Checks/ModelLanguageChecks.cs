using System.Net;
using System.Text;
using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class ModelLanguageChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task Run()
    {
        var h = new Harness();
        string text = "Please tidy the mixed Windows newlines and trailing blanks in input.txt; keep the original and place the result in output.txt.";
        string reply = Candidate(text, "input.txt", "output.txt", "normalize");
        var handler = new Handler((_, _) => Task.FromResult(Envelope(reply)));
        using var client = new HttpSemanticModelClient(Config(), handler);
        var interpreter = new ModelGoalInterpreter(client);
        var dialogue = await NaturalLanguageGoals.SayAsync(h.Session, "model", text, interpreter);
        Check(dialogue.GoalId is not null && handler.Calls == 1 && !handler.Body!.Contains("\"tools\""), "model reply compiles a freely phrased goal without model tool access");
        using (var request = JsonDocument.Parse(handler.Body!))
            Check(request.RootElement.GetProperty("max_tokens").GetInt32() == 4096 && !request.RootElement.GetProperty("stream").GetBoolean(), "model request has explicit output bound and non-streaming JSON contract");
        var state = await h.Run();
        Check(state.Goals[0].Status == GoalStatus.Completed && File.ReadAllText(Path.Combine(h.Root, "output.txt")) == "first\nsecond\n", "model interpretation reaches real filesystem execution and verification");
        var meaning = dialogue.Turns[0].Understanding;
        Check(JsonSerializer.Serialize(ModelGoalInterpreter.Validate(null, text, meaning.ModelExchange!)) == JsonSerializer.Serialize(meaning) && handler.Calls == 1,
            "semantic replay uses recorded reply without network or tools");
        var normalized = state.Episodes.Single(e => e.Result!.ToolReceipt!.ToolId == "text.normalize-lines");
        h.Session.Feedback(new("review", normalized.Trace.Input.Id, null, false, ["file.created"], "reviewer", "Whitespace mattered for this document class."));
        string nextText = text.Replace("output.txt", "next.txt", StringComparison.Ordinal);
        using var secondClient = new HttpSemanticModelClient(Config(), new Handler((_, _) => Task.FromResult(Envelope(Candidate(nextText, "input.txt", "next.txt", "normalize")))));
        await NaturalLanguageGoals.SayAsync(h.Session, "next", nextText, new ModelGoalInterpreter(secondClient));
        var next = await h.Run();
        Check(next.Goals.Single(g => g.Goal.Id == "language:next").Status == GoalStatus.Refused && !File.Exists(Path.Combine(h.Root, "next.txt")),
            "actual execution feedback can veto a later model-derived plan");

        foreach (var bad in new[] {
            "not JSON", reply.Replace("output.txt", "invented.txt", StringComparison.Ordinal),
            reply.Replace("\"fields\":", "\"unknown\":true,\"fields\":", StringComparison.Ordinal),
            reply.Replace("\"mode\":[\"normalize\"]", "\"mode\":[\"normalize\"],\"Mode\":[\"preserve\"]", StringComparison.Ordinal) })
        {
            using var badClient = new HttpSemanticModelClient(Config(), new Handler((_, _) => Task.FromResult(Envelope(bad))));
            var rejected = await new ModelGoalInterpreter(badClient).InterpretAsync(null, text);
            Check(rejected.Questions[0].Field == "model", "untrusted malformed or ungrounded model reply cannot submit a goal");
        }
        var missing = JsonSerializer.Serialize(new SemanticCandidate([], [], [], []), Json);
        using var missingClient = new HttpSemanticModelClient(Config(), new Handler((_, _) => Task.FromResult(Envelope(missing))));
        var missingResult = await new ModelGoalInterpreter(missingClient).InterpretAsync(null, "Help me organize that document.");
        Check(missingResult.Questions.Select(q => q.Field).Contains("source") && missingResult.Questions.Any(q => q.Field == "output"), "host asks missing information even when model forgets questions");

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
        {
            var unavailable = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("server secrets must not be logged") }));
            using var badClient = new HttpSemanticModelClient(Config(), unavailable);
            var result = await new ModelGoalInterpreter(badClient).InterpretAsync(null, text);
            Check(result.ModelExchange!.Failure == "http-" + (int)status && result.ModelExchange.Response is null && unavailable.Calls == 1,
                "HTTP failure is bounded, sanitized and not automatically retried: " + (int)status);
        }
        using (var truncated = new HttpSemanticModelClient(Config(), new Handler((_, _) => Task.FromResult(Envelope(reply, "length")))))
            Check((await new ModelGoalInterpreter(truncated).InterpretAsync(null, text)).ModelExchange!.Failure == "incomplete-response", "truncated model output is not executable");
        using (var oversized = new HttpSemanticModelClient(Config() with { MaxResponseBytes = 1024 }, new Handler((_, _) => Task.FromResult(Envelope(new string('x', 4096))))))
            Check((await new ModelGoalInterpreter(oversized).InterpretAsync(null, text)).ModelExchange!.Failure == "response-too-large", "response byte cap is enforced");
        using (var timeout = new HttpSemanticModelClient(Config() with { TimeoutSeconds = 1 }, new Handler(async (_, token) => { await Task.Delay(5000, token); return Envelope(reply); })))
            Check((await new ModelGoalInterpreter(timeout).InterpretAsync(null, text)).ModelExchange!.Failure == "timeout", "model timeout becomes a durable clarification instead of execution");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var before = h.Session.Read().Revision;
            try { await NaturalLanguageGoals.SayAsync(h.Session, "cancel", text, interpreter, cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(h.Session.Read().Revision == before, "caller cancellation does not save partial model interpretation"); }
        }
        using (var ollama = new HttpSemanticModelClient(Config() with { Protocol = "ollama" }, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { model = "local-test", done = true, done_reason = "stop", message = new { content = reply } })) }))))
            Check((await new ModelGoalInterpreter(ollama).InterpretAsync(null, text)).Questions.Length == 0, "Ollama protocol uses the same host semantic guard");
        string limitedText = "规范化 input.txt，保存为 output.txt，最多 2 次工具";
        using (var limits = new HttpSemanticModelClient(Config(), new Handler((_, _) => Task.FromResult(Envelope(Candidate(limitedText, "input.txt", "output.txt", "normalize"))))))
            Check((await new ModelGoalInterpreter(limits).InterpretAsync(null, limitedText)).Questions.Any(q => q.Field == "budget"), "explicit user budget cannot be silently omitted by model");
        var noNetwork = new Handler((_, _) => Task.FromResult(Envelope(reply)));
        using (var bounded = new HttpSemanticModelClient(Config(), noNetwork))
            Check((await new ModelGoalInterpreter(bounded, maxInputCharacters: 10).InterpretAsync(null, text)).ModelExchange!.Failure == "input-too-large" && noNetwork.Calls == 0,
                "oversized conversation is not sent or silently truncated");
        try { HttpSemanticModelClient.Validate(Config() with { Endpoint = "http://remote.example/chat" }); throw new Exception("Insecure endpoint allowed"); }
        catch (ArgumentException) { Check(true, "non-loopback endpoints require TLS"); }

        var raced = new Harness();
        using (var racingClient = new HttpSemanticModelClient(Config(), new Handler((_, _) =>
        {
            NaturalLanguageGoals.Say(raced.Session, "other", "帮我整理一下文件");
            return Task.FromResult(Envelope(reply));
        })))
        {
            try { await NaturalLanguageGoals.SayAsync(raced.Session, "raced", text, new ModelGoalInterpreter(racingClient)); throw new Exception("Stale model response accepted"); }
            catch (ArgumentException) { Check(raced.Session.Read().Goals.Length == 0 && raced.Session.Read().Dialogues.Length == 1,
                "state changes during network call reject stale interpretation atomically"); }
        }
        string secretName = "LITTLELOOP_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(secretName, "fake-test-secret");
        try
        {
            using var credentialClient = new HttpSemanticModelClient(Config() with { ApiKeyEnvironment = secretName }, new Handler((request, _) =>
            {
                Check(request.Headers.Authorization?.Parameter == "fake-test-secret", "credential is sent only through authorization header");
                return Task.FromResult(Envelope("fake-test-secret"));
            }));
            var protectedResult = await new ModelGoalInterpreter(credentialClient).InterpretAsync(null, text);
            Check(protectedResult.ModelExchange!.Failure == "credential-in-response" && !JsonSerializer.Serialize(protectedResult).Contains("fake-test-secret"),
                "credential echo is rejected before persistent audit");
        }
        finally { Environment.SetEnvironmentVariable(secretName, null); }
    }
    private static SemanticModelConfig Config() => new("chat-completions", "https://model.example/chat/completions", "test-model");
    private static string Candidate(string text, string source, string output, string mode) => JsonSerializer.Serialize(new SemanticCandidate(
        new() { ["source"] = [source], ["output"] = [output], ["mode"] = [mode] }, [],
        [new("source", source, 0, text), new("output", output, 0, text), new("mode", mode, 0, text)], []), Json);
    private static HttpResponseMessage Envelope(string content, string finish = "stop") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { model = "test-model", choices = new[] { new { finish_reason = finish, message = new { content } } } }), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++; Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }
    private sealed class Harness
    {
        public string Root { get; }
        public LearningSession Session { get; }
        public WorkspaceToolPolicy Policy { get; }
        public Harness()
        {
            var parent = Path.Combine(Path.GetTempPath(), "littleloop-model-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(parent, "workspace"); Directory.CreateDirectory(Root);
            Session = new(Path.Combine(parent, "state.json")); Session.Initialize(); Session.EnableScheduling(new(2, 100));
            Policy = new(Root, WorkspaceTools.List().Select(t => t.Id).ToArray());
            File.WriteAllText(Path.Combine(Root, "input.txt"), "first  \r\nsecond\t\r\n");
        }
        public Task<LearningState> Run() => new GoalOrchestrator(Session, Policy).RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
}

