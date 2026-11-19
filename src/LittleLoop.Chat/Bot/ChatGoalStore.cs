using LittleLoop.Core;
using System.Data.Common;

sealed record GoalNotice(string Id, string Room, long ReplyTo, string Text, Guid? TaskId);

sealed partial class ChatStore
{
    private async Task UpgradeGoals(DbConnection db)
    {
        string outboxId = sqlite ? "id CHAR(36) NOT NULL UNIQUE" : "id CHAR(36) PRIMARY KEY";
        string outboxOrder = sqlite ? "created INTEGER PRIMARY KEY AUTOINCREMENT" : "created BIGINT AUTO_INCREMENT UNIQUE";
        await using var cmd = Command(db, null, $"""
            CREATE TABLE IF NOT EXISTS chat_goal_inbox (
                id BIGINT AUTO_INCREMENT PRIMARY KEY, room_id CHAR(36) NOT NULL, seq BIGINT NOT NULL,
                owner_id CHAR(36) NOT NULL, body TEXT NOT NULL, reply_to BIGINT NULL,
                processed BOOLEAN NOT NULL DEFAULT FALSE, UNIQUE(room_id,seq), INDEX(processed,id)
            );
            CREATE TABLE IF NOT EXISTS chat_goal_events (
                id BIGINT AUTO_INCREMENT PRIMARY KEY, work_id CHAR(36) NOT NULL, version INT NOT NULL,
                room_id CHAR(36) NOT NULL, seq BIGINT NOT NULL, action VARCHAR(24) NOT NULL, snapshot JSON NOT NULL
            );
            CREATE TABLE IF NOT EXISTS chat_goal_outbox (
                {outboxId}, room_id CHAR(36) NOT NULL, reply_to BIGINT NOT NULL,
                body TEXT NOT NULL, work_id CHAR(36) NULL, delivered BOOLEAN NOT NULL DEFAULT FALSE,
                {outboxOrder}, INDEX(delivered,created)
            );
            CREATE TABLE IF NOT EXISTS chat_goal_preferences (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL,
                include_objective BOOLEAN NOT NULL, source_seq BIGINT NOT NULL,
                PRIMARY KEY(room_id,owner_id),
                FOREIGN KEY(room_id) REFERENCES chat_rooms(id),
                FOREIGN KEY(owner_id) REFERENCES chat_accounts(id)
            );
            CREATE TABLE IF NOT EXISTS chat_goal_experience_settings (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL,
                PRIMARY KEY(room_id,owner_id)
            );
            CREATE TABLE IF NOT EXISTS chat_goal_experiences (
                work_id CHAR(36) PRIMARY KEY, room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL,
                source_version INT NOT NULL, grant_hash CHAR(64) NOT NULL, goal_hash CHAR(64) NOT NULL, paths JSON NOT NULL,
                INDEX(room_id,owner_id,grant_hash,goal_hash),
                FOREIGN KEY(work_id) REFERENCES chat_bot_work(id)
            );
            """);
        await cmd.ExecuteNonQueryAsync();
        await UpgradeConversationMemory(db);
    }

    private static async Task EnqueueGoalMessage(DbConnection db, DbTransaction tx, string room,
        string type, string user, long seq, PostMessage input, string robot)
    {
        if (type is not ("direct" or "group" or "topic") || !await IsMember(db, tx, room, robot)) return;
        await using var enabled = Command(db, tx, "SELECT enabled FROM chat_bot_rooms WHERE room_id=@room FOR SHARE", ("room", room));
        if (await enabled.ExecuteScalarAsync() is not { } value || !Convert.ToBoolean(value)) return;
        bool addressed = type == "direct" || input.Text.TrimStart().StartsWith("@littleloop", StringComparison.OrdinalIgnoreCase);
        if (!addressed && input.ReplyTo is { } parent)
        {
            await using var reply = Command(db, tx, """
                SELECT card FROM chat_messages WHERE room_id=@room AND seq=@seq AND NOT deleted
                """, ("room", room), ("seq", parent));
            addressed = await reply.ExecuteScalarAsync() is string raw && FromJson<MessageCard>(raw).TaskId is not null;
        }
        if (!addressed) return;
        await using var insert = Command(db, tx, """
            INSERT INTO chat_goal_inbox(room_id,seq,owner_id,body,reply_to) VALUES(@room,@seq,@user,@body,@reply)
            """, ("room", room), ("seq", seq), ("user", user), ("body", input.Text), ("reply", input.ReplyTo));
        await insert.ExecuteNonQueryAsync();
    }

