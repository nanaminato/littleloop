using System.Data.Common;
using LittleLoop.Core;

sealed partial class ChatStore
{
    private static async Task<string?> HandleGoalMemory(DbConnection db, DbTransaction tx, string room, string? roomType,
        string owner, long sequence, long? replyTo, string text, List<BotWork> goals, string? grantHash)
    {
        string command = text.Trim();
        if (command.StartsWith("@littleloop", StringComparison.OrdinalIgnoreCase))
            command = command[11..].TrimStart(' ', '：', ':', '，', ',');
        string answerText = command.TrimEnd('。', '.', '！', '!').TrimEnd().ToLowerInvariant();
        bool explicitAnswer = answerText is "确认偏好" or "拒绝偏好" or "confirm preference" or "reject preference";
        bool management = explicitAnswer || command is "启用经验" or "停用经验" or "查看经验" or "撤回经验" or "查看偏好" or "忘记偏好" or "回顾经验"
            || command.StartsWith("记住偏好", StringComparison.Ordinal);
        if (roomType != "direct") return management ? "长期记忆命令仅在 LittleLoop 私聊中可用。" : null;
        var observation = management ? null : ConversationMemoryPolicy.Observe(command);
        bool? answer = PreferenceAnswer(answerText);
        if (!management && observation is null && answer is null) return null;
        // The inbox preserves the sent text, even when its source was edited/deleted before consumption.
        await using var source = Command(db, tx, """
            SELECT 1 FROM chat_messages WHERE room_id=@room AND seq=@seq AND sender_id=@owner AND NOT edited AND NOT deleted
            """, ("room", room), ("seq", sequence), ("owner", owner));
        if (await source.ExecuteScalarAsync() is null) return "来源消息已更改，本次未写入记忆。";
        if (answer is bool accepted)
            return await ReviewPreference(db, tx, room, owner, sequence, replyTo, accepted, explicitAnswer);

        switch (command)
        {
            case "启用经验" or "停用经验" or "查看经验" or "撤回经验":
                return await ManageGoalKnowledge(db, tx, room, owner, replyTo, command, grantHash);
            case "查看偏好":
                string preference = (await ReadGoalPreference(db, tx, room, owner)) switch
                {
                    true => "当前偏好：完成通知含目标。",
                    false => "当前偏好：完成通知简洁。",
                    null => "尚未保存偏好。默认使用简洁的完成通知。"
                };
                var review = await ReadPreferenceReview(db, tx, room, owner);
                if (review.Proposal is { } proposedAt && review.IncludeGoal is bool suggested &&
                    await RepeatedNoticeChoice(db, tx, room, owner, review.Since) == suggested)
                    preference += $"\n待确认建议（来源消息 #{proposedAt}）：以后默认{(suggested ? "附上任务目标" : "简洁通知")}。";
                return preference;
            case "忘记偏好":
                await using (var delete = Command(db, tx, "DELETE FROM chat_goal_preferences WHERE room_id=@room AND owner_id=@owner",
                    ("room", room), ("owner", owner)))
                    await delete.ExecuteNonQueryAsync();
                await using (var forget = Command(db, tx, "DELETE FROM chat_goal_notice_overrides WHERE room_id=@room AND owner_id=@owner",
                    ("room", room), ("owner", owner)))
                    await forget.ExecuteNonQueryAsync();
                await ResetPreferenceReview(db, tx, room, owner, sequence);
                return "已清除保存的偏好。之后使用默认的简洁完成通知。";
            case "回顾经验":
                return await RecallCompletedGoals(db, tx, room, owner);
            case "记住偏好：完成通知含目标":
            case "记住偏好:完成通知含目标":
            case "记住偏好：完成通知简洁":
            case "记住偏好:完成通知简洁":
                return await SavePreference(db, tx, room, owner, sequence, command.EndsWith("含目标", StringComparison.Ordinal));
        }
        if (observation is null) return "可设置“记住偏好：完成通知含目标”或“记住偏好：完成通知简洁”。";
        if (observation.Subject == MemorySubject.CompletionNotice)
        {
            bool include = observation.Value == "with-goal";
            if (observation.Scope == MemoryScope.Durable)
                return await SavePreference(db, tx, room, owner, sequence, include);
            return await SetTaskNotice(db, tx, room, owner, sequence, replyTo, goals, include);
        }
        if (observation.Scope != MemoryScope.Durable) return null; // Continue through normal per-goal revision routing.
        // A reply to another provider's task cannot teach the default project's memory.
        if (replyTo is not null && goals.Any(goal => !SupportsGoalExperience(goal)))
            return "这项任务使用其他执行上下文。请在相应项目中说明要保留的做法。";
        string lessonReply = await SaveGoalLesson(db, tx, room, owner, sequence, grantHash, observation);
        // Room-scoped corrections also reach the current goal through its normal versioned revision.
        // A specifically future rule or a correction to an unrelated task must not restart existing work.
        bool futureOnly = new[] { "以后", "今后", "从下次", "下次", "in future", "in the future", "next time", "going forward" }
            .Any(marker => command.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (goals is [var target] && ActiveForMemory(target) && (replyTo is not null ||
            !futureOnly && SupportsGoalExperience(target) && ConversationMemoryPolicy.Applies(observation, target.Goal!)))
        {
            var goal = target.Goal!;
            try
            {
                var revised = goal.Change("revise", command, goal.Version);
                await SaveGoal(db, tx, target, revised, sequence, "revise");
                lessonReply += $"\n要求已补充到当前任务 v{revised.Version}。";
            }
            catch (InvalidOperationException e) { lessonReply += "\n" + e.Message; }
        }
        else if (!futureOnly && goals.Count > 1 && goals.Any(work => ActiveForMemory(work) &&
            ConversationMemoryPolicy.Applies(observation, work.Goal!)))
            lessonReply += "\n当前有多个任务。若要马上修改其中一项，请回复该任务卡补充要求。";
        return lessonReply;
    }

    private static bool ActiveForMemory(BotWork work) =>
        work.Status is "queued" or "running" or "waiting-input" or "paused";

    private static async Task<string> SavePreference(DbConnection db, DbTransaction tx, string room, string owner,
        long sequence, bool include)
    {
        await using var save = Command(db, tx, """
            INSERT INTO chat_goal_preferences(room_id,owner_id,include_objective,source_seq) VALUES(@room,@owner,@include,@seq)
            ON DUPLICATE KEY UPDATE include_objective=@include,source_seq=@seq
            """, ("room", room), ("owner", owner), ("include", include), ("seq", sequence));
        await save.ExecuteNonQueryAsync();
        await ResetPreferenceReview(db, tx, room, owner, sequence);
        return include ? "已保存偏好：完成通知含目标。" : "已保存偏好：完成通知简洁。";
    }

    private static async Task<bool?> ReadGoalPreference(DbConnection db, DbTransaction tx, string room, string owner)
    {
        await using var select = Command(db, tx, """
            SELECT p.include_objective FROM chat_goal_preferences p JOIN chat_messages m
              ON m.room_id=p.room_id AND m.seq=p.source_seq AND m.sender_id=p.owner_id
            WHERE p.room_id=@room AND p.owner_id=@owner AND NOT m.deleted AND NOT m.edited
            """, ("room", room), ("owner", owner));
        var value = await select.ExecuteScalarAsync();
        return value is null ? null : Convert.ToBoolean(value);
    }

    private static async Task<bool> CompletionIncludesGoal(DbConnection db, DbTransaction tx, BotWork work)
    {
        await using var select = Command(db, tx, """
            SELECT n.include_objective FROM chat_goal_notice_overrides n JOIN chat_messages m
              ON m.room_id=n.room_id AND m.seq=n.source_seq AND m.sender_id=n.owner_id
            WHERE n.work_id=@id AND n.room_id=@room AND n.owner_id=@owner AND NOT m.edited AND NOT m.deleted
            """, ("id", work.Id.ToString()), ("room", work.Room), ("owner", work.Owner));
        return await select.ExecuteScalarAsync() is { } value ? Convert.ToBoolean(value) :
            await ReadGoalPreference(db, tx, work.Room, work.Owner) is true;
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
