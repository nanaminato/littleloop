using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using LittleLoop.Tools;
using LittleLoop.Core;
using MySqlConnector;

sealed record MessageCard(string Kind, SourceCitation? Source = null, Guid? TaskId = null);
sealed record BotInput(string Text, string? Anchor = null, SourceCitation? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BotGoalLoop? Loop { get; init; }
    [JsonIgnore] public ChatExchange[] History { get; init; } = [];
    [JsonIgnore] public string? ConversationStyle { get; init; }
    [JsonIgnore] public string? ConversationNote { get; init; }
    [JsonIgnore] public Guid? ConversationWorkId { get; init; }
    [JsonIgnore] public string? ConversationWorkspace { get; init; }
}
sealed record ChatExchange(string User, string Assistant, bool Quoted);
sealed record BotWork(Guid Id, string Room, string Owner, long Sequence, string Kind, string Status, BotInput Input,
    string? Error = null, string? Output = null, ContinuousGoal? Goal = null)
{
    [JsonIgnore] public bool IsAnswer => Kind is "question" or "chat";
    [JsonIgnore]
    public GoalExperienceHint? ExperienceHint { get; init; }
    [JsonIgnore]
    public GoalLessonHint[] LessonHints { get; init; } = [];
    [JsonIgnore]
    public string? SemanticMatchQuote { get; init; }
}
sealed record BotInstall(bool Enabled);
sealed record BotAsk(Guid ClientId, string Text, string Path, int Start, int End, long? ReplyTo, string? Provider);
sealed record BotSchedule(Guid ClientId, long Sequence, string Goal, string? Provider);

sealed partial class ChatStore
{
    public const string BotName = "littleloop";
    public const string CommitmentBotName = "littleloop_synthetic";
    public const string ReviewBotName = "source_review";
    public const string DeepSeekBotName = "deepseek";
    public const string DeepSeekTestBotName = "deepseek_test";
    public const string DeepSeekProjectPrefix = "deepseek_project_";
    public const string CollaborationBotName = "littleloop_collab";
    public static bool IsProjectTaskOnly(string? name) => name == CollaborationBotName || IsDeepSeekProject(name);
    public static bool IsDeepSeekProject(string? name) => name?.StartsWith(DeepSeekProjectPrefix, StringComparison.Ordinal) == true;
    public static bool IsRobot(string name) => name is BotName or CommitmentBotName or ReviewBotName or DeepSeekBotName or DeepSeekTestBotName || IsProjectTaskOnly(name);
    private const string RobotCredential = "!robot:v1";
    private static readonly JsonSerializerOptions BotJson = new(JsonSerializerDefaults.Web);
    internal static string ToJson<T>(T item) => JsonSerializer.Serialize(item, BotJson);
    internal static T FromJson<T>(string raw) => JsonSerializer.Deserialize<T>(raw, BotJson)!;

    private static async Task UpgradeBot(DbConnection db)
    {
        await using var cmd = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_bot_rooms (
                room_id CHAR(36) PRIMARY KEY, enabled BOOLEAN NOT NULL,
                FOREIGN KEY(room_id) REFERENCES chat_rooms(id)
            );
            CREATE TABLE IF NOT EXISTS chat_bot_work (
                id CHAR(36) PRIMARY KEY, room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL,
                seq BIGINT NOT NULL, kind VARCHAR(10) NOT NULL, status VARCHAR(32) NOT NULL, input JSON NOT NULL,
                error VARCHAR(256) NULL, created DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                INDEX(status,created), FOREIGN KEY(room_id,seq) REFERENCES chat_messages(room_id,seq),
                FOREIGN KEY(owner_id) REFERENCES chat_accounts(id)
            );
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Account> GetRobot(string name)
    {
        await Register(new(Guid.NewGuid().ToString(), name, RobotCredential));
        var robot = await Find(name);
        return robot is { Password: RobotCredential } ? robot : throw new InvalidOperationException($"Account {name} is reserved.");
    }

    public async Task<string[]?> InstallRobot(string room, string user, string[] robots, bool enabled)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        if (!await IsMember(db, tx, room, user)) return null;
        await using (var kind = Command(db, tx, "SELECT type FROM chat_rooms WHERE id=@room FOR UPDATE", ("room", room)))
        {
            string? type = await kind.ExecuteScalarAsync() as string;
            if (type is not ("group" or "topic") && (type != "direct" || robots.Length != 1 || !await IsMember(db, tx, room, robots[0]))) return null;
        }
        await using (var write = Command(db, tx, """
            INSERT INTO chat_bot_rooms(room_id,enabled) VALUES(@room,@enabled)
            ON DUPLICATE KEY UPDATE enabled=@enabled
            """, ("room", room), ("enabled", enabled)))
            await write.ExecuteNonQueryAsync();
        if (enabled)
            foreach (string robot in robots) await AddMember(db, tx, room, robot);
        var recipients = await MemberIds(db, tx, room);
        await tx.CommitAsync();
        return recipients;
    }

