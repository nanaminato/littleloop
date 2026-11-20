using System.Text.Json;

static class DeepSeekPrivateConversationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var folder = Directory.CreateTempSubdirectory("littleloop-private-acp-");
        try
        {
            var input = new BotInput("current-message")
            {
                ConversationWorkId = Guid.NewGuid(),
                History = [new("past-unquoted", "old", false), new("explicit-quote", "old-quote", true)]
            };
            const string session = "private-acp-session";
            using (var turn = new DeepSeekPrivateConversation(input, folder.FullName, true))
            {
                bool denied = false;
                try { turn.Prompt(session, false); }
                catch (ConversationProviderException ex) when (ex.Reason == ConversationFailure.PermissionDenied) { denied = true; }
                check(denied && !turn.Submitted, "DeepSeek private messages require an acknowledged guard before sending");
                turn.ObserveGuard(JsonSerializer.SerializeToElement(new
                {
                    method = "littleloop/private-ready", @params = new { nonce = turn.Challenge, sessionId = session, workspace = turn.Workspace, tools = new[] { "read", "write", "edit" } }
                }));
                denied = false;
                try { turn.Prompt("another-session", true); }
                catch (ConversationProviderException ex) when (ex.Reason == ConversationFailure.PermissionDenied) { denied = true; }
                string first = turn.Prompt(session, false), resumed = turn.Prompt(session, true);
                check(denied && first.Contains("past-unquoted") && !resumed.Contains("past-unquoted") && resumed.Contains("explicit-quote") &&
                    resumed.Contains("current-message"), "ACP session pinning, history bootstrap and explicitly quoted follow-up");
                turn.MarkSending();
            }
            using (var recovered = new DeepSeekPrivateConversation(input, folder.FullName, true))
            {
                bool uncertain = false;
                try { recovered.ExistingAnswer(); }
                catch (ConversationProviderException ex) when (ex.Reason == ConversationFailure.Interrupted) { uncertain = true; }
                check(uncertain, "ambiguous ACP work survives restart without replaying its prompt");
            }
            var settled = input with { ConversationWorkId = Guid.NewGuid() };
            using (var turn = new DeepSeekPrivateConversation(settled, folder.FullName, true))
            {
                turn.MarkSending();
                turn.ObserveUpdate(JsonSerializer.SerializeToElement(new { sessionUpdate = "tool_call_update", toolCallId = "unknown", status = "completed" }));
                turn.ObserveUpdate(JsonSerializer.SerializeToElement(new { sessionUpdate = "tool_call", toolCallId = "write1", title = "write" }));
                turn.ObserveUpdate(JsonSerializer.SerializeToElement(new { sessionUpdate = "tool_call_update", toolCallId = "write1", status = "failed" }));
                check(turn.Finish("report") == "report", "unmatched or failed ACP tool updates produce no success receipt");
            }
            using (var recovered = new DeepSeekPrivateConversation(settled, folder.FullName, true))
                check(recovered.ExistingAnswer() == "report", "completed private ACP work restores the settled answer");
            var limited = JsonSerializer.SerializeToElement(new { code = -32603, data = new { httpStatus = 429 } });
            var missing = JsonSerializer.SerializeToElement(new { code = -32601 });
            var unknown = JsonSerializer.SerializeToElement(new { code = -32603, message = "unknown" });
            check(DeepSeekChatAdapter.PrivateError(limited, true) == ConversationFailure.RateLimited &&
                DeepSeekChatAdapter.PrivateError(missing, false) == ConversationFailure.Unsupported &&
                DeepSeekChatAdapter.PrivateError(unknown, true) == ConversationFailure.Interrupted,
                "private ACP error handling distinguishes rate limits, unsupported methods and uncertainty");
        }
        finally { folder.Delete(true); }
    }
}
