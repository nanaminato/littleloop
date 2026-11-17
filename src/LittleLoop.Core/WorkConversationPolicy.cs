namespace LittleLoop.Core;

/// <summary>Interpretation only. The host authenticates, scopes references, and atomically checks versions before acting.</summary>
public enum WorkIntent { None, Accept, Pause, Cancel, Revise, Replace, Resume, Clarify }
public sealed record WorkUtterance(string MessageId, string Text, string? ReplyToMessageId = null, string? TaskId = null, int? ExpectedVersion = null);
public sealed record WorkReference(string Id, int Version, string Goal, string Status, string[] MessageIds);
public sealed record WorkInterpretation(WorkIntent Intent, string? TaskId, int? ExpectedVersion, string? Goal,
    string[] EvidenceMessageIds, string Reason);
public sealed record WorkMemoryMessage(string Id, string Text, string? TaskId = null);

public static class WorkConversationPolicy
{
    public const int MaximumTextLength = 12000;
    public const int MaximumReferences = 64;
    private static readonly string[] Active = ["queued", "running", "paused", "stopping", "waiting-approval"];

    public static WorkInterpretation Interpret(WorkUtterance message, IReadOnlyList<WorkReference> references)
    {
        WorkInterpretation Result(WorkIntent intent, WorkReference? target = null, string? goal = null, string reason = "") =>
            new(intent, target?.Id, target?.Version, goal,
                new[] { message.MessageId, message.ReplyToMessageId }.Where(x => x is not null).Cast<string>().Distinct().ToArray(), reason);
        if (string.IsNullOrWhiteSpace(message.MessageId) || message.Text.Length > MaximumTextLength || references.Count > MaximumReferences)
            return Result(WorkIntent.Clarify, reason: "Input exceeds the bounded interpretation window.");
        var text = message.Text.Trim();
        if (text.Length == 0) return Result(WorkIntent.None);
        // Questions, quotation and hypothetical discussion are never executable instructions in this finite grammar.
        if (text.StartsWith('>') || text.StartsWith('"') || text.StartsWith('“') || text.StartsWith('「') ||
            text.Contains('?') || text.Contains('？') || Starts(text, ["如果", "假如", "为什么", "怎么", "如何", "if ", "why ", "how ", "what "]))
            return Result(WorkIntent.None, reason: "Question, quotation or hypothetical, not a work command.");
        if (Starts(text, ["请", "please "])) text = text.StartsWith("请", StringComparison.Ordinal) ? text[1..].TrimStart() : text[7..].TrimStart();

        if (Starts(text, ["补充", "also ", "add "]))
            return Result(WorkIntent.Clarify, reason: "Please give the complete revised goal with 修改为 / revise to; partial changes are not merged implicitly.");
        var (intent, goal) = Parse(text);
        if (intent == WorkIntent.None) return Result(intent, reason: "Outside the finite work-intent grammar.");
        if (goal is { Length: 0 }) return Result(WorkIntent.Clarify, reason: "Please provide the new work goal.");
        // References such as 'yesterday' require evidence retrieval; they are not silently interpreted as a new executable goal.
        if (goal is not null && Starts(goal, ["之前", "昨天", "上次", "那个", "按之前", "按昨天", "按上次", "the previous", "yesterday", "that one", "as before"]))
            return Result(WorkIntent.Clarify, reason: "Reply to the original goal or quote the intended goal; historical reference is ambiguous.");

        var explicitTarget = message.TaskId is not null || message.ReplyToMessageId is not null;
        var matches = references.Where(r => (message.TaskId is null || r.Id == message.TaskId) &&
            (message.ReplyToMessageId is null || r.MessageIds.Contains(message.ReplyToMessageId, StringComparer.Ordinal))).ToArray();
        if (!explicitTarget) matches = references.Where(r => Active.Contains(r.Status, StringComparer.Ordinal)).ToArray();
        if (intent == WorkIntent.Accept)
        {
            if (explicitTarget || matches.Length != 0)
                return Result(WorkIntent.Clarify, reason: "An existing commitment was referenced or is active. Say replace explicitly, or finish it first.");
            return Result(intent, goal: goal, reason: "Explicit new work request.");
        }
        if (matches.Length != 1) return Result(WorkIntent.Clarify, reason: "Please reply to exactly one current task card.");
        var target = matches[0];
        if (message.ExpectedVersion is { } expected && expected != target.Version)
            return Result(WorkIntent.Clarify, target, reason: "The referenced task version is stale.");
        if (!Active.Contains(target.Status, StringComparer.Ordinal))
            return Result(WorkIntent.Clarify, target, reason: "Historical work is evidence, not a current commitment or renewed authorization.");
        return Result(intent, target, goal, "Bounded work request resolved to one current commitment.");
    }

    private static (WorkIntent, string?) Parse(string text)
    {
        var clean = text.TrimEnd('。', '.', '！', '!');
        if (Equal(clean, ["停一下", "先停一下", "暂停", "先别做", "先别做了", "pause", "pause this", "stop for now"])) return (WorkIntent.Pause, null);
        if (Equal(clean, ["停止", "取消", "取消任务", "停止任务", "不要做了", "别做了", "stop", "cancel", "cancel this", "stop this task"])) return (WorkIntent.Cancel, null);
        if (Equal(clean, ["继续", "继续吧", "继续任务", "resume", "resume this"])) return (WorkIntent.Resume, null);
        foreach (var prefix in new[] { "改为", "换成", "改做", "替换为", "instead, ", "instead ", "replace with " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (WorkIntent.Replace, text[prefix.Length..].Trim(' ', ':', '：', '，', ','));
        foreach (var prefix in new[] { "修改为", "更新要求：", "revise to ", "update goal: " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (WorkIntent.Revise, text[prefix.Length..].Trim());
        foreach (var prefix in new[] { "帮我", "请帮我", "做一下", "开始任务：", "开始任务:", "work on ", "start task: ", "help me " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (WorkIntent.Accept, text[prefix.Length..].Trim());
        return (WorkIntent.None, null);
    }
    private static bool Starts(string value, string[] prefixes) => prefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    private static bool Equal(string value, string[] choices) => choices.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns bounded evidence only; retrieved history is never itself an instruction. Caller scopes by account/room first.</summary>
    public static WorkMemoryMessage[] SelectContext(IEnumerable<WorkMemoryMessage> candidates, string? replyTo, string? taskId,
        int maximumMessages = 8, int maximumCharacters = 6000)
    {
        if (maximumMessages is < 1 or > 16 || maximumCharacters is < 1 or > 12000) throw new ArgumentOutOfRangeException();
        // Input is already a bounded storage query, not the entire conversation history.
        var bounded = candidates.Take(65).ToArray();
        if (bounded.Length > 64) throw new ArgumentException("Query at most 64 context candidates.");
        var chosen = bounded.Where(m => m.Id == replyTo || (taskId is not null && m.TaskId == taskId))
            .OrderByDescending(m => m.Id == replyTo).DistinctBy(m => m.Id);
        var result = new List<WorkMemoryMessage>();
        foreach (var m in chosen)
        {
            if (result.Count == maximumMessages || maximumCharacters == 0) break;
            var excerpt = m.Text[..Math.Min(m.Text.Length, maximumCharacters)];
            result.Add(m with { Text = excerpt });
            maximumCharacters -= excerpt.Length;
        }
        return result.ToArray();
    }
}
