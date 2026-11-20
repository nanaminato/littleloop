using LittleLoop.Tools;

sealed class DeepSeekChatAdapter(string launcher, string[] arguments, string? harnessHome, bool electron,
    string project, TimeSpan timeout, AgentHostPolicy? development = null, string provider = ChatStore.DeepSeekBotName)
    : DeepSeekAcpClient(launcher, arguments, harnessHome, electron, project, timeout), IChatRobotAdapter
{
    public string Name => provider;
    public bool SupportsApproval => development is not null;

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
}
