using System.Text.Json;
using LittleLoop.Tools;

static class CodexPrivateConversation
{
    private sealed record NativeThread(string Id, bool TextOnly);

    public static async Task<string> Answer(BotInput input, string executable, string sessionDir, CancellationToken token)
    {
        string session = Directory.CreateDirectory(Path.GetFullPath(sessionDir)).FullName;
        string workspace = Directory.CreateDirectory(input.ConversationWorkspace ?? Path.Combine(session, "workspace")).FullName;
        await using var lease = new FileStream(Path.Combine(session, "thread.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await using (var client = CodexAppServerClient.ForPrivateWorkspace(executable, workspace, session))
        {
            await client.InitializeAsync(deadline.Token, experimental: true);
            if (!OperatingSystem.IsWindows() || !await WindowsSandboxMissing(client, deadline.Token))
                return await Run(client, input, session, workspace, deadline.Token);
        }
        // The readiness check ran before a user turn. Start a native client without environment tools.
        await using var textClient = CodexAppServerClient.ForPrivateWorkspace(executable, workspace, session, tools: false);
        await textClient.InitializeAsync(deadline.Token, experimental: true);
        string answer = await Run(textClient, input, session, workspace, deadline.Token, textOnly: true);
        return WithNotice(answer, "当前 Codex 工作区工具尚未就绪，本次仅提供文字回复。");
    }

    internal static async Task<bool> WindowsSandboxMissing(IAgentSessionClient client, CancellationToken token)
    {
        var readiness = await client.CallAsync("windowsSandbox/readiness", null!, token);
        return readiness.GetProperty("status").GetString() switch
        {
            "ready" => false,
            "notConfigured" or "updateRequired" => true,
            _ => throw new ConversationProviderException(ConversationFailure.Unsupported)
        };
    }

    internal static async Task<string> Run(IAgentSessionClient client, BotInput input, string session, string workspace,
        CancellationToken token, bool textOnly = false, HarnessTurn? delegation = null)
    {
        string file = Path.Combine(session, "thread.json");
        NativeThread? previous = File.Exists(file)
            ? JsonSerializer.Deserialize<NativeThread>(await File.ReadAllTextAsync(file, token)) : null;
        if (File.Exists(file) && (previous is null || !Guid.TryParseExact(previous.Id, "D", out _)))
            throw new ConversationProviderException(ConversationFailure.Interrupted);
        bool resuming = previous is not null && previous.TextOnly == textOnly;

        // A unique profile cannot merge with a more permissive profile from an operator's config.
        string profile = "littleloop_chat_" + Guid.NewGuid().ToString("N");
        var filesystem = new Dictionary<string, object>
        {
            [":minimal"] = "read",
            [workspace] = new Dictionary<string, string>
            {
                ["."] = "write", [".git"] = "deny", [".codex"] = "deny", [".agents"] = "deny"
            }
        };
        var settings = await client.CallAsync("config/read", new { cwd = workspace, includeLayers = false }, token);
        var disabledServers = new Dictionary<string, object>();
        if (settings.GetProperty("config").TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (var server in servers.EnumerateObject()) disabledServers[server.Name] = new { enabled = false };
        var config = new Dictionary<string, object>
        {
            ["web_search"] = "disabled", ["project_doc_max_bytes"] = 0, ["mcp_servers"] = disabledServers
        };
        var parameters = new Dictionary<string, object>
        {
            ["cwd"] = workspace, ["runtimeWorkspaceRoots"] = new[] { workspace }, ["approvalPolicy"] = "never",
            ["config"] = config, ["developerInstructions"] = delegation is null ? ChatConversation.Instructions : HarnessTurn.Instructions
        };
        if (textOnly)
        {
            parameters["sandbox"] = "read-only";
            parameters["baseInstructions"] = "Answer the user's request in plain text. No tools are available.";
            parameters["developerInstructions"] = ChatConversation.Instructions + "\n当前工作区工具尚未就绪。本次只提供文字，请如实说明涉及文件或系统操作的限制。";
        }
        else
        {
            parameters["permissions"] = profile;
            config["permissions"] = new Dictionary<string, object> { [profile] = new { filesystem, network = new { enabled = false } } };
            if (OperatingSystem.IsWindows())
                config["windows"] = new { sandbox = "elevated" }; // Restricted-account sandbox.
        }
        if (resuming)
        {
            parameters["threadId"] = previous!.Id;
            parameters["excludeTurns"] = true;
        }
        else
        {
            parameters["selectedCapabilityRoots"] = Array.Empty<object>();
            parameters["ephemeral"] = false;
            if (textOnly) parameters["environments"] = Array.Empty<object>();
        }
        var opened = await client.CallAsync(resuming ? "thread/resume" : "thread/start", parameters, token);
        string? thread = opened.GetProperty("thread").GetProperty("id").GetString();
        if (!Guid.TryParseExact(thread, "D", out _) || resuming && previous!.Id != thread)
            throw new ConversationProviderException(ConversationFailure.Interrupted);
        var runtimeRoots = opened.GetProperty("runtimeWorkspaceRoots").EnumerateArray().Select(root => root.GetString()).ToArray();
        if (opened.GetProperty("approvalPolicy").GetString() != "never" ||
            !textOnly && (!opened.TryGetProperty("activePermissionProfile", out var active) || active.ValueKind != JsonValueKind.Object ||
            active.GetProperty("id").GetString() != profile) ||
            !(textOnly && runtimeRoots.Length == 0) && !runtimeRoots.SequenceEqual([workspace]) ||
            !opened.TryGetProperty("sandbox", out var sandbox) || sandbox.GetProperty("type").GetString() != (textOnly ? "readOnly" : "workspaceWrite") ||
            sandbox.GetProperty("networkAccess").GetBoolean() ||
            sandbox.TryGetProperty("writableRoots", out var roots) && roots.EnumerateArray().Any(root => root.GetString() != workspace))
            throw new ConversationProviderException(ConversationFailure.PermissionDenied);
        if (!resuming)
        {
            string temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new NativeThread(thread, textOnly)), token);
            File.Move(temp, file, true);
        }
        delegation?.Opened(thread);
        string prompt = delegation is null ? ChatConversation.MessageContext(resuming
            ? input with { History = input.History.Where(exchange => exchange.Quoted).ToArray() } : input) : input.Text;
        return await Turn(client, thread, textOnly ? null : profile, input.ConversationWorkId, prompt, token, delegation);
    }

    internal static async Task<string> Turn(IAgentSessionClient client, string thread, string? profile, Guid? workId,
        string prompt, CancellationToken token, HarnessTurn? delegation = null, bool deployedWorkspace = false)
    {
        string? turnId = null;
        bool toolStarted = false;
        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["threadId"] = thread, ["clientUserMessageId"] = workId?.ToString(),
                ["input"] = new[] { new { type = "text", text = prompt } }
            };
            if (!deployedWorkspace)
            {
                parameters["approvalPolicy"] = "never";
                if (profile is null) parameters["environments"] = Array.Empty<object>();
                else parameters["permissions"] = profile;
            }
            if (delegation is not null && await delegation.ShouldInterrupt(token))
                return delegation.Finish("interrupted", "指令在发送前取消。").Report;
            delegation?.Sending();
            var started = await client.CallAsync("turn/start", parameters, token);
            turnId = started.GetProperty("turn").GetProperty("id").GetString() ?? throw new InvalidDataException("Missing turn ID.");
            string? final = null;
            var patches = new HashSet<string>();
            var commands = new HashSet<string>();
            bool stopping = false;
            long nextCheck = Environment.TickCount64;
            while (true)
            {
                var pending = client.NextAsync(token);
                if (delegation is not null && !stopping)
                {
                    do
                    {
                        if (Environment.TickCount64 >= nextCheck)
                        {
                            if (await delegation.ShouldInterrupt(token))
                            {
                                await client.CallAsync("turn/interrupt", new { threadId = thread, turnId }, token);
                                stopping = true;
                                break;
                            }
                            nextCheck = Environment.TickCount64 + 300;
                        }
                        if (!pending.IsCompleted)
                            await Task.WhenAny(pending, Task.Delay(300, token));
                        token.ThrowIfCancellationRequested();
                    } while (!pending.IsCompleted);
                }
                var item = await pending;
                var method = item.GetProperty("method").GetString()!;
                var p = item.GetProperty("params");
                if (item.TryGetProperty("id", out var requestId))
                {
                    if (!AgentApproval.Supported(method))
                    {
                        if (delegation is not null)
                            throw new IOException($"Codex 请求了当前部署无法响应的交互 {method}。");
                        throw new ConversationProviderException(ConversationFailure.Unsupported);
                    }
                    await client.ReplyAsync(requestId, AgentApproval.Response(method, p, false), token);
                    delegation?.Tool("request-" + requestId, method, "declined", "该操作需要原生 Harness 交互授权，当前配置未授予此权限。");
                    continue;
                }
                if (!p.TryGetProperty("threadId", out var id) || id.GetString() != thread) continue;
                if (delegation is not null && method == "item/started" && p.GetProperty("turnId").GetString() == turnId)
                    CodexBotHarness.ObserveTool(delegation, p.GetProperty("item"), started: true);
                if (method is "item/started" or "item/completed" && p.GetProperty("turnId").GetString() == turnId &&
                    p.GetProperty("item").GetProperty("type").GetString() is "fileChange" or "commandExecution")
                {
                    toolStarted = true;
                    if (profile is null && !deployedWorkspace) throw new ConversationProviderException(ConversationFailure.Interrupted);
                }
                if (method == "item/completed" && p.GetProperty("turnId").GetString() == turnId)
                    Observe(p.GetProperty("item"));
                if (method != "turn/completed") continue;
                var turn = p.GetProperty("turn");
                if (turn.GetProperty("id").GetString() != turnId) continue;
                if (turn.TryGetProperty("items", out var completed) && completed.ValueKind == JsonValueKind.Array)
                    foreach (var entry in completed.EnumerateArray()) Observe(entry);
                string? nativeStatus = turn.GetProperty("status").GetString();
                if (delegation is not null)
                {
                    if (nativeStatus == "completed") return delegation.Finish("returned", final?.Trim() ?? "").Report;
                    if (nativeStatus == "interrupted") return delegation.Finish("interrupted", "Codex 已确认中断本轮。").Report;
                    string reason = turn.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("message", out var message) ? message.GetString() ?? nativeStatus! : nativeStatus!;
                    return delegation.Finish("failed", reason ?? "Codex 未返回完成状态。").Report;
                }
                if (nativeStatus != "completed")
                    throw new ConversationProviderException(toolStarted ? ConversationFailure.Interrupted : TurnFailure(turn));
                if (string.IsNullOrWhiteSpace(final)) throw new ConversationProviderException(ConversationFailure.Interrupted);
                var receipts = new List<string>();
                if (patches.Count > 0) receipts.Add($"已应用 {patches.Count} 次文件补丁");
                if (commands.Count > 0) receipts.Add($"{commands.Count} 个命令成功退出");
                return receipts.Count == 0 ? final.Trim() : WithNotice(final.Trim(), "Codex 回执：" + string.Join("，", receipts) + "。");
            }

            void Observe(JsonElement entry)
            {
                if (delegation is not null) CodexBotHarness.ObserveTool(delegation, entry);
                final = AnswerText(entry) ?? final;
                if (!entry.TryGetProperty("status", out var status) || status.GetString() != "completed" ||
                    !entry.TryGetProperty("id", out var itemId) || itemId.GetString() is not { Length: > 0 } key) return;
                switch (entry.GetProperty("type").GetString())
                {
                    case "fileChange" when entry.GetProperty("changes").GetArrayLength() > 0: patches.Add(key); break;
                    case "commandExecution" when entry.TryGetProperty("exitCode", out var exit) &&
                        exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out int code) && code == 0: commands.Add(key); break;
                }
            }
        }
        catch (Exception e)
        {
            if (turnId is not null)
            {
                using var interrupt = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await client.CallAsync("turn/interrupt", new { threadId = thread, turnId }, interrupt.Token); }
                catch (Exception failure) when (failure is IOException or OperationCanceledException or TimeoutException or InvalidOperationException) { }
            }
            if (delegation is not null) throw;
            if (toolStarted) throw new ConversationProviderException(ConversationFailure.Interrupted, e);
            if (e is ConversationProviderException or OperationCanceledException or TimeoutException) throw;
            throw new ConversationProviderException(ConversationFailure.Interrupted, e);
        }
    }

    private static string WithNotice(string answer, string notice)
    {
        int available = 12000 - notice.Length - 2;
        if (answer.Length > available)
        {
            int count = available - 1;
            if (char.IsHighSurrogate(answer[count - 1])) count--;
            answer = answer[..count] + "…";
        }
        return answer + "\n\n" + notice;
    }

    private static string? AnswerText(JsonElement item) =>
        item.GetProperty("type").GetString() == "agentMessage" &&
        (!item.TryGetProperty("phase", out var phase) || phase.GetString() is null or "final_answer")
            ? item.GetProperty("text").GetString() : null;

    private static ConversationFailure TurnFailure(JsonElement turn)
    {
        if (!turn.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("codexErrorInfo", out var code)) return ConversationFailure.Interrupted;
        if (code.ValueKind == JsonValueKind.String)
            return code.GetString() switch
            {
                "rateLimitExceeded" or "usageLimitExceeded" => ConversationFailure.RateLimited,
                "sandboxError" when error.TryGetProperty("message", out var detail) &&
                    detail.GetString()?.Contains("app runtime provisioning service is unavailable", StringComparison.Ordinal) == true
                    => ConversationFailure.ToolRuntimeUnavailable,
                "unauthorized" or "sandboxError" or "inferenceHookDenied" => ConversationFailure.PermissionDenied,
                "serverOverloaded" or "modelNotFound" => ConversationFailure.Unavailable,
                _ => ConversationFailure.Interrupted
            };
        if (code.ValueKind == JsonValueKind.Object)
        {
            foreach (var variant in code.EnumerateObject())
                if (variant.Value.ValueKind == JsonValueKind.Object && variant.Value.TryGetProperty("httpStatusCode", out var status) &&
                    status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int http))
                    return http switch
                    {
                        429 => ConversationFailure.RateLimited, 401 or 403 => ConversationFailure.PermissionDenied,
                        408 or 504 => ConversationFailure.TimedOut,
                        _ => ConversationFailure.Unavailable
                    };
        }
        return ConversationFailure.Interrupted;
    }
}
