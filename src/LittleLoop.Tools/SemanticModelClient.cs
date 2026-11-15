using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LittleLoop.Tools;

public sealed record SemanticModelConfig(string Protocol, string Endpoint, string Model,
    string? ApiKeyEnvironment = null, int TimeoutSeconds = 60, int MaxOutputTokens = 4096,
    int MaxResponseBytes = 131072, int MaxInputCharacters = 32768, int MaxGoalBudget = 12,
    string TokenLimitParameter = "max_tokens", string? ReasoningEffort = null,
    bool EnablePlanning = false, int MaxPlanningCalls = 8, int MaxObservationCharacters = 2048);
public sealed record SemanticModelReply(string Content, string Model);
public interface ISemanticModelClient
{
    string Provider { get; }
    string Model { get; }
    Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken);
}

/// <summary>One bounded request, no redirects, hidden retries or model tool calls.</summary>
public sealed class HttpSemanticModelClient : ISemanticModelClient, IDisposable
{
    private readonly SemanticModelConfig config;
    private readonly HttpClient client;
    private readonly string? key;
    public string Provider => config.Protocol + "@" + config.Endpoint;
    public string Model => config.Model;
    public HttpSemanticModelClient(SemanticModelConfig config, HttpMessageHandler? handler = null, string? projectDirectory = null)
    {
        Validate(config);
        this.config = config;
        key = config.ApiKeyEnvironment is null ? null : LocalModelSecrets.Read(config.ApiKeyEnvironment, projectDirectory);
        if (config.ApiKeyEnvironment is not null && string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Configured model credential is missing from the environment and project .env.local.");
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static void Validate(SemanticModelConfig c)
    {
        if (c.Protocol is not ("chat-completions" or "ollama") || string.IsNullOrWhiteSpace(c.Model) || c.Model.Length > 200 ||
            !Uri.TryCreate(c.Endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            c.TimeoutSeconds is < 1 or > 300 || c.MaxOutputTokens is < 128 or > 16384 ||
            c.MaxResponseBytes is < 1024 or > 1048576 || c.MaxInputCharacters is < 1024 or > 131072 ||
            c.MaxGoalBudget is < 1 or > 1000 || c.MaxPlanningCalls is < 1 or > 64 || c.MaxObservationCharacters is < 0 or > 8192 ||
            c.TokenLimitParameter is not ("max_tokens" or "max_completion_tokens") ||
            (c.ReasoningEffort is not null && c.ReasoningEffort is not ("none" or "low" or "medium" or "high")))
            throw new ArgumentException("Invalid semantic model configuration; use HTTPS or a loopback HTTP endpoint and bounded limits.");
    }
    public async Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var token = deadline.Token;
        var body = new Dictionary<string, object>
        {
            ["model"] = config.Model, ["stream"] = false,
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = input } }
        };
        if (config.Protocol == "ollama")
        { body["format"] = "json"; body["options"] = new { num_predict = config.MaxOutputTokens }; }
        else
        {
            body["response_format"] = new { type = "json_object" };
            body[config.TokenLimitParameter] = config.MaxOutputTokens;
            if (config.ReasoningEffort is not null) body["reasoning_effort"] = config.ReasoningEffort;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new SemanticModelException("http-" + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > config.MaxResponseBytes) throw new SemanticModelException("response-too-large");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (bytes.Length + count > config.MaxResponseBytes) throw new SemanticModelException("response-too-large");
                bytes.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(bytes.ToArray());
            var root = json.RootElement;
            string model = root.GetProperty("model").GetString() ?? throw new SemanticModelException("missing-model");
            JsonElement message;
            if (config.Protocol == "ollama")
            {
                if (!root.GetProperty("done").GetBoolean() || root.GetProperty("done_reason").GetString() != "stop")
                    throw new SemanticModelException("incomplete-response");
                message = root.GetProperty("message");
            }
            else
            {
                var choices = root.GetProperty("choices");
                if (choices.GetArrayLength() != 1 || choices[0].GetProperty("finish_reason").GetString() != "stop")
                    throw new SemanticModelException("incomplete-response");
                message = choices[0].GetProperty("message");
            }
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null && calls.GetArrayLength() > 0)
                throw new SemanticModelException("unexpected-tool-call");
            string? content = message.GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new SemanticModelException("empty-response");
            if (model.Length > 200 || string.IsNullOrWhiteSpace(model)) throw new SemanticModelException("invalid-model-name");
            if (key is not null && (content.Contains(key, StringComparison.Ordinal) || model.Contains(key, StringComparison.Ordinal)))
                throw new SemanticModelException("credential-in-response");
            return new(content, model);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new SemanticModelException("timeout"); }
        catch (HttpRequestException) { throw new SemanticModelException("transport-unavailable"); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new SemanticModelException("invalid-envelope"); }
    }
    public void Dispose() => client.Dispose();
}
public sealed class SemanticModelException(string code) : Exception(code)
{ public string Code { get; } = code; }
