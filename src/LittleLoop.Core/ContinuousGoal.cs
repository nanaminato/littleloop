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
        if (Status is "completed" or "cancelled" or "failed" || Status == "needs-reconciliation" && action != "cancel")
            throw new InvalidOperationException("此任务已结束或需要核对，不能直接恢复；请查看结果。");
        return action switch
        {
            "pause" => this with { Version = Version + 1, Status = "paused" },
            "cancel" => this with { Version = Version + 1, Status = "cancelled" },
            "resume" when Status is "paused" or "waiting-input" => this with
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
