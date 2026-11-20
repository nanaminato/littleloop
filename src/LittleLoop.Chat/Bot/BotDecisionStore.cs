using System.Data.Common;
using LittleLoop.Core;

// Stored in the existing job row, atomically with the goal, budget and next action.
sealed record BotDispatch(Guid Id, string Harness, string Instruction, string? RecoveryKey = null);
sealed record BotGoalLoop(int Used, int Limit, BotDispatch? Dispatch = null, BotObservation? Next = null);
sealed record DecisionInput(long? Inbox, string Room, string Owner, long Sequence, long? ReplyTo,
    BotObservation Trigger, string? Citation = null);
sealed record DecisionSnapshot(DecisionInput Input, BotDecisionContext Context, long Revision);

sealed partial class ChatStore
{
    private static async Task UpgradeBotDecisions(DbConnection db)
    {
        await using var cmd = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_bot_events (
                id BIGINT AUTO_INCREMENT PRIMARY KEY, room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL,
                seq BIGINT NOT NULL, work_id CHAR(36) NULL, kind VARCHAR(24) NOT NULL,
                observation JSON NOT NULL, detail JSON NULL, INDEX(room_id,owner_id,id), INDEX(work_id,id)
            );
            CREATE TABLE IF NOT EXISTS chat_bot_local_results (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL, seq BIGINT NOT NULL, observation JSON NOT NULL,
                PRIMARY KEY(room_id,seq), FOREIGN KEY(room_id,seq) REFERENCES chat_messages(room_id,seq)
            );
            CREATE TABLE IF NOT EXISTS chat_bot_inflight (
                dispatch_id CHAR(36) PRIMARY KEY, work_id CHAR(36) NOT NULL UNIQUE, claimed_work JSON NOT NULL,
                FOREIGN KEY(work_id) REFERENCES chat_bot_work(id)
            );
            CREATE TABLE IF NOT EXISTS chat_bot_memories (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL, source_seq BIGINT NOT NULL, body TEXT NOT NULL,
                PRIMARY KEY(room_id,owner_id,source_seq), FOREIGN KEY(room_id,source_seq) REFERENCES chat_messages(room_id,seq)
            );
            CREATE TABLE IF NOT EXISTS chat_bot_report_settings (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL, enabled BOOLEAN NOT NULL,
                PRIMARY KEY(room_id,owner_id)
            );
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<BotObservation[]> GoalObservations(Guid id)
    {
        await using var db = await Open();
        await using var query = Command(db, null, "SELECT observation FROM chat_bot_events WHERE work_id=@id ORDER BY id DESC LIMIT 128",
            ("id", id.ToString()));
        var events = new List<BotObservation>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) events.Add(FromJson<BotObservation>(reader.GetString(0)));
        events.Reverse();
        return events.ToArray();
    }

