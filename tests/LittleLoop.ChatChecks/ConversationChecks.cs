using System.Text.Json;
using LittleLoop.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class ConversationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        const string question = "解释一下第三列";
        const string reply = "第三列是收盘价。";
        var history = new ChatExchange("哪些列适合记录价格？", "日期、品种、收盘价。", true);
        var input = new BotInput(question) { History = [history] };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        JsonElement request = default;
        api.MapPost("/model", async (HttpRequest req) =>
        {
            using var document = await JsonDocument.ParseAsync(req.Body);
            request = document.RootElement.Clone();
            return Results.Json(new { model = "fixture", choices = new[] { new { finish_reason = "stop",
                message = new { content = JsonSerializer.Serialize(new { answer = reply }) } } } });
        });
        await api.StartAsync();
        using var model = new HttpSemanticModelClient(new("chat-completions", api.Urls.Single() + "/model", "fixture"));
        var actual = await ChatConversation.Answer(input, model, "unused", "unused", CancellationToken.None);
        var messages = request.GetProperty("messages");
        using var user = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        check(actual == reply && !request.TryGetProperty("tools", out _) && messages.GetArrayLength() == 2 &&
            user.RootElement.GetProperty("message").GetString() == question &&
            user.RootElement.GetProperty("history")[0].GetProperty("assistant").GetString() == history.Assistant,
            "real loopback text-model HTTP request includes prior chat exchange and returns plain reply without tools");

        var client = new SampleClient(reply);
        check(await ChatConversation.Sample(client, Path.GetTempPath(), question, CancellationToken.None) == reply &&
            client.SeedPrompt == "Reply with just OK." && client.SamplePrompt == question && client.DisableMcp && client.ServerDisabled,
            "only the fixed handshake enters the regular Codex turn; the user's message goes to tool-free sampling");
        bool declined = false;
        try { await ChatConversation.Sample(new SampleClient(null), Path.GetTempPath(), question, CancellationToken.None); }
        catch (InvalidDataException) { declined = true; }
        check(declined, "Codex sample attempting a tool or returning no text cannot report completion");
    }

    private sealed class SampleClient(string? answer) : IAgentSessionClient
    {
        public string? SeedPrompt { get; private set; }
        public string? SamplePrompt { get; private set; }
        public bool DisableMcp { get; private set; }
        public bool ServerDisabled { get; private set; }
        public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            object result;
            switch (method)
            {
                case "config/read": result = new { config = new { mcp_servers = new { local = new { enabled = true } } } }; break;
                case "thread/start":
                    ServerDisabled = !p.GetProperty("config").GetProperty("mcp_servers").GetProperty("local").GetProperty("enabled").GetBoolean();
                    result = new { thread = new { id = "thread" } }; break;
                case "thread/archive": result = new { }; break;
                case "turn/start":
                    SeedPrompt = p.GetProperty("input")[0].GetProperty("text").GetString();
                    result = new { turn = new { id = "turn" } }; break;
                case "thread/sample":
                    SamplePrompt = p.GetProperty("prompt").GetString();
                    DisableMcp = p.GetProperty("disableMcp").GetBoolean();
                    result = new { text = answer }; break;
                default: throw new InvalidOperationException(method);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
        public Task<JsonElement> NextAsync(CancellationToken token) => Task.FromResult(JsonSerializer.SerializeToElement(
            new { method = "turn/completed", @params = new { threadId = "thread", turn = new { id = "turn", status = "completed",
                items = new[] { new { type = "agentMessage" } } } } }));
        public Task ReplyAsync(JsonElement id, object result, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
