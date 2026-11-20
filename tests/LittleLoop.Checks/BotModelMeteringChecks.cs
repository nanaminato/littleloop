using System.Net;
using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class BotModelMeteringChecks
{
    private static readonly BotDecisionContext Conversation = new(new(BotEventKind.UserMessage, "请问现在的状态？"), [], [], [], []);
    private static readonly SemanticModelConfig Config = new("chat-completions", "http://127.0.0.1/model", "fixture-model");

    public static async Task Run()
    {
        var calls = new List<BotModelCall>();
        var context = Conversation with { ObserveModelCall = calls.Add };
        using var responses = new Responses(
            (HttpStatusCode.OK, ChatCompletion("""{"action":"unknown"}""", """
                {"prompt_tokens":10,"completion_tokens":3,"total_tokens":13,"prompt_cache_hit_tokens":2,"prompt_cache_miss_tokens":8}
                """)),
            (HttpStatusCode.OK, ChatCompletion("""{"action":"reply","message":"可用。"}""", """
                {"prompt_tokens":12,"completion_tokens":4,"total_tokens":16,"prompt_tokens_details":{"cached_tokens":0}}
                """)),
            (HttpStatusCode.OK, ChatCompletion("""{"action":"reply","message":"可用。"}""",
                """{"prompt_tokens":12,"completion_tokens":4}""", finishReason: "length")),
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.OK, ChatCompletion("""{"action":"reply","message":"可用。"}""",
                """{"prompt_tokens":-1,"completion_tokens":"bad"}""")),
            (HttpStatusCode.OK, ChatCompletion("""{"action":"unknown"}""", "{}")),
            (HttpStatusCode.OK, ChatCompletion("""{"action":"unknown"}""", "{}")));
        using var client = new HttpSemanticModelClient(Config, responses);
        var engine = new ModelBotDecisionEngine(client);
        var result = await engine.DecideWithSourceAsync(context, default);
        Require(result.Decision.Action == BotAction.Reply && result.Source.Engine == "semantic-model" &&
            Metered(calls).SequenceEqual([
                new("invalid-decision", "served-model", new(10, 3, 13, 2, 8)),
                new("accepted", "served-model", new(12, 4, 16, 0))
            ]) && calls[0].Draft?.Raw == """{"action":"unknown"}""" && calls[0].Error is not null &&
            calls[1].Draft?.Raw == """{"action":"reply","message":"可用。"}""" && calls[1].Error is null,
            "rejected draft and accepted correction each keep reported counts and draft diagnostics");

        calls.Clear();
        await ProviderReject(() => engine.DecideAsync(context, default), "incomplete-response");
        await ProviderReject(() => engine.DecideAsync(context, default), "http-429");
        Require(Metered(calls).SequenceEqual([
            new("request-failed", Usage: new(12, 4)),
            new("request-failed")
        ]), "failed requests include only usage available on the response");

        calls.Clear();
        await engine.DecideAsync(context, default);
        Require(Metered(calls).SequenceEqual([new("accepted", "served-model")]), "malformed usage does not reject a valid action");

        calls.Clear();
        await InvalidReject(() => engine.DecideAsync(context, default));
        Require(calls.Count == 2 && calls.All(c => c.Outcome == "invalid-decision") && responses.Requests == 7,
            "exhausted formatting retries count every attempted request");
        calls.Clear();
        await InvalidReject(() => new ModelBotDecisionEngine(client, maxInputCharacters: 1).DecideAsync(context, default));
        Require(calls.Count == 0 && responses.Requests == 7, "host rejection before dispatch consumes no model requests");

        using var ollamaResponse = new Responses((HttpStatusCode.OK, """
            {"model":"served-model","message":{"content":"{\"action\":\"reply\",\"message\":\"ok\"}"},
             "done":true,"done_reason":"stop","prompt_eval_count":15,"prompt_eval_cached_count":3,"eval_count":9}
            """));
        using var ollama = new HttpSemanticModelClient(Config with { Protocol = "ollama" }, ollamaResponse);
        await new ModelBotDecisionEngine(ollama).DecideAsync(context, default);
        Require(Metered(calls).SequenceEqual([new("accepted", "served-model", new(15, 9, CachedInputTokens: 3))]),
            "Ollama counts are kept and unreported totals remain unknown");
        Console.WriteLine("PASS model decision metering with retries, errors, partial usage and a pre-request rejection");
    }

    private static IEnumerable<BotModelCall> Metered(IEnumerable<BotModelCall> calls) =>
        calls.Select(call => new BotModelCall(call.Outcome, call.Model, call.Usage));

    private static string ChatCompletion(string content, string usage, string finishReason = "stop")
    {
        using var usageDocument = JsonDocument.Parse(usage);
        return JsonSerializer.Serialize(new
        {
            model = "served-model",
            choices = new[] { new { finish_reason = finishReason, message = new { content } } },
            usage = usageDocument.RootElement
        });
    }

    private static async Task ProviderReject(Func<Task<BotDecision>> action, string expected)
    {
        try { await action(); }
        catch (SemanticModelException error) when (error.Code == expected) { return; }
        throw new InvalidOperationException($"Expected model request failure {expected}.");
    }

    private static async Task InvalidReject(Func<Task<BotDecision>> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected a decision validation failure.");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + check);
    }

    private sealed class Responses(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (status, body) = responses[Requests++];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