    public async Task<DecisionInput?> NextBotInput(bool goal)
    {
        await using var db = await Open();
        if (goal)
        {
            await using (var local = Command(db, null, "SELECT room_id,owner_id,seq,observation FROM chat_bot_local_results ORDER BY seq LIMIT 1"))
            await using (var localReader = await local.ExecuteReaderAsync())
                if (await localReader.ReadAsync()) return new(null, localReader.GetString(0), localReader.GetString(1), localReader.GetInt64(2),
                    null, FromJson<BotObservation>(localReader.GetString(3)));
            await using var next = Command(db, null, """
                SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work
                WHERE (status='deciding' OR (status='needs-reconciliation'
                    AND JSON_UNQUOTE(JSON_EXTRACT(input,'$.loop.next.status'))='uncertain'))
                AND NOT EXISTS(SELECT 1 FROM chat_bot_inflight f WHERE f.work_id=chat_bot_work.id)
                AND NOT EXISTS(SELECT 1 FROM chat_goal_inbox i WHERE i.room_id=chat_bot_work.room_id
                    AND i.owner_id=chat_bot_work.owner_id AND NOT i.processed)
                ORDER BY created,id LIMIT 1
                """);
            await using var r = await next.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            var work = ReadWork(r);
            return new(null, work.Room, work.Owner, work.Sequence, null, work.Input.Loop!.Next!,
                work.Input.Source?.Text ?? work.Input.Anchor);
        }
        await using var cmd = Command(db, null, """
            SELECT i.id,i.room_id,i.owner_id,i.seq,i.reply_to,i.body,m.card
            FROM chat_goal_inbox i JOIN chat_messages m ON m.room_id=i.room_id AND m.seq=i.seq
            WHERE NOT i.processed ORDER BY i.id LIMIT 1
            """);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var card = reader.IsDBNull(6) ? null : FromJson<MessageCard>(reader.GetString(6));
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), new(BotEventKind.UserMessage, reader.GetString(5),
                Status: card?.Kind, Sequence: reader.GetInt64(3)), Citation: card?.Source is { } citation
                    ? $"{citation.Path}:{citation.Start}–{citation.End}\n{citation.Text}" : null);
    }

    private static async Task<bool> Permitted(DbConnection db, DbTransaction tx, string room, string owner,
        string robot, bool operatorAccess, string? type)
    {
        var access = type == "direct" ? await ReadBotFriendAccess(db, tx, owner, robot, room)
            : BotFriendAccessState.Unmanaged;
        if (access != BotFriendAccessState.Active && !(access == BotFriendAccessState.Unmanaged && operatorAccess)) return false;
        await using var enabled = Command(db, tx, """
            SELECT 1 FROM chat_bot_rooms b JOIN chat_members u ON u.room_id=b.room_id AND u.user_id=@user
            JOIN chat_members r ON r.room_id=b.room_id AND r.user_id=@robot
            WHERE b.room_id=@room AND b.enabled
            """, ("user", owner), ("robot", robot), ("room", room));
        return await enabled.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> ConsumeInbox(DbConnection db, DbTransaction tx, long id)
    {
        await using var cmd = Command(db, tx, "UPDATE chat_goal_inbox SET processed=TRUE WHERE id=@id AND NOT processed", ("id", id));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    private static async Task<bool> ConsumeLocalResult(DbConnection db, DbTransaction tx, DecisionInput input)
    {
        await using var cmd = Command(db, tx, "DELETE FROM chat_bot_local_results WHERE room_id=@room AND owner_id=@owner AND seq=@seq",
            ("room", input.Room), ("owner", input.Owner), ("seq", input.Sequence));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    private static async Task<BotWork?> LockedWork(DbConnection db, DbTransaction tx, Guid id, string room, string owner)
    {
        await using var cmd = Command(db, tx, """
            SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work
            WHERE id=@id AND room_id=@room AND owner_id=@owner FOR UPDATE
            """, ("id", id.ToString()), ("room", room), ("owner", owner));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadWork(reader) : null;
    }

    private static async Task Record(DbConnection db, DbTransaction tx, string room, string owner, long seq,
        BotObservation observation, string? detail = null)
    {
        await using var cmd = Command(db, tx, """
            INSERT INTO chat_bot_events(room_id,owner_id,seq,work_id,kind,observation,detail)
            VALUES(@room,@owner,@seq,@work,@kind,@observation,@detail)
            """, ("room", room), ("owner", owner), ("seq", seq), ("work", observation.GoalId?.ToString()),
            ("kind", observation.Kind.ToString()), ("observation", ToJson(observation)), ("detail", detail));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DecisionSnapshot?> DecisionContext(DecisionInput input, string robot, bool operatorAccess,
        string grant, string[] harnesses, int limit, bool reportDefault, string? style)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var type = await LockGoalRoom(db, tx, input.Room);
        if (!await Permitted(db, tx, input.Room, input.Owner, robot, operatorAccess, type))
        {
            if (input.Inbox is long inbox) await ConsumeInbox(db, tx, inbox);
            else if (input.Trigger.Kind == BotEventKind.ToolResult) await ConsumeLocalResult(db, tx, input);
            else if (input.Trigger.GoalId is Guid id && await LockedWork(db, tx, id, input.Room, input.Owner) is { Status: "deciding" or "needs-reconciliation" } old)
                await SaveGoal(db, tx, old, old.Goal! with { Status = "disabled" }, input.Sequence, "disabled");
            await tx.CommitAsync();
            return null;
        }

        if (input.Inbox.HasValue || input.Trigger.Kind == BotEventKind.ToolResult)
        {
            await using var source = Command(db, tx, "SELECT 1 FROM chat_messages WHERE room_id=@room AND seq=@seq AND sender_id=@owner AND NOT deleted AND NOT edited",
                ("room", input.Room), ("seq", input.Sequence), ("owner", input.Owner));
            if (await source.ExecuteScalarAsync() is null)
            {
                if (input.Inbox is long obsolete) await ConsumeInbox(db, tx, obsolete);
                else await ConsumeLocalResult(db, tx, input);
                await tx.CommitAsync();
                return null;
            }
        }
        await using var revision = Command(db, tx, "SELECT revision FROM chat_rooms WHERE id=@room", ("room", input.Room));
        long roomRevision = Convert.ToInt64(await revision.ExecuteScalarAsync());
        Guid? quotedGoal = null;
        string? quote = input.Citation;
        if (input.ReplyTo is long parent)
        {
            await using var cmd = Command(db, tx, "SELECT body,card FROM chat_messages WHERE room_id=@room AND seq=@seq AND NOT deleted",
                ("room", input.Room), ("seq", parent));
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                quote = r.GetString(0) + (quote is null ? "" : "\n" + quote);
                quotedGoal = r.IsDBNull(1) ? null : FromJson<MessageCard>(r.GetString(1)).TaskId;
            }
        }
        var goals = new List<BotGoalSnapshot>();
        await using (var cmd = Command(db, tx, """
            SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work
            WHERE room_id=@room AND owner_id=@owner AND goal IS NOT NULL
            AND (JSON_EXTRACT(input,'$.provider') IS NULL OR JSON_UNQUOTE(JSON_EXTRACT(input,'$.provider'))='littleloop')
            ORDER BY CASE WHEN id=@target THEN 0 WHEN status IN ('deciding','queued','running','paused','waiting-input') THEN 1 ELSE 2 END,
                seq DESC LIMIT 24
            """, ("room", input.Room), ("owner", input.Owner), ("target", (input.Trigger.GoalId ?? quotedGoal)?.ToString())))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var w = ReadWork(r);
                goals.Add(new(w.Id, w.Goal!, w.Input.Loop is { } loop ? loop.Limit - loop.Used : limit));
            }

        var history = new List<BotObservation>();
        await using (var cmd = Command(db, tx, """
            SELECT e.observation FROM chat_bot_events e JOIN chat_messages m ON m.room_id=e.room_id AND m.seq=e.seq
            WHERE e.room_id=@room AND e.owner_id=@owner AND NOT m.deleted AND NOT m.edited
            ORDER BY CASE WHEN e.work_id=@target THEN 0 ELSE 1 END,e.id DESC LIMIT 32
            """, ("room", input.Room), ("owner", input.Owner), ("target", (input.Trigger.GoalId ?? quotedGoal)?.ToString())))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) history.Add(FromJson<BotObservation>(r.GetString(0)));
        history.Reverse();
        if (history.Count < 32)
        {
            // Earlier releases stored ordinary exchanges as chat jobs before the event ledger existed.
            await using var old = Command(db, tx, """
                SELECT w.seq,q.body,a.body FROM chat_bot_work w
                JOIN chat_messages q ON q.room_id=w.room_id AND q.seq=w.seq AND q.sender_id=w.owner_id
                JOIN chat_messages a ON a.room_id=w.room_id AND a.client_id=w.id AND a.sender_id=@robot
                WHERE w.room_id=@room AND w.owner_id=@owner AND w.kind='chat' AND w.status='completed' AND w.seq<@seq
                  AND NOT q.deleted AND NOT a.deleted AND NOT q.edited AND NOT a.edited
                ORDER BY w.seq DESC LIMIT 6
                """, ("room", input.Room), ("owner", input.Owner), ("robot", robot), ("seq", input.Sequence));
            await using var r = await old.ExecuteReaderAsync();
            while (await r.ReadAsync())
                history.InsertRange(0, [new(BotEventKind.UserMessage, Cut(r.GetString(1), 1200), Sequence: r.GetInt64(0)),
                    new(BotEventKind.HarnessReceipt, Cut(r.GetString(2), 1200), Status: "historical-chat", Sequence: r.GetInt64(0))]);
        }
        var memory = new List<string>();
        if (style is not null) memory.Add(style);
        if (type == "direct")
        {
            if (await ReadConversationNote(db, tx, input.Room, input.Owner) is { } note) memory.Add(note);
            if (!await LessonsPaused(db, tx, input.Room, input.Owner))
                foreach (var lesson in await LoadGoalLessons(db, tx, input.Room, input.Owner, grant, 12))
                    memory.Add($"#{lesson.SourceSequence} {lesson.Context} · {LessonDescription(lesson)}");
            await using var cmd = Command(db, tx, """
                SELECT p.source_seq,p.body FROM chat_bot_memories p JOIN chat_messages m ON m.room_id=p.room_id AND m.seq=p.source_seq
                WHERE p.room_id=@room AND p.owner_id=@owner AND NOT m.deleted AND NOT m.edited
                ORDER BY p.source_seq DESC LIMIT 16
                """, ("room", input.Room), ("owner", input.Owner));
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) memory.Add($"preference #{r.GetInt64(0)} {r.GetString(1)}");
        }
        await using var pref = Command(db, tx, "SELECT enabled FROM chat_bot_report_settings WHERE room_id=@room AND owner_id=@owner",
            ("room", input.Room), ("owner", input.Owner));
        bool report = await pref.ExecuteScalarAsync() is { } value ? Convert.ToBoolean(value) : reportDefault;
        await tx.CommitAsync();
        return new(input, new(input.Trigger, goals.ToArray(), history.ToArray(), memory.ToArray(), harnesses, report,
            quotedGoal, quote), roomRevision);
    }
}
