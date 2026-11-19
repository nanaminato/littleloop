using System.Data.Common;

sealed partial class ChatStore
{
    private sealed record PreferenceReview(long Since, long? Proposal, bool? IncludeGoal);

    private static bool? PreferenceAnswer(string normalized) => normalized switch
        {
            "确认偏好" or "是" or "是的" or "可以" or "好的" or "好" or "同意" or "确认" or "yes" or "okay" or "ok" or "confirm preference" => true,
            "拒绝偏好" or "不用" or "不要" or "不用了" or "否" or "拒绝" or "no" or "no thanks" or "reject preference" => false,
            _ => null
        };

    private static async Task<PreferenceReview> ReadPreferenceReview(DbConnection db, DbTransaction tx, string room, string owner)
    {
        await using var read = Command(db, tx, """
            SELECT since_seq,pending_seq,include_objective FROM chat_goal_preference_reviews
            WHERE room_id=@room AND owner_id=@owner
            """, ("room", room), ("owner", owner));
        await using var r = await read.ExecuteReaderAsync();
        return await r.ReadAsync() ? new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetBoolean(2)) : new(0, null, null);
    }

    private static async Task ResetPreferenceReview(DbConnection db, DbTransaction tx, string room, string owner, long sequence)
    {
        await using var reset = Command(db, tx, """
            INSERT INTO chat_goal_preference_reviews(room_id,owner_id,since_seq) VALUES(@room,@owner,@seq)
            ON DUPLICATE KEY UPDATE since_seq=@seq,pending_seq=NULL,include_objective=NULL
            """, ("room", room), ("owner", owner), ("seq", sequence));
        await reset.ExecuteNonQueryAsync();
    }

    private static async Task<bool?> RepeatedNoticeChoice(DbConnection db, DbTransaction tx, string room, string owner, long since)
    {
        // One effective instruction per distinct task. Repeats, retries and edits cannot manufacture a pattern.
        await using var query = Command(db, tx, """
            SELECT o.include_objective FROM chat_goal_notice_overrides o JOIN chat_messages m
              ON m.room_id=o.room_id AND m.seq=o.source_seq AND m.sender_id=o.owner_id
            WHERE o.room_id=@room AND o.owner_id=@owner AND o.source_seq>@since AND NOT m.edited AND NOT m.deleted
            ORDER BY o.source_seq DESC LIMIT 3
            """, ("room", room), ("owner", owner), ("since", since));
        await using var r = await query.ExecuteReaderAsync();
        var evidence = new List<bool>(3);
        while (await r.ReadAsync()) evidence.Add(r.GetBoolean(0));
        return evidence.Count == 3 && evidence.All(value => value == evidence[0]) ? evidence[0] : null;
    }

    private static async Task<string> SetTaskNotice(DbConnection db, DbTransaction tx, string room, string owner,
        long sequence, long? replyTo, List<BotWork> goals, bool include)
    {
        if (replyTo is null) goals = goals.Where(ActiveForMemory).ToList();
        if (goals is not [var goal])
            return "请回复一个尚未结束的任务卡说明本次通知格式。本次要求只适用于所选任务。";
        bool feedback = goal.Status == "completed" && replyTo is { } reply && await IsCompletionNotice(db, tx, goal, reply);
        if (!feedback && !ActiveForMemory(goal))
            return "此任务已经结束。可以回复它的完成通知，告诉我你希望怎样汇报。";
        await using var update = Command(db, tx, """
            INSERT INTO chat_goal_notice_overrides(work_id,room_id,owner_id,source_seq,include_objective)
            VALUES(@work,@room,@owner,@seq,@include)
            ON DUPLICATE KEY UPDATE source_seq=@seq,include_objective=@include
            """, ("work", goal.Id.ToString()), ("room", room), ("owner", owner), ("seq", sequence), ("include", include));
        await update.ExecuteNonQueryAsync();
        string response = feedback ? $"已记录你对任务 #{goal.Sequence} 完成通知的反馈。" :
            $"仅本次任务（消息 #{goal.Sequence}）的完成通知{(include ? "会附上目标" : "会简洁报告结果")}。";
        var review = await ReadPreferenceReview(db, tx, room, owner);
        bool? proposed = await RepeatedNoticeChoice(db, tx, room, owner, review.Since);
        if (proposed == await ReadGoalPreference(db, tx, room, owner)) proposed = null;
        if (proposed == review.IncludeGoal) return response;
        await using var record = Command(db, tx, """
            INSERT INTO chat_goal_preference_reviews(room_id,owner_id,since_seq,pending_seq,include_objective)
            VALUES(@room,@owner,0,@seq,@include)
            ON DUPLICATE KEY UPDATE pending_seq=@seq,include_objective=@include
            """, ("room", room), ("owner", owner), ("seq", proposed.HasValue ? sequence : (long?)null), ("include", proposed));
        await record.ExecuteNonQueryAsync();
        return proposed is bool value ? response +
            $"\n你最近三项任务都选了{(value ? "附上目标" : "简洁通知")}，以后也默认这样吗？可回复“是”或“不用”，也可以发送“确认偏好”或“拒绝偏好”。" : response;
    }

    private static async Task<bool> IsCompletionNotice(DbConnection db, DbTransaction tx, BotWork work, long reply)
    {
        await using var query = Command(db, tx, """
            SELECT 1 FROM chat_messages m JOIN chat_goal_outbox o ON o.room_id=m.room_id AND o.id=m.client_id
            JOIN chat_accounts a ON a.id=m.sender_id AND a.name=@robot
            WHERE m.room_id=@room AND m.seq=@reply AND NOT m.deleted AND NOT m.edited AND o.work_id=@work
              AND o.body LIKE @completion
            """, ("room", work.Room), ("reply", reply), ("work", work.Id.ToString()), ("robot", BotName),
            ("completion", $"任务 v{work.Goal!.Version}：{GoalStatusText("completed")}%"));
        return await query.ExecuteScalarAsync() is not null;
    }

    private static async Task<string?> ReviewPreference(DbConnection db, DbTransaction tx, string room, string owner,
        long sequence, long? replyTo, bool accept, bool explicitAnswer)
    {
        var review = await ReadPreferenceReview(db, tx, room, owner);
        if (review.Proposal is not { } proposedAt || review.IncludeGoal is not bool include)
            return explicitAnswer ? "没有待确认的通知偏好建议。" : null;
        if (!explicitAnswer || replyTo is not null)
        {
            // A conversational “yes” belongs to a specific delivered suggestion. Other questions and approvals
            // can be interleaved in this same private chat.
            await using var anchor = Command(db, tx, """
                SELECT 1 FROM chat_messages m JOIN chat_goal_outbox o ON o.room_id=m.room_id AND o.id=m.client_id
                JOIN chat_accounts a ON a.id=m.sender_id AND a.name=@robot
                WHERE m.room_id=@room AND NOT m.deleted AND NOT m.edited AND o.reply_to=@pending AND o.work_id IS NULL
                  AND m.seq=COALESCE(@reply,(SELECT MAX(prev.seq) FROM chat_messages prev
                      WHERE prev.room_id=@room AND prev.seq<@seq AND NOT prev.deleted))
                """, ("room", room), ("robot", BotName), ("pending", proposedAt), ("reply", replyTo), ("seq", sequence));
            if (await anchor.ExecuteScalarAsync() is null)
                return explicitAnswer ? "请回复当前的偏好建议，或单独发送“确认偏好”或“拒绝偏好”。" : null;
        }
        if (accept && await RepeatedNoticeChoice(db, tx, room, owner, review.Since) == include)
            return await SavePreference(db, tx, room, owner, sequence, include);
        await ResetPreferenceReview(db, tx, room, owner, sequence);
        return accept ? "来源记录已变化，已撤下这项偏好建议。" : "已撤下偏好建议，之后的任务继续使用原通知设置。";
    }
}
