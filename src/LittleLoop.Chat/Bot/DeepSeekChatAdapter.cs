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
    public bool ConversationWorkspaceFiles { get; init; }

    public async Task<string> AnswerConversation(BotInput input, string sessionDir, CancellationToken token)
    {
        if (development is not null || input.Source is not null) throw new ConversationProviderException(ConversationFailure.Unsupported);
        using var conversation = new DeepSeekPrivateConversation(input, sessionDir, ConversationWorkspaceFiles);
        if (conversation.ExistingAnswer() is { } cached) return cached;
        try
        {
            var result = await Execute("", sessionDir, null, token, conversation);
            return result.Status == "completed" ? result.Report! : throw new ConversationProviderException(ConversationFailure.Interrupted);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException e) { throw new ConversationProviderException(ConversationFailure.TimedOut, e); }
        catch (IOException e) when (conversation.Submitted && e is not ConversationProviderException)
        { throw new ConversationProviderException(ConversationFailure.Interrupted, e); }
    }

    public Task<RobotOutcome> Review(string prompt, string folder, Func<CancellationToken, Task<bool>>? shouldYield, CancellationToken token)
    {
        if (SupportsApproval) throw new InvalidOperationException("The reviewer must have no development grant.");
        return Execute(prompt, folder, shouldYield, token);
    }

    public async Task<string> Answer(BotInput input, CancellationToken token)
    {
        var result = await Execute(AnswerPrompt(input), null, null, token);
        return result.Status == "completed" ? result.Report! : throw new IOException("DeepSeek did not complete the answer.");
    }

    internal static string AnswerPrompt(BotInput input)
    {
        if (input.Source is { } citation) return input.Text + "\n\n引用资料：\n" + citation.Text;
        return ChatConversation.Instructions + """

            当前会话由 DeepSeek Harness 的只读配置处理。只使用这次会话明确提供的读取能力，
            不能修改系统设置、提升权限或发送文件。根据 message 回答用户。
            """ + "\n" + ChatConversation.MessageContext(input);
    }

    public async Task<RobotOutcome> Run(BotWork work, string folder,
        Func<AgentApprovalRequest, CancellationToken, Task> onApproval, CancellationToken token,
        Func<CancellationToken, Task<bool>>? shouldYield = null)
    {
        if (development is null) return await Execute(work.Input.Text, folder, shouldYield, token);
        var host = new AgentDelegationHost(folder, onApproval, shouldYield, transport: "deepseek-acp");
        if (!File.Exists(Path.Combine(folder, "agent.json"))) host.Create(development);
        var previous = host.Read();
        if (previous.Policy.Development is not { } stored || development.Development is not { } current ||
            new ProjectFiles(stored).PolicyHash != new ProjectFiles(current).PolicyHash)
            return new("needs-reconciliation", "项目授权已变化，请核对旧任务后按当前授权新建任务。");
        bool resuming = previous.Status == "suspended";
        var state = await host.RunAsync(work.Input.Text, token, new DeepSeekDevelopmentSession(
            Path.Combine(folder, "acp"), (prompt, ct) => Execute(prompt, Path.Combine(folder, "acp"), shouldYield, ct), shouldYield, resuming));
        var report = state.Turns.LastOrDefault()?.Report;
        return new(state.Status == "needs-revision" ? "waiting-input" : state.Status, report);
    }

    private async Task<RobotOutcome> Execute(string prompt, string? folder,
        Func<CancellationToken, Task<bool>>? shouldYield, CancellationToken shutdown, DeepSeekPrivateConversation? conversation = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        if (shouldYield is not null && await shouldYield(token)) return new("suspended");

        // The operator-provisioned launcher supplies the restricted ACP composition.
        var start = new ProcessStartInfo(launcher)
        {
            WorkingDirectory = conversation?.Workspace ?? project,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (conversation is not null)
        {
            start.ArgumentList.Add("--patch");
            start.ArgumentList.Add(conversation.Overlay);
            start.Environment["LITTLELOOP_ACP_CHALLENGE"] = conversation.Challenge;
            start.Environment["LITTLELOOP_ACP_WORKSPACE"] = conversation.Workspace;
            start.Environment["LITTLELOOP_ACP_FILES"] = conversation.WorkspaceFiles ? "1" : "0";
        }
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ChatBot__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ChatFcm__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        if (harnessHome is not null) start.Environment["DSH_HOME"] = harnessHome;
        start.Environment["DSH_PERMISSION_MODE"] = conversation?.WorkspaceFiles == true ? "workspace-write" : "read-only";
        if (electron) start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not start DeepSeek ACP.");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            var initialized = await Request(1, "initialize", new { protocolVersion = 1, clientCapabilities = new { } });
            string? path = folder is null ? null : Path.Combine(folder, "deepseek-session.json");
            string? sessionId = path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<string>(AtomicSnapshot.ReadText(path)) ?? throw new InvalidDataException("Missing ACP session ID.")
                : null;
            var mcpServers = Array.Empty<object>();
            bool resumed = sessionId is not null;
            string cwd = start.WorkingDirectory;
            if (sessionId is null)
            {
                var created = await Request(2, "session/new", new { cwd, mcpServers });
                sessionId = created.GetProperty("sessionId").GetString();
                if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidDataException("Missing ACP session ID.");
                if (path is not null)
                {
                    Directory.CreateDirectory(folder!);
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(sessionId));
                    AtomicSnapshot.Replace(path + ".tmp", path);
                }
            }
            else
            {
                if (conversation is not null && !(initialized.TryGetProperty("agentCapabilities", out var agent) &&
                    agent.TryGetProperty("sessionCapabilities", out var sessions) && sessions.TryGetProperty("resume", out _)))
                    throw new ConversationProviderException(ConversationFailure.Unsupported);
                await Request(2, "session/resume", new { sessionId, cwd, mcpServers });
            }

            if (shouldYield is not null && await shouldYield(token))
            {
                await Request(4, "session/close", new { sessionId });
                return new("suspended");
            }
            var report = new StringBuilder();
            if (conversation is not null)
            {
                prompt = conversation.Prompt(sessionId, resumed);
                conversation.MarkSending();
            }
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
                    string? reason = Result(message, conversation).GetProperty("stopReason").GetString();
                    if (conversation is not null && reason == "end_turn" && report.Length > 0)
                    {
                        string answer = conversation.Finish(report.ToString());
                        try { await Request(4, "session/close", new { sessionId }); }
                        catch (Exception e) when (e is IOException or OperationCanceledException) { }
                        return new("completed", answer);
                    }
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
                    if (parameters.GetProperty("sessionId").GetString() == sessionId) conversation?.ObserveUpdate(update);
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
                if (Matches(message, id)) return Result(message, conversation);
                await RejectRequest(message);
            }
        }

        async Task RejectRequest(JsonElement message)
        {
            conversation?.ObserveGuard(message);
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

    private static JsonElement Result(JsonElement message, DeepSeekPrivateConversation? conversation)
    {
        if (message.TryGetProperty("result", out var result)) return result;
        if (conversation is not null && message.TryGetProperty("error", out var error))
            throw new ConversationProviderException(PrivateError(error, conversation.Submitted));
        throw new IOException("DeepSeek ACP rejected the request.");
    }

    internal static ConversationFailure PrivateError(JsonElement error, bool submitted)
    {
        if (error.ValueKind == JsonValueKind.Object)
        {
            var data = error.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Object ? value : error;
            foreach (var field in new[] { "httpStatus", "statusCode", "status", "code" })
                if (data.TryGetProperty(field, out var status))
                {
                    switch (status.ToString())
                    {
                        case "429": case "rate_limit_error": case "rate_limit_exceeded": return ConversationFailure.RateLimited;
                        case "408": case "504": case "ETIMEDOUT": return ConversationFailure.TimedOut;
                        case "401": case "403": return ConversationFailure.PermissionDenied;
                    }
                }
            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number))
            {
                if (number == -32601) return ConversationFailure.Unsupported;
                if (number == -32000) return ConversationFailure.PermissionDenied;
            }
        }
        return submitted ? ConversationFailure.Interrupted : ConversationFailure.Unavailable;
    }
}
