using System.Data.Common;
using LittleLoop.Core;
using static ChatStore;

// Reconstruct pre-decision-engine inputs for the original storage and migration fixtures.
static class HistoricalGoalInbox
{
    public static async Task<bool> ProcessGoalMessage(this ChatStore store, string robot, Func<string, bool> canOperate, string? grantHash = null,
        bool nativePrivateChat = false)
    {
        await using var db = await store.Open();
        // The single worker processes inbox messages. All goal writers lock room, then work.
        long inbox, seq; string room, user, text; long? replyTo;
        await using (var select = Command(db, null, "SELECT id,room_id,seq,owner_id,body,reply_to FROM chat_goal_inbox WHERE NOT processed ORDER BY id LIMIT 1"))
        await using (var r = await select.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync()) return false;
            inbox = r.GetInt64(0); room = r.GetString(1); seq = r.GetInt64(2); user = r.GetString(3);
            text = r.GetString(4); replyTo = r.IsDBNull(5) ? null : r.GetInt64(5);
        }
        await using var tx = await db.BeginTransactionAsync();
        var roomType = await LockGoalRoom(db, tx, room);
        await using var done = Command(db, tx, "UPDATE chat_goal_inbox SET processed=TRUE WHERE id=@id AND NOT processed", ("id", inbox));
        if (await done.ExecuteNonQueryAsync() != 1) return true;
        await using var installed = Command(db, tx, "SELECT enabled FROM chat_bot_rooms WHERE room_id=@room FOR SHARE", ("room", room));
        var access = roomType == "direct"
            ? await ReadBotFriendAccess(db, tx, user, robot, room) : BotFriendAccessState.Unmanaged;
        bool permitted = access == BotFriendAccessState.Active || access == BotFriendAccessState.Unmanaged && canOperate(user);
        if (!permitted || !await IsMember(db, tx, room, user) || !await IsMember(db, tx, room, robot) ||
            await installed.ExecuteScalarAsync() is not { } enabled || !Convert.ToBoolean(enabled))
        { await tx.CommitAsync(); return true; }

        if (roomType == "direct" && replyTo is null &&
            await HandleConversationNote(db, tx, room, user, seq, text) is { } noteReply)
        {
            await AddGoalNotice(db, tx, room, seq, noteReply, null);
            await tx.CommitAsync();
            return true;
        }

