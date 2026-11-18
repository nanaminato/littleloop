using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

sealed class DeepSeekChatAdapter(string launcher, string[] arguments, string? harnessHome, bool electron,
    string project, TimeSpan timeout, AgentHostPolicy? development = null, string provider = ChatStore.DeepSeekBotName) : IChatRobotAdapter
{
    public string Name => provider;
    public bool SupportsApproval => development is not null;
    public string ProjectRoot => project;

    public Task<RobotOutcome> Review(string prompt, string folder, Func<CancellationToken, Task<bool>>? shouldYield, CancellationToken token)
    {
        if (SupportsApproval) throw new InvalidOperationException("The reviewer must have no development grant.");
        return Execute(new(prompt), folder, shouldYield, token);
    }

    public async Task<string> Answer(BotInput input, CancellationToken token)
    {
        var result = await Execute(input, null, null, token);
        return result.Status == "completed" ? result.Report! : throw new IOException("DeepSeek did not complete the answer.");
    }

    public async Task<RobotOutcome> Run(BotWork work, string folder,
        Func<AgentApprovalRequest, CancellationToken, Task> onApproval, CancellationToken token,
        Func<CancellationToken, Task<bool>>? shouldYield = null)
    {
        if (development is null) return await Execute(work.Input, folder, shouldYield, token);
        var host = new AgentDelegationHost(folder, onApproval, shouldYield, transport: "deepseek-acp");
        if (!File.Exists(Path.Combine(folder, "agent.json"))) host.Create(development);
        var previous = host.Read();
        if (previous.Policy.Development is not { } stored || development.Development is not { } current ||
            new ProjectFiles(stored).PolicyHash != new ProjectFiles(current).PolicyHash)
            return new("needs-reconciliation", "项目授权已变化，请核对旧任务后按当前授权新建任务。");
        bool resuming = previous.Status == "suspended";
        var state = await host.RunAsync(work.Input.Text, token, new DeepSeekDevelopmentSession(
            Path.Combine(folder, "acp"), (prompt, ct) => Execute(new(prompt), Path.Combine(folder, "acp"), shouldYield, ct), shouldYield, resuming));
        var report = state.Turns.LastOrDefault()?.Report;
        return new(state.Status == "needs-revision" ? "waiting-input" : state.Status, report);
    }

    private async Task<RobotOutcome> Execute(BotInput input, string? folder,
        Func<CancellationToken, Task<bool>>? shouldYield, CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        if (shouldYield is not null && await shouldYield(token)) return new("suspended");

        // The operator-provisioned launcher supplies the restricted ACP composition.
        var start = new ProcessStartInfo(launcher)
        {
            WorkingDirectory = project,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ChatBot__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ChatFcm__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        if (harnessHome is not null) start.Environment["DSH_HOME"] = harnessHome;
        start.Environment["DSH_PERMISSION_MODE"] = "read-only";
        if (electron) start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not start DeepSeek ACP.");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await Request(1, "initialize", new { protocolVersion = 1, clientCapabilities = new { } });
            string? path = folder is null ? null : Path.Combine(folder, "deepseek-session.json");
            string? sessionId = path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<string>(AtomicSnapshot.ReadText(path)) ?? throw new InvalidDataException("Missing ACP session ID.")
                : null;
            var mcpServers = Array.Empty<object>();
            if (sessionId is null)
            {
                var created = await Request(2, "session/new", new { cwd = project, mcpServers });
                sessionId = created.GetProperty("sessionId").GetString();
                if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidDataException("Missing ACP session ID.");
                if (path is not null)
                {
                    Directory.CreateDirectory(folder!);
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(sessionId));
                    AtomicSnapshot.Replace(path + ".tmp", path);
                }
            }
            else await Request(2, "session/resume", new { sessionId, cwd = project, mcpServers });

            if (shouldYield is not null && await shouldYield(token))
            {
                await Request(4, "session/close", new { sessionId });
                return new("suspended");
            }
            var report = new StringBuilder();
            string prompt = folder is null && input.Source is { } citation
                ? input.Text + "\n\n引用资料：\n" + citation.Text : input.Text;
            await Send(new { jsonrpc = "2.0", id = 3, method = "session/prompt",
                @params = new { sessionId, prompt = new[] { new { type = "text", text = prompt } } } });
            Record("prompting");

            bool cancelled = false;
            var pending = Read();
            while (true)
            {
                if (!cancelled && shouldYield is not null)
                {
                    await Task.WhenAny(pending, Task.Delay(300, token));
                    token.ThrowIfCancellationRequested();
                    if (await shouldYield(token))
                    {
                        await Send(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId } });
                        cancelled = true;
                    }
                }
                if (!pending.IsCompleted && !cancelled && shouldYield is not null) continue;
                var message = await pending;
                if (Matches(message, 3))
                {
                    string? reason = Result(message).GetProperty("stopReason").GetString();
                    await Request(4, "session/close", new { sessionId });
                    Record(cancelled && reason is "cancelled" or "end_turn" ? "suspended" : "settled");
                    return reason switch
                    {
                        "cancelled" when cancelled => new("suspended"),
                        "end_turn" when cancelled => new("suspended"),
                        "end_turn" when report.Length > 0 => new("completed", report.ToString()),
                        _ when !cancelled => new("waiting-input", report.Length == 0 ? "DeepSeek 未返回完整结果。" : report.ToString()),
                        _ => new("needs-reconciliation")
                    };
                }
                if (message.TryGetProperty("method", out var method) && method.GetString() == "session/update")
                {
                    var parameters = message.GetProperty("params");
                    var update = parameters.GetProperty("update");
                    if (parameters.GetProperty("sessionId").GetString() == sessionId &&
                        update.GetProperty("sessionUpdate").GetString() == "agent_message_chunk" &&
                        update.GetProperty("content").TryGetProperty("text", out var content) &&
                        content.GetString() is { } chunk)
                        report.Append(chunk.AsSpan(0, Math.Min(chunk.Length, 12000 - report.Length)));
                }
                else await RejectRequest(message);
                pending = Read();
            }

            void Record(string stage)
            {
                if (folder is null) return;
                var receipt = Path.Combine(folder, "deepseek-execution.json");
                File.WriteAllText(receipt + ".tmp", JsonSerializer.Serialize(new { sessionId, stage }));
                AtomicSnapshot.Replace(receipt + ".tmp", receipt);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            try { await stderr; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        async Task Send(object message) =>
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);

        async Task<JsonElement> Read()
        {
            var line = await process.StandardOutput.ReadLineAsync(token) ?? throw new IOException("DeepSeek ACP disconnected.");
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }

        async Task<JsonElement> Request(int id, string method, object parameters)
        {
            await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            while (true)
            {
                var message = await Read();
                if (Matches(message, id)) return Result(message);
                await RejectRequest(message);
            }
        }

        async Task RejectRequest(JsonElement message)
        {
            if (!message.TryGetProperty("method", out var method) || !message.TryGetProperty("id", out var id)) return;
            if (method.GetString() == "session/request_permission")
                await Send(new { jsonrpc = "2.0", id, result = new { outcome = new { outcome = "cancelled" } } });
            else
                await Send(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported client method" } });
        }
    }

    private static bool Matches(JsonElement message, int id) =>
        !message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var candidate) &&
        candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt32(out var number) && number == id;

    private static JsonElement Result(JsonElement message) =>
        message.TryGetProperty("result", out var result) ? result : throw new IOException("DeepSeek ACP rejected the request.");
}