    public async Task<bool> RobotIsMember(string room, string robot)
    {
        await using var db = await Open();
        return await IsMember(db, null, room, robot);
    }

    public async Task<ChatExchange[]> ConversationHistory(BotWork work, string robot)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT w.seq,question.body,answer.body,
                   CASE WHEN current_msg.reply_to IN (question.seq,answer.seq) THEN 1 ELSE 0 END AS quoted
            FROM chat_bot_work w
            JOIN chat_messages question ON question.room_id=w.room_id AND question.seq=w.seq AND question.sender_id=w.owner_id
            JOIN chat_messages answer ON answer.room_id=w.room_id AND answer.reply_to=w.seq AND answer.sender_id=@robot AND answer.client_id=w.id
            JOIN chat_messages current_msg ON current_msg.room_id=w.room_id AND current_msg.seq=@seq
            JOIN chat_members m ON m.room_id=w.room_id AND m.user_id=@owner
            WHERE w.room_id=@room AND w.owner_id=@owner AND w.kind='chat' AND w.status='completed' AND w.seq<@seq
                  AND NOT question.deleted AND NOT answer.deleted
            ORDER BY quoted DESC,w.seq DESC LIMIT 6
            """, ("room", work.Room), ("owner", work.Owner), ("robot", robot), ("seq", work.Sequence));
        var history = new List<(long Seq, ChatExchange Exchange)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            history.Add((reader.GetInt64(0), new(Excerpt(reader.GetString(1)), Excerpt(reader.GetString(2)), reader.GetBoolean(3))));
        return history.OrderBy(row => row.Seq).Select(row => row.Exchange).ToArray();

        static string Excerpt(string text) => text.Length <= 1200 ? text : text[..1200] + "…";
    }

    internal static async Task AddMember(DbConnection db, DbTransaction tx, string room, string robot)
    {
        await using var join = Command(db, tx, """
            INSERT INTO chat_members(room_id,user_id) VALUES(@room,@robot)
            ON DUPLICATE KEY UPDATE read_to=read_to
            """, ("room", room), ("robot", robot));
        await join.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> MemberIds(DbConnection db, DbTransaction tx, string room)
    {
        await using var select = Command(db, tx, "SELECT user_id FROM chat_members WHERE room_id=@room", ("room", room));
        var recipients = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync()) recipients.Add(reader.GetString(0));
        return recipients.ToArray();
    }

    public async Task<(bool Member, bool Installed, string? Type)> BotRoom(string room, string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT IFNULL(b.enabled,FALSE),r.type FROM chat_members m
            JOIN chat_rooms r ON r.id=m.room_id LEFT JOIN chat_bot_rooms b ON b.room_id=m.room_id
            WHERE m.room_id=@room AND m.user_id=@user
            """, ("room", room), ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (true, reader.GetBoolean(0), reader.GetString(1)) : (false, false, null);
    }

    public async Task<ChatMessage?> FindMessage(string room, long seq, string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, MessageSelect + """
             JOIN chat_members m ON m.room_id=msg.room_id AND m.user_id=@user
             WHERE msg.room_id=@room AND msg.seq=@seq AND NOT msg.deleted
            """, ("room", room), ("seq", seq), ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Message(reader, room) : null;
    }

    internal static async Task InsertJob(DbConnection db, DbTransaction tx, BotWork task)
    {
        await using var cmd = Command(db, tx, """
            INSERT INTO chat_bot_work(id,room_id,owner_id,seq,kind,status,input,error,goal)
            VALUES(@id,@room,@user,@seq,@kind,@status,@input,@error,@goal)
            """, ("id", task.Id.ToString()), ("room", task.Room), ("user", task.Owner), ("seq", task.Sequence),
            ("kind", task.Kind), ("status", task.Status), ("input", ToJson(task.Input)), ("error", task.Error),
            ("goal", task.Goal is null ? null : ToJson(task.Goal)));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DbConnection> AcquireBotLease()
    {
        if (sqlite)
        {
            var fileLock = AcquireSqliteLock("bot");
            try
            {
                var local = await Open();
                local.Disposed += (_, _) => fileLock.Dispose();
                return local;
            }
            catch { fileLock.Dispose(); throw; }
        }
        var settings = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        var db = new MySqlConnection(settings.ConnectionString);
        try
        {
            await db.OpenAsync();
            await using var cmd = Command(db, null, """
                SELECT GET_LOCK(SHA2(CONCAT('littleloop-chat-bot:',DATABASE()),256),0)
                """);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1) return db;
            throw new IOException("Another chat bot worker already owns this database.");
        }
        catch { await db.DisposeAsync(); throw; }
    }

    public async Task RecoverBotWork(DbConnection lease)
    {
        // A running native chat might have already called tools. Preserve uncertainty on restart.
        var retired = new List<(string Id, string Room, long Sequence, string Kind, string Status)>();
        await using (var lookup = Command(lease, null, """
            SELECT id,room_id,seq,kind,status FROM chat_bot_work
            WHERE kind IN ('setting','timezone','pair') AND status IN ('queued','running','needs-input')
            OR kind='chat' AND status='running'
            """))
        await using (var reader = await lookup.ExecuteReaderAsync())
            while (await reader.ReadAsync()) retired.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4)));
        foreach (var work in retired)
        {
            await using var tx = await lease.BeginTransactionAsync();
            await LockGoalRoom(lease, tx, work.Room);
            bool interrupted = work.Status == "running";
            string message = work.Kind == "chat" ? ChatConversation.FailureReply(ConversationFailure.Interrupted) : interrupted
                ? "旧版在处理这项本机操作时中断了，请核对实际结果。此请求已停止。"
                : "内置的本机操作已退役，此请求已结束。需要继续时，请使用有相应权限的 agent。";
            await using var mark = Command(lease, tx, """
                UPDATE chat_bot_work SET status=@next,error=@message WHERE id=@id AND status=@previous
                """, ("next", interrupted ? "needs-reconciliation" : "disabled"), ("message", message),
                ("id", work.Id), ("previous", work.Status));
            if (await mark.ExecuteNonQueryAsync() == 1)
                await AddGoalNotice(lease, tx, work.Room, work.Sequence, message, null);
            await tx.CommitAsync();
        }
        await using var cmd = Command(lease, null, """
            UPDATE chat_bot_work SET status='needs-reconciliation', error='进程曾中断，请核对执行记录。'
            WHERE status='running' AND goal IS NULL
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long?> PrivateConversationRevision(BotWork work)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT r.revision FROM chat_rooms r
            JOIN chat_messages m ON m.room_id=r.id AND m.seq=@seq AND m.sender_id=@owner
            WHERE r.id=@room AND r.type='direct' AND NOT m.deleted AND NOT m.edited
            """, ("room", work.Room), ("seq", work.Sequence), ("owner", work.Owner));
        return await cmd.ExecuteScalarAsync() is { } value ? Convert.ToInt64(value) : null;
    }

    public async Task<BotWork?> ClaimBotWork(DbConnection lease, Func<BotWork, bool>? canDispatch = null)
    {
        BotWork? work = null;
        string limit = canDispatch is null ? " LIMIT 1" : "";
        await using (var select = Command(lease, null, """
            SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal
            FROM chat_bot_work w WHERE status='queued' AND (kind<>'chat' OR NOT EXISTS(
                SELECT 1 FROM chat_bot_work older WHERE older.room_id=w.room_id AND older.owner_id=w.owner_id
                AND older.kind='chat' AND older.seq<w.seq AND older.status IN ('queued','running')))
            AND NOT EXISTS(SELECT 1 FROM chat_bot_inflight f WHERE f.work_id=w.id)
            AND (JSON_EXTRACT(w.input,'$.loop') IS NULL OR (
                NOT EXISTS(SELECT 1 FROM chat_goal_outbox o WHERE o.work_id=w.id AND NOT o.delivered)
                AND NOT EXISTS(SELECT 1 FROM chat_goal_inbox i WHERE i.room_id=w.room_id AND i.owner_id=w.owner_id AND NOT i.processed)))
            ORDER BY created,id
            """ + limit))
        {
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var candidate = ReadWork(reader);
                if (canDispatch is not null && !canDispatch(candidate)) continue;
                work = candidate;
                break;
            }
        }
        if (work is null) return null;
        var goal = work.Goal is null ? null : work.Goal with { Status = "running" };
        string sameGoal = sqlite ? "goal IS @oldgoal" : "goal <=> CAST(@oldgoal AS JSON)";
        var running = work with { Status = "running", Goal = goal };
        await using var tx = await lease.BeginTransactionAsync();
        await LockGoalRoom(lease, tx, work.Room);
        await using var update = Command(lease, tx, $"UPDATE chat_bot_work SET status='running',goal=@goal WHERE id=@id AND status='queued' AND ({sameGoal})",
            ("id", work.Id.ToString()), ("goal", goal is null ? null : ToJson(goal)), ("oldgoal", work.Goal is null ? null : ToJson(work.Goal)));
        if (await update.ExecuteNonQueryAsync() != 1) return null;
        if (work.Input.Loop?.Dispatch is { } dispatch)
        {
            await using var claimed = Command(lease, tx, "INSERT INTO chat_bot_inflight(dispatch_id,work_id,claimed_work) VALUES(@dispatch,@id,@work)",
                ("dispatch", dispatch.Id.ToString()), ("id", work.Id.ToString()), ("work", ToJson(running)));
            await claimed.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return running;
    }

    public async Task SetBotStatus(BotWork work, string state, string? error = null, string? output = null,
        VerifiedGoalExperience? experience = null)
    {
        if (work.Goal is not null) { await FinishGoal(work, state, error, output, experience); return; }
        await using var db = await Open();
        await using var cmd = Command(db, null, "UPDATE chat_bot_work SET status=@status,error=@error,output=@output WHERE id=@id AND status='running'",
            ("status", state), ("error", error), ("output", output), ("id", work.Id.ToString()));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<BotWork?> ReadBotWork(Guid id, string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT w.id,w.room_id,w.owner_id,w.seq,w.kind,w.status,w.input,w.error,w.output,w.goal
            FROM chat_bot_work w JOIN chat_members m ON m.room_id=w.room_id AND m.user_id=@user WHERE w.id=@id
            """, ("user", user), ("id", id.ToString()));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadWork(reader) : null;
    }

    public async Task<BotWork[]> RunningPrivateWork()
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT w.id,w.room_id,w.owner_id,w.seq,w.kind,w.status,w.input,w.error,w.output,w.goal
            FROM chat_bot_work w JOIN chat_rooms r ON r.id=w.room_id
            JOIN chat_bot_rooms b ON b.room_id=r.id AND b.enabled
            WHERE r.type='direct' AND w.kind='task' AND w.status='running'
            """);
        await using var reader = await cmd.ExecuteReaderAsync();
        var works = new List<BotWork>();
        while (await reader.ReadAsync()) works.Add(ReadWork(reader));
        return works.ToArray();
    }

    internal static BotWork ReadWork(DbDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
        r.GetInt64(3), r.GetString(4), r.GetString(5), FromJson<BotInput>(r.GetString(6)), r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : FromJson<ContinuousGoal>(r.GetString(9)));

    public async Task AddLegacyRobot(string room, string user, string robot)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        if (!await IsMember(db, tx, room, user)) throw new InvalidOperationException("Historical room was not created.");
        await AddMember(db, tx, room, robot);
        await tx.CommitAsync();
    }
}
