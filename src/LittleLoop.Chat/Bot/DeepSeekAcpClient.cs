using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LittleLoop.Core;

class DeepSeekAcpClient(string launcher, string[] arguments, string? harnessHome, bool electron, string project, TimeSpan timeout)
{
    public string ProjectRoot => project;
    public bool ConversationWorkspaceFiles { get; init; }
    public bool UseLauncherPermissions { get; init; }

    internal async Task<HarnessReceipt> Delegate(string instruction, string directory, string workspace, bool isolated,
        HarnessTurn turn, CancellationToken token)
    {
        using var conversation = isolated ? new DeepSeekPrivateConversation(
            new(instruction) { ConversationWorkspace = workspace, ConversationWorkId = turn.DispatchId }, directory, ConversationWorkspaceFiles) : null;
        await Execute(instruction, directory, turn.ShouldInterrupt, token, conversation, turn);
        return turn.Read()!;
    }

    protected async Task<RobotOutcome> Execute(string prompt, string? folder,
        Func<CancellationToken, Task<bool>>? shouldYield, CancellationToken shutdown, DeepSeekPrivateConversation? conversation = null,
        HarnessTurn? delegation = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        if (shouldYield is not null && await shouldYield(token))
        {
            delegation?.Finish("interrupted", "指令在发送前取消。");
            return new("suspended");
        }

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
        if (conversation is not null || delegation is null || !UseLauncherPermissions)
            start.Environment["DSH_PERMISSION_MODE"] = conversation?.WorkspaceFiles == true ? "workspace-write" : "read-only";
        if (electron) start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not start DeepSeek ACP.");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        string? activeSession = null;
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
                if ((conversation is not null || delegation is not null) && !(initialized.TryGetProperty("agentCapabilities", out var agent) &&
                    agent.TryGetProperty("sessionCapabilities", out var sessions) && sessions.TryGetProperty("resume", out _)))
                    throw new ConversationProviderException(ConversationFailure.Unsupported);
                await Request(2, "session/resume", new { sessionId, cwd, mcpServers });
            }

            delegation?.Opened(sessionId);
            if (shouldYield is not null && await shouldYield(token))
            {
                delegation?.Finish("interrupted", "指令在发送前取消。");
                await Request(4, "session/close", new { sessionId });
                return new("suspended");
            }
            var report = new StringBuilder();
            if (conversation is not null)
            {
                prompt = conversation.Prompt(sessionId, resumed, delegation is null ? null : prompt);
                conversation.MarkSending();
            }
            else if (delegation is not null) prompt = HarnessTurn.Instructions + "\n" + prompt;
            delegation?.Sending();
            activeSession = sessionId;
            await Send(new { jsonrpc = "2.0", id = 3, method = "session/prompt",
                @params = new { sessionId, prompt = new[] { new { type = "text", text = prompt } } } });
            Record("prompting");

            bool cancelled = false;
            var tools = new Dictionary<string, string>();
            long nextCheck = Environment.TickCount64;
            var pending = Read();
            while (true)
            {
                if (!cancelled && shouldYield is not null)
                {
                    await Task.WhenAny(pending, Task.Delay(300, token));
                    token.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 >= nextCheck)
                    {
                        nextCheck = Environment.TickCount64 + 300;
                        if (await shouldYield(token))
                        {
                            await Send(new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId } });
                            cancelled = true;
                        }
                    }
                }
                if (!pending.IsCompleted && !cancelled && shouldYield is not null) continue;
                var message = await pending;
                if (Matches(message, 3))
                {
                    activeSession = null;
                    if (delegation is not null && message.TryGetProperty("error", out var error))
                    {
                        delegation.Finish("failed", ErrorDetail(error));
                        return new("failed");
                    }
                    string? reason = Result(message, conversation).GetProperty("stopReason").GetString();
                    if (delegation is not null)
                    {
                        string status = reason switch { "end_turn" => "returned", "cancelled" => "interrupted", _ => "failed" };
                        string detail = status == "returned" ? report.ToString() : $"DeepSeek stopReason={reason}. {report}";
                        delegation.Finish(status, detail);
                        try { await Request(4, "session/close", new { sessionId }); }
                        catch (Exception e) when (e is IOException or OperationCanceledException) { }
                        return new(status, detail);
                    }
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
                    if (parameters.GetProperty("sessionId").GetString() == sessionId)
                    {
                        conversation?.ObserveUpdate(update);
                        if (delegation is not null) DeepSeekBotHarness.ObserveTool(delegation, tools, update);
                    }
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
            if (activeSession is not null && !process.HasExited)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var cancel = new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId = activeSession } };
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(cancel).AsMemory(), stop.Token);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { }
            }
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
            var line = await process.StandardOutput.ReadLineAsync(token) ?? throw new IOException(process.HasExited
                ? $"DeepSeek ACP disconnected (exit code {process.ExitCode})." : "DeepSeek ACP disconnected.");
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }

        async Task<JsonElement> Request(int id, string method, object parameters)
        {
            await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            while (true)
            {
                var message = await Read();
                if (Matches(message, id))
                {
                    if (delegation is not null && message.TryGetProperty("error", out var error))
                        throw new IOException(method + ": " + ErrorDetail(error));
                    return Result(message, conversation);
                }
                await RejectRequest(message);
            }
        }

        async Task RejectRequest(JsonElement message)
        {
            conversation?.ObserveGuard(message);
            if (!message.TryGetProperty("method", out var method) || !message.TryGetProperty("id", out var id)) return;
            if (method.GetString() == "session/request_permission")
            {
                await Send(new { jsonrpc = "2.0", id, result = new { outcome = new { outcome = "cancelled" } } });
                delegation?.Tool("request-" + id, method.GetString()!, "declined", "该操作需要原生 Harness 交互授权，当前配置未授予此权限。");
            }
            else
                await Send(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported client method" } });
        }
    }

    private static string ErrorDetail(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
            ? message.ToString() : HarnessTurn.Limit(error.ToString(), 1000);

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
