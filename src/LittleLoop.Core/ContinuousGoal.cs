namespace LittleLoop.Core;

// The host persists this aggregate atomically when consuming the originating message event.
// Tool sessions hold execution evidence, not a second copy of the user's goal lifecycle.
public sealed record ContinuousGoal(int Version, string Objective, string[] Requirements, string Status)
{
    public string Prompt => Objective + (Requirements.Length == 0 ? "" :
        "\n用户后续要求（按时间顺序；冲突时先澄清）：\n" + string.Join("\n", Requirements.Select((s, i) => $"{i + 1}. {s}")));

    public static ContinuousGoal Create(string objective) => new(1, objective, [],
        string.IsNullOrWhiteSpace(objective) ? "waiting-input" : "queued");

    public ContinuousGoal Change(string action, string text, int expectedVersion)
    {
        if (Version != expectedVersion) throw new InvalidOperationException("目标已变化，请刷新后重试。");
        if (action == "status") return this;
        if (Status is "completed" or "cancelled" or "failed" or "needs-reconciliation")
            throw new InvalidOperationException("此任务已结束或需要核对，不能直接恢复；请查看结果。");
        return action switch
        {
            "pause" => this with { Version = Version + 1, Status = "paused" },
            "cancel" => this with { Version = Version + 1, Status = "cancelled" },
            "resume" when Status == "paused" => this with
            { Version = Version + 1, Status = string.IsNullOrWhiteSpace(Objective) ? "waiting-input" : "queued" },
            "revise" when !string.IsNullOrWhiteSpace(text) && text.Length <= 4000 && Requirements.Length < 32 && Prompt.Length + text.Length < 24000 => this with
            {
                Version = Version + 1,
                Objective = string.IsNullOrWhiteSpace(Objective) ? text : Objective,
                Requirements = string.IsNullOrWhiteSpace(Objective) ? Requirements : [.. Requirements, text],
                Status = Status == "paused" ? "paused" : "queued"
            },
            _ => throw new InvalidOperationException("无法应用此操作；请补充具体要求，或对暂停的任务说“恢复”。")
        };
    }

    public ContinuousGoal Finish(int executedVersion, string status) => Version == executedVersion && Status == "running"
        ? this with { Status = status } : this;
}

public sealed record GoalMessageIntent(string Action, string Text);

public static class GoalMessageRouter
{
    // Bounded-domain routing: free text within an active goal is supplemental context,
    // never a grant to expand tool permissions.
    public static GoalMessageIntent Parse(string message, bool hasGoal)
    {
        string text = message.Trim();
        if (text.StartsWith("@littleloop", StringComparison.OrdinalIgnoreCase)) text = text[11..].TrimStart(' ', '：', ':', '，', ',');
        string command = text.TrimEnd('。', '！', '!', '.', '？', '?').Trim();
        if (command is "暂停" or "暂停任务" or "先暂停" or "先停一下" or "/pause") return new("pause", "");
        if (command is "恢复" or "恢复任务" or "继续" or "继续工作" or "/resume") return new("resume", "");
        if (command is "取消" or "取消任务" or "/cancel") return new("cancel", "");
        if (command is "进度" or "查询进度" or "查询任务" or "任务状态" or "进展如何" or "进度如何" or "做到哪了" or "/status") return new("status", "");
        // Discussion and quoted examples are context, not new work or implicit revisions.
        if (text.StartsWith('>') || text.StartsWith('"') || text.StartsWith('“') || text.StartsWith('「') ||
            new[] { "如果", "假如", "为什么", "怎么", "如何" }.Any(text.StartsWith)) return new("help", "");
        foreach (string prefix in new[] { "新任务：", "新任务:", "任务：", "任务:", "/task " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return new("create", text[prefix.Length..].Trim());
        if (command is "新任务" or "任务" or "/task") return new("create", "");
        foreach (string prefix in new[] { "补充：", "补充:", "补充要求：", "补充要求:", "/revise " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return new("revise", text[prefix.Length..].Trim());
        if (hasGoal && text.Length > 0 && !text.EndsWith('?') && !text.EndsWith('？') &&
            text is not ("你好" or "谢谢" or "好的")) return new("revise", text);
        if (!hasGoal && !text.EndsWith('?') && !text.EndsWith('？') &&
            new[] { "请修复", "帮我修复", "修复", "请实现", "帮我实现", "实现", "请修改", "帮我修改", "修改" }.Any(text.StartsWith))
            return new("create", text);
        return new("help", "");
    }
}