        Guid? target = null;
        if (replyTo is { } reply)
        {
            await using var source = Command(db, tx, "SELECT card FROM chat_messages WHERE room_id=@room AND seq=@seq AND NOT deleted", ("room", room), ("seq", reply));
            if (await source.ExecuteScalarAsync() is string raw) target = FromJson<MessageCard>(raw).TaskId;
        }
        if (nativePrivateChat && roomType == "direct" && target is null && !ExplicitProjectCommand(text))
        {
            // Natural requests go to the native agent, including requests which mention files or tools.
            await InsertJob(db, tx, new(Guid.NewGuid(), room, user, seq, "chat", "queued", new(text)));
            await tx.CommitAsync();
            return true;
        }
        var candidates = new List<BotWork>();
        await using (var select = Command(db, tx, """
            SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work
            WHERE room_id=@room AND owner_id=@user AND goal IS NOT NULL
              AND ((@target IS NOT NULL AND id=@target) OR (@unanchored=TRUE AND status NOT IN ('completed','cancelled','failed','needs-reconciliation','disabled')))
            ORDER BY created,id FOR UPDATE
            """, ("room", room), ("user", user), ("target", target?.ToString()), ("unanchored", replyTo is null)))
        await using (var r = await select.ExecuteReaderAsync())
            while (await r.ReadAsync()) candidates.Add(ReadWork(r));
        if (await HandleGoalMemory(db, tx, room, roomType, user, seq, replyTo, text, candidates, grantHash) is { } memoryReply)
        {
            await AddGoalNotice(db, tx, room, seq, memoryReply, null);
            await tx.CommitAsync();
            return true;
        }
        bool activeTarget = candidates is [var selected] && ActiveForMemory(selected);
        var intent = GoalMessageRouter.Parse(text, candidates.Count > 0, privateChat: roomType == "direct",
            taskReply: target is not null && activeTarget, awaitingInput: candidates is [{ Status: "waiting-input" }]);
        BotInput? continuation = null;
        bool continueWaiting = intent.Action == "resume" && text.Trim().TrimEnd('。', '！', '!') is "继续" or "继续吧" or "接着做吧";
        if (roomType == "direct" && candidates is [{ Status: "waiting-input" } pending] &&
            (continueWaiting || target is not null && intent.Action == "chat" || intent.Action == "revise" &&
                GoalMessageRouter.Parse(text, false, privateChat: true).Action == "chat"))
            continuation = await ReadEarlierChatRequest(db, tx, pending, continueWaiting ? text.Trim() : intent.Text);
        if (intent.Action == "chat" || continuation is not null)
        {
            await InsertJob(db, tx, new(Guid.NewGuid(), room, user, seq, "chat", "queued", continuation ?? new(intent.Text)));
            await tx.CommitAsync();
            return true;
        }
        // A plain progress question still works after completion/cancellation. Explicit replies
        // must resolve their own task; never silently redirect them to another active goal.
        if (intent.Action == "status" && replyTo is null && candidates.Count == 0)
        {
            await using var latest = Command(db, tx, """
                SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work
                WHERE room_id=@room AND owner_id=@user AND goal IS NOT NULL
                ORDER BY seq DESC LIMIT 1 FOR UPDATE
                """, ("room", room), ("user", user));
            await using var reader = await latest.ExecuteReaderAsync();
            if (await reader.ReadAsync()) candidates.Add(ReadWork(reader));
        }
        BotWork? work = candidates.Count == 1 ? candidates[0] : null;
        string response;
        try
        {
            if (intent.Action is "help" or "reply")
            {
                work = null;
                response = intent.Action == "reply" ? intent.Text : "你想让我处理什么？如果是在说群里已有的任务，可以直接回复对应的任务。";
            }
            else if (intent.Action == "create")
            {
                var goal = ContinuousGoal.Create(intent.Text);
                work = new(Guid.NewGuid(), room, user, seq, "task", goal.Status, new(goal.Prompt), Goal: goal);
                await InsertJob(db, tx, work);
                await SaveGoal(db, tx, work, goal, seq, "create");
                response = goal.Status == "waiting-input" ? "你想让我做什么？" : "收到，我开始处理。";
            }
            else if (intent.Action == "status" && candidates.Count > 1)
            {
                var overview = candidates.Take(8).Select(item =>
                {
                    string objective = item.Goal!.Objective;
                    string summary = objective.Length == 0 ? "等待补充目标" :
                        objective.Length > 96 ? objective[..96] + "…" : objective;
                    return $"任务消息 #{item.Sequence} · {GoalStatusText(item.Status)} · {summary}";
                });
                response = $"当前有 {candidates.Count} 项未结束任务。回复对应任务卡可查看或操作单项。\n" +
                    string.Join("\n", overview);
            }
            else if (work is null) response = candidates.Count > 1
                ? "你有多个进行中的任务，请回复要操作的那一项。" : "我没找到对应的任务。如果你指的是之前的任务，可以回复那条消息。";
            else
            {
                var goal = work.Goal!.Change(intent.Action, intent.Text, work.Goal.Version);
                if (intent.Action != "status") await SaveGoal(db, tx, work, goal, seq, intent.Action);
                work = work with { Goal = goal, Status = goal.Status };
                response = intent.Action switch
                {
                    "pause" => "已请求暂停；已发出的操作先收取结果，随后停止推进。说“恢复”可继续同一任务。",
                    "resume" => "已恢复任务，将沿用已有执行记录和剩余预算继续。",
                    "cancel" => "已取消后续工作；已发生的操作不会自动撤销。",
                    "revise" => goal.Status == "paused" ? "已保存补充要求，任务保持暂停。" : "已保存补充要求，将按新版本继续；旧回执不会覆盖新目标。",
                    _ => "当前状态：" + GoalStatusText(goal.Status)
                };
            }
        }
        catch (InvalidOperationException e) { response = e.Message; }
        if (work is not null)
        {
            await using var card = Command(db, tx, "UPDATE chat_messages SET card=@card WHERE room_id=@room AND seq=@seq",
                ("card", ToJson(new MessageCard("task", TaskId: work.Id))), ("room", room), ("seq", seq));
            await card.ExecuteNonQueryAsync();
            if (intent.Action == "status") response += $"\n目标 v{work.Goal!.Version}：{work.Goal.Objective}";
        }
        await AddGoalNotice(db, tx, room, seq, response, work?.Id);
        await tx.CommitAsync(); return true;
    }

    private static bool ExplicitProjectCommand(string text)
    {
        string command = text.Trim();
        return command is "/task" or "/status" or "/pause" or "/resume" or "/cancel" ||
            command.StartsWith("/task ", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("/revise ", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("开发任务：", StringComparison.Ordinal) ||
            command.StartsWith("开发任务:", StringComparison.Ordinal);
    }

    // Previous versions sent general requests through development when the sender used “任务：”.
    // The original inbox row distinguishes those messages from a deliberately scheduled project job.
    private static async Task<BotInput?> ReadEarlierChatRequest(DbConnection db, DbTransaction tx, BotWork pending, string reply)
    {
        await using var cmd = Command(db, tx, """
            SELECT i.body, m.deleted, m.edited FROM chat_goal_inbox i
            JOIN chat_messages m ON m.room_id=i.room_id AND m.seq=i.seq AND m.sender_id=i.owner_id
            WHERE i.room_id=@room AND i.seq=@seq AND i.owner_id=@owner AND i.processed
            """, ("room", pending.Room), ("seq", pending.Sequence), ("owner", pending.Owner));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var original = GoalMessageRouter.Parse(reader.GetString(0), false, privateChat: true);
        if (original.Action != "chat" || original.Text != pending.Goal!.Objective) return null;
        string? context = reader.GetBoolean(1) || reader.GetBoolean(2) ? null : GoalMemoryExcerpt(original.Text);
        return new(reply, context);
    }

}