    public async Task<bool> ProcessGoalMessage(string robot, Func<string, bool> canOperate, string? grantHash = null)
    {
        await using var db = await Open();
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
        if (!canOperate(user) || !await IsMember(db, tx, room, user) || !await IsMember(db, tx, room, robot) ||
            await installed.ExecuteScalarAsync() is not { } enabled || !Convert.ToBoolean(enabled))
        { await tx.CommitAsync(); return true; }

        Guid? target = null;
        if (replyTo is { } reply)
        {
            await using var source = Command(db, tx, "SELECT card FROM chat_messages WHERE room_id=@room AND seq=@seq AND NOT deleted", ("room", room), ("seq", reply));
            if (await source.ExecuteScalarAsync() is string raw) target = FromJson<MessageCard>(raw).TaskId;
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
        var intent = GoalMessageRouter.Parse(text, candidates.Count > 0, privateDocumentRequests: roomType == "direct");
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
            if (intent.Action == "help")
            {
                work = null;
                response = "可以发送“任务：具体目标和验收要求”，或说“帮我修复…”；回复任务卡可补充要求、查询进度、暂停、恢复或取消。";
            }
            else if (intent.Action == "create")
            {
                var goal = ContinuousGoal.Create(intent.Text);
                work = new(Guid.NewGuid(), room, user, seq, "task", goal.Status, new(goal.Prompt), Goal: goal);
                await InsertJob(db, tx, work);
                await SaveGoal(db, tx, work, goal, seq, "create");
                response = goal.Status == "waiting-input" ? "请补充具体要完成什么，以及如何判断完成。" : "已接收任务，将在现有权限和预算内持续推进。";
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
                ? "你有多个进行中的任务，请回复要操作的任务卡。" : "未找到你在此会话中可操作的目标。请回复自己的任务卡，或发送“任务：具体目标”。";
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
            response += $"\n目标 v{work.Goal!.Version}：{work.Goal.Objective}";
        }
        await AddGoalNotice(db, tx, room, seq, response, work?.Id);
        await tx.CommitAsync(); return true;
    }

    private static async Task<string?> LockGoalRoom(DbConnection db, DbTransaction tx, string room)
    {
        await using var cmd = Command(db, tx, "SELECT type FROM chat_rooms WHERE id=@room FOR UPDATE", ("room", room));
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task SaveGoal(DbConnection db, DbTransaction tx, BotWork work, ContinuousGoal goal, long seq, string action,
        string? error = null, string? output = null)
    {
        await using var cmd = Command(db, tx, """
            UPDATE chat_bot_work SET status=@status,goal=@goal,input=@input,error=@error,output=@output WHERE id=@id;
            INSERT INTO chat_goal_events(work_id,version,room_id,seq,action,snapshot) VALUES(@id,@version,@room,@seq,@action,@goal);
            """, ("status", goal.Status), ("goal", ToJson(goal)), ("input", ToJson(work.Input with { Text = goal.Prompt })),
            ("error", error), ("output", output), ("id", work.Id.ToString()), ("version", goal.Version),
            ("room", work.Room), ("seq", seq), ("action", action));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> GoalShouldYield(BotWork work)
    {
        var latest = await ReadBotWork(work.Id, work.Owner);
        var room = await BotRoom(work.Room, work.Owner);
        return latest?.Goal is not { } goal || goal.Version != work.Goal!.Version || goal.Status != "running" || !room.Installed;
    }

    public async Task FinishGoal(BotWork executed, string status, string? error = null, string? output = null,
        VerifiedGoalExperience? experience = null)
    {
        await using var db = await Open(); await using var tx = await db.BeginTransactionAsync();
        var roomType = await LockGoalRoom(db, tx, executed.Room);
        ContinuousGoal goal;
        await using (var cmd = Command(db, tx, "SELECT goal FROM chat_bot_work WHERE id=@id FOR UPDATE", ("id", executed.Id.ToString())))
            goal = FromJson<ContinuousGoal>((string)(await cmd.ExecuteScalarAsync())!);
        // Unknown effects must block even a newer paused/revised version. Safe old results cannot complete it.
        var next = status == "needs-reconciliation" ? goal with { Status = status } : goal.Finish(executed.Goal!.Version, status);
        if (next != goal)
        {
            await SaveGoal(db, tx, executed with { Input = executed.Input with { Text = next.Prompt } }, next, executed.Sequence, "result", error, output);
            if (next.Status == "completed" && roomType == "direct" && experience is not null && SupportsGoalExperience(executed))
                await SaveGoalExperience(db, tx, executed, experience);
            bool includeObjective = next.Status == "completed" && roomType == "direct" &&
                await CompletionIncludesGoal(db, tx, executed);
            await AddGoalNotice(db, tx, executed.Room, executed.Sequence,
                $"任务 v{next.Version}：{GoalStatusText(next.Status)}" + (error is null ? "" : "\n" + error) +
                (next.Status == "waiting-input" && output is not null ? "\n" + output[..Math.Min(output.Length, 3000)] : "") +
                (includeObjective ? "\n任务目标：" + GoalMemoryExcerpt(next.Objective) : ""), executed.Id);
        }
        await tx.CommitAsync();
    }

    public async Task RecoverContinuousGoals(Func<BotWork, bool?>? recoverApprovalBoundary = null,
        Func<BotWork, bool?>? recoverReview = null)
    {
        await using var db = await Open(); var works = new List<BotWork>();
        await using (var cmd = Command(db, null, "SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work WHERE goal IS NOT NULL AND status IN ('running','paused')"))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) works.Add(ReadWork(r));
        foreach (var work in works)
        {
            var review = recoverReview?.Invoke(work);
            if (review == true)
            {
                // No editing or testing is allowed after this checkpoint. Preserve user pauses.
                if (work.Status == "running")
                    await FinishGoal(work, "queued", "服务已重启，将从只读审阅继续。已完成的补丁和测试不会重跑。");
                continue;
            }
            if (review == false)
            {
                await FinishGoal(work, "needs-reconciliation", "审阅记录与任务或源码不一致，请核对原测试回执。");
                continue;
            }
            var recovered = recoverApprovalBoundary?.Invoke(work);
            if (recovered == true && work.Status == "running")
                await FinishGoal(work, "paused", "服务重启，原审批已失效。恢复任务后将重新申请审批。");
            else if (recovered != true && (work.Status == "running" || recovered == false))
                await FinishGoal(work, "needs-reconciliation", "服务中断，执行结果需要核对；不会自动重跑未知操作。");
        }
    }

    private static async Task AddGoalNotice(DbConnection db, DbTransaction tx, string room, long reply, string text, Guid? work)
    {
        await using var cmd = Command(db, tx, "INSERT INTO chat_goal_outbox(id,room_id,reply_to,body,work_id) VALUES(@id,@room,@reply,@body,@work)",
            ("id", Guid.NewGuid().ToString()), ("room", room), ("reply", reply), ("body", text), ("work", work?.ToString()));
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<GoalNotice?> NextGoalNotice()
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "SELECT id,room_id,reply_to,body,work_id FROM chat_goal_outbox WHERE NOT delivered ORDER BY created LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4))) : null;
    }
    public async Task CompleteGoalNotice(string id)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "UPDATE chat_goal_outbox SET delivered=TRUE WHERE id=@id", ("id", id));
        await cmd.ExecuteNonQueryAsync();
    }
    internal static string GoalStatusText(string status) => status switch
    {
        "queued" => "等待调度", "running" => "正在工作（可能等待逐次授权）", "paused" => "暂停",
        "waiting-input" => "等待补充要求", "completed" => "已完成，实际修改与必需测试通过宿主验收",
        "cancelled" => "已取消", "needs-reconciliation" => "执行结果待核对", "suspended" => "已停止推进",
        "disabled" => "会话已停用机器人", _ => status
    };
}
