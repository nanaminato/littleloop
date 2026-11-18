using System.Data.Common;
using LittleLoop.Core;

sealed partial class ChatStore
{
    private static async Task<string?> HandleGoalMemory(DbConnection db, DbTransaction tx, string room, string? roomType,
        string owner, long sequence, string text)
    {
        string command = text.Trim();
        if (command.StartsWith("@littleloop", StringComparison.OrdinalIgnoreCase))
            command = command[11..].TrimStart(' ', '：', ':', '，', ',');
        if (command is not ("查看偏好" or "忘记偏好" or "回顾经验") && !command.StartsWith("记住偏好", StringComparison.Ordinal))
            return null;
        if (roomType != "direct") return "长期记忆命令仅在 LittleLoop 私聊中可用。";

        switch (command)
        {
            case "查看偏好":
                return (await ReadGoalPreference(db, tx, room, owner)) switch
                {
                    true => "当前偏好：完成通知含目标。",
                    false => "当前偏好：完成通知简洁。",
                    null => "尚未保存偏好。默认使用简洁的完成通知。"
                };
            case "忘记偏好":
                await using (var delete = Command(db, tx, "DELETE FROM chat_goal_preferences WHERE room_id=@room AND owner_id=@owner",
                    ("room", room), ("owner", owner)))
                    await delete.ExecuteNonQueryAsync();
                return "已清除保存的偏好。之后使用默认的简洁完成通知。";
            case "回顾经验":
                return await RecallCompletedGoals(db, tx, room, owner);
            case "记住偏好：完成通知含目标":
            case "记住偏好:完成通知含目标":
            case "记住偏好：完成通知简洁":
            case "记住偏好:完成通知简洁":
                bool include = command.EndsWith("含目标", StringComparison.Ordinal);
                await using (var save = Command(db, tx, """
                    INSERT INTO chat_goal_preferences(room_id,owner_id,include_objective,source_seq) VALUES(@room,@owner,@include,@seq)
                    ON DUPLICATE KEY UPDATE include_objective=@include,source_seq=@seq
                    """, ("room", room), ("owner", owner), ("include", include), ("seq", sequence)))
                    await save.ExecuteNonQueryAsync();
                return include ? "已保存偏好：完成通知含目标。" : "已保存偏好：完成通知简洁。";
            default:
                return "可设置“记住偏好：完成通知含目标”或“记住偏好：完成通知简洁”。";
        }
    }

    private static async Task<bool?> ReadGoalPreference(DbConnection db, DbTransaction tx, string room, string owner)
    {
        await using var select = Command(db, tx, """
            SELECT include_objective FROM chat_goal_preferences WHERE room_id=@room AND owner_id=@owner
            """, ("room", room), ("owner", owner));
        var value = await select.ExecuteScalarAsync();
        return value is null ? null : Convert.ToBoolean(value);
    }

    private static async Task<string> RecallCompletedGoals(DbConnection db, DbTransaction tx, string room, string owner)
    {
        await using var select = Command(db, tx, """
            SELECT w.seq,w.goal FROM chat_bot_work w
            JOIN chat_messages m ON m.room_id=w.room_id AND m.seq=w.seq AND NOT m.deleted
            WHERE w.room_id=@room AND w.owner_id=@owner AND w.goal IS NOT NULL AND w.status='completed'
            ORDER BY w.seq DESC LIMIT 3
            """, ("room", room), ("owner", owner));
        await using var reader = await select.ExecuteReaderAsync();
        var entries = new List<string>();
        while (await reader.ReadAsync())
        {
            var objective = FromJson<ContinuousGoal>(reader.GetString(1)).Objective;
            entries.Add($"任务消息 #{reader.GetInt64(0)} · {GoalMemoryExcerpt(objective)}");
        }
        return entries.Count == 0 ? "本私聊暂无线索可回顾。完成并通过验收的任务会出现在这里。" :
            "本私聊最近通过验收的任务\n" + string.Join("\n", entries);
    }

    private static string GoalMemoryExcerpt(string objective) =>
        objective.Length <= 300 ? objective : objective[..(char.IsHighSurrogate(objective[299]) ? 299 : 300)] + "…";
}
