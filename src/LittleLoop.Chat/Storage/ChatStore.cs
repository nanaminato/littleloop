using System.Data.Common;
using MySqlConnector;

sealed record Account(string Id, string Name, string Password);
sealed record Room(string Id, string Type, string Title, long Latest, long Read, long Unread, long Revision,
    bool Pinned, bool Archived, bool Muted, string? ParentId, bool CanPost, string Preview, DateTime? LastSent,
    string? AvatarName, string? AvatarVersion, bool PreviewDeleted, long? PinnedMessage, bool CanManage, int ChannelRights = 0, bool SignPosts = false, bool CanModerate = false);
sealed record Attachment(string Id, string Name, string Mime, long Size);
sealed record ChatMessage(string RoomId, long Sequence, string ClientId, string Sender, string Text, long? ReplyTo,
    Attachment? Attachment, bool Edited, bool Deleted, string? ForwardedFrom, MessageCard? Card, DateTime? SentAt, bool DiscussionRoot = false);
sealed record Login(string Name, string Password, string? Invite);
sealed record OpenRoom(string Type, string? Title, string[] Members);
sealed record PostMessage(string ClientId, string Text, long? ReplyTo, string? AttachmentId = null);
sealed record ReadTo(long Sequence);
sealed record MessageChange(string[] Recipients, DiscussionRoomChange? Discussion);
sealed record DiscussionRoomChange(string Room, string[] Recipients);
sealed record SendResult(ChatMessage Message, string[] Recipients, bool IsNew, DiscussionRoomChange? Discussion = null);

sealed partial class ChatStore(string connection, bool fcmEnabled, string provider = "MySql", TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly bool sqlite = ParseProvider(provider);
    private readonly string connectionString = ParseProvider(provider)
        ? SqliteSettings(connection)
        : new MySqlConnectionStringBuilder(connection) { GuidFormat = MySqlGuidFormat.None, UseAffectedRows = false }.ConnectionString;

    public async Task Initialize()
    {
        await using var db = await Open();
        await using var command = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_accounts (
                id CHAR(36) PRIMARY KEY, name VARCHAR(32) NOT NULL UNIQUE, password VARCHAR(512) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS chat_rooms (
                id CHAR(36) PRIMARY KEY, type VARCHAR(10) NOT NULL, title VARCHAR(100) NOT NULL,
                direct_key VARCHAR(73) UNIQUE, latest BIGINT NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS chat_members (
                room_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL, read_to BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY(room_id,user_id), INDEX(user_id,room_id),
                FOREIGN KEY(room_id) REFERENCES chat_rooms(id), FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            );
            CREATE TABLE IF NOT EXISTS chat_messages (
                room_id CHAR(36) NOT NULL, seq BIGINT NOT NULL, sender_id CHAR(36) NOT NULL,
                client_id CHAR(36) NOT NULL, body TEXT NOT NULL, reply_to BIGINT NULL,
                PRIMARY KEY(room_id,seq), UNIQUE(room_id,sender_id,client_id),
                CONSTRAINT fk_chat_message_room FOREIGN KEY(room_id) REFERENCES chat_rooms(id),
                CONSTRAINT fk_chat_message_sender FOREIGN KEY(sender_id) REFERENCES chat_accounts(id),
                FOREIGN KEY(room_id,reply_to) REFERENCES chat_messages(room_id,seq)
            ) CHARACTER SET utf8mb4;
            """);
        await command.ExecuteNonQueryAsync();
        await Upgrade(db);
    }

    public async Task<Account?> Find(string name)
    {
        await using var db = await Open();
        return await Find(db, name);
    }

    private static async Task<Account?> Find(DbConnection db, string name, DbTransaction? tx = null)
    {
        await using var cmd = Command(db, tx, "SELECT id,name,password FROM chat_accounts WHERE name=@name", ("name", name));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public async Task<bool> Register(Account user)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var cmd = Command(db, tx, """
            INSERT INTO chat_accounts(id,name,password) VALUES(@id,@name,@password);
            INSERT INTO chat_usernames(name,user_id) VALUES(@name,@id);
            """,
            ("id", user.Id), ("name", user.Name), ("password", user.Password));
        try { await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync(); return true; }
        catch (DbException e) when (ChatStore.IsDuplicate(e)) { return false; }
    }

    public async Task<(string Id, string[] Members)> CreateRoom(Account user, OpenRoom request)
    {
        await using var db = await Open();
        var others = new List<Account>();
        foreach (var name in request.Members.Select(x => x.Trim().ToLowerInvariant()).Distinct())
        {
            var found = await Find(db, name) ?? throw new ArgumentException("成员不存在：" + name);
            if (found.Id != user.Id && await Blocked(db, null, user.Id, found.Id)) throw new DirectMessageBlockedException();
            if (request.Type == "group" && found.Id != user.Id &&
                !(await GetSetting(found.Id, "privacy", new PrivacySettings())).GroupInvites)
                throw new ArgumentException($"{found.Name} 当前不接受群邀请。");
            if (found.Id != user.Id) others.Add(found);
        }
        if (request.Type == "direct" && others.Count != 1)
            throw new ArgumentException("请选择有效成员。");
        string[] members = [.. others.Select(x => x.Id).Order(), user.Id];
        string? directKey = request.Type switch
        {
            "direct" => string.Join(":", members.Order(StringComparer.Ordinal)),
            "saved" => "saved:" + user.Id,
            "legacy" => "legacy:" + user.Id,
            _ => null,
        };
        await using var tx = await db.BeginTransactionAsync();
        string id = await SaveRoom(db, tx, request.Type, request.Title ?? "", directKey,
            request.Type == "group" ? user.Id : null, members);
        await tx.CommitAsync();
        return (id, members);
    }

    private static async Task<string> SaveRoom(DbConnection db, DbTransaction tx, string type, string title,
        string? directKey, string? owner, string[] members)
    {
        string id = Guid.NewGuid().ToString();
        await using (var insert = Command(db, tx, """
            INSERT INTO chat_rooms(id,type,title,direct_key,owner_id) VALUES(@id,@type,@title,@key,@owner)
            ON DUPLICATE KEY UPDATE id=id
            """, ("id", id), ("type", type), ("title", title), ("key", directKey), ("owner", owner)))
            await insert.ExecuteNonQueryAsync();
        if (directKey is not null)
        {
            await using var select = Command(db, tx, "SELECT id FROM chat_rooms WHERE direct_key=@key FOR UPDATE", ("key", directKey));
            id = (string)(await select.ExecuteScalarAsync())!;
        }
        foreach (var member in members.Order(StringComparer.Ordinal))
        {
            await using var insert = Command(db, tx, """
                INSERT INTO chat_members(room_id,user_id) VALUES(@id,@user)
                ON DUPLICATE KEY UPDATE read_to=read_to
                """, ("id", id), ("user", member));
            await insert.ExecuteNonQueryAsync();
        }
        return id;
    }

    public async Task<Room[]> Rooms(string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT r.id,r.type,IF(r.type='direct',COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(profile.value,'$.displayName')),''),peer.name),r.title),r.latest,m.read_to,
              (SELECT COUNT(*) FROM chat_messages msg WHERE msg.room_id=r.id AND msg.seq>m.read_to AND msg.sender_id<>@user AND NOT msg.deleted),
              r.revision,m.pinned,m.archived,m.muted,r.parent_id,(COALESCE(gm.silenced,FALSE)=FALSE AND (r.type<>'channel' OR r.owner_id=@user OR EXISTS (
                SELECT 1 FROM chat_channel_admins g WHERE g.room_id=r.id AND g.user_id=@user AND (g.rights & 1)=1))),
              CASE WHEN last.deleted THEN '消息已删除' ELSE COALESCE(NULLIF(LEFT(last.body,140),''),f.name,'') END,last.sent_at,
              peer.name,picture.version,COALESCE(last.deleted,FALSE),r.pinned_message,
              COALESCE(r.type='group' AND r.owner_id=@user,FALSE),
              CASE WHEN r.type<>'channel' THEN 0 WHEN r.owner_id=@user THEN 127
                   ELSE COALESCE((SELECT g.rights FROM chat_channel_admins g WHERE g.room_id=r.id AND g.user_id=@user),0) END, COALESCE(cp.sign_posts,FALSE),
              COALESCE(gr.owner_id=@user OR gm.administrator,FALSE)
            FROM chat_members m JOIN chat_rooms r ON r.id=m.room_id
            LEFT JOIN chat_rooms gr ON gr.type='group' AND gr.id=IF(r.type='topic',r.parent_id,r.id)
            LEFT JOIN chat_group_moderation gm ON gm.group_id=gr.id AND gm.user_id=@user
            LEFT JOIN chat_channel_profiles cp ON cp.room_id=r.id
            LEFT JOIN chat_members p ON r.type='direct' AND p.room_id=r.id AND p.user_id<>m.user_id
            LEFT JOIN chat_accounts peer ON peer.id=p.user_id
            LEFT JOIN chat_user_settings profile ON profile.user_id=peer.id AND profile.section='profile'
            LEFT JOIN chat_profile_pictures picture ON picture.user_id=peer.id
            LEFT JOIN chat_messages last ON last.room_id=r.id AND last.seq=r.latest
            LEFT JOIN chat_attachments f ON f.id=last.attachment_id
            WHERE m.user_id=@user ORDER BY m.pinned DESC,r.last_activity DESC,r.id
            """, ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        var rooms = new List<Room>();
        while (await reader.ReadAsync())
            rooms.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetBoolean(11), reader.GetString(12), ReadTimestamp(reader, 13),
                reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetBoolean(16),
                reader.IsDBNull(17) ? null : reader.GetInt64(17), reader.GetBoolean(18), reader.GetInt32(19), reader.GetBoolean(20), reader.GetBoolean(21)));
        return rooms.ToArray();
    }

    public async Task<ChatMessage[]?> History(string user, string room, long? after, long? before)
    {
        await using var db = await Open();
        if (!await IsMember(db, null, room, user)) return null;
        string sql = MessageSelect + """
             WHERE msg.room_id=@room AND EXISTS(
                 SELECT 1 FROM chat_members access WHERE access.room_id=msg.room_id AND access.user_id=@user)
            """;

        sql += after.HasValue ? " AND msg.seq>@cursor ORDER BY msg.seq ASC LIMIT 50"
            : " AND msg.seq<@cursor ORDER BY msg.seq DESC LIMIT 50";
        await using var cmd = Command(db, null, sql, ("room", room), ("user", user), ("cursor", after ?? before ?? long.MaxValue));
        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<ChatMessage>();
        while (await reader.ReadAsync()) items.Add(Message(reader, room));
        return after.HasValue ? items.ToArray() : items.AsEnumerable().Reverse().ToArray();
    }

    public async Task<SendResult?> Send(string user, string name, string room, PostMessage input, ForwardMessage? forward = null,
        MessageCard? card = null, BotWork? job = null, string? goalRobot = null, bool legacyBotOperator = true,
        BotWork? answeredWork = null, string answerStatus = "completed", long? answerRevision = null, bool decisionRequest = false, Func<string, bool>? noticeOperator = null)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var clock = Command(db, tx, "SELECT latest,type,owner_id,revision FROM chat_rooms WHERE id=@room FOR UPDATE", ("room", room));
        long latest;
        string roomType, owner;
        await using (var reader = await clock.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return null;
            if (answerRevision is long revision && reader.GetInt64(3) != revision) return null;
            roomType = reader.GetString(1);
            owner = reader.IsDBNull(2) ? "" : reader.GetString(2);
            latest = reader.GetInt64(0);
        }
        if (!await IsMember(db, tx, room, user)) return null;
        if (noticeOperator is not null && input.ReplyTo is long sourceSeq)
        {
            // Order a delayed outbox delivery with source edits, uninstall and access revocation.
            await using var source = Command(db, tx, "SELECT sender_id FROM chat_messages WHERE room_id=@room AND seq=@seq AND NOT deleted AND NOT edited",
                ("room", room), ("seq", sourceSeq));
            if (await source.ExecuteScalarAsync() is not string recipient ||
                !await Permitted(db, tx, room, recipient, user, noticeOperator(recipient), roomType)) return null;
        }
        if (answeredWork is not null)
        {
            if (answeredWork.Kind != "chat" || answerStatus is not ("completed" or "failed" or "needs-reconciliation"))
                throw new ArgumentException("Invalid reply job or result.");
            // Commit the reply and its job's result together. Room locking also orders
            // delivery against a simultaneous revocation, which cancels outstanding jobs.
            await using var finish = Command(db, tx, """
                UPDATE chat_bot_work SET status=@status WHERE id=@work AND kind=@kind AND status='running'
                AND room_id=@room AND owner_id=@owner AND seq=@seq
                AND EXISTS (SELECT 1 FROM chat_bot_rooms b WHERE b.room_id=@room AND b.enabled)
                AND EXISTS (SELECT 1 FROM chat_members m WHERE m.room_id=@room AND m.user_id=@owner)
                """, ("status", answerStatus), ("work", answeredWork.Id.ToString()), ("kind", answeredWork.Kind),
                ("owner", answeredWork.Owner), ("room", room), ("seq", answeredWork.Sequence));
            if (await finish.ExecuteNonQueryAsync() != 1) return null;
        }
        if (roomType == "channel" && owner != user &&
            !await HasChannelRight(db, tx, room, user, ChannelRights.Publish))
            throw new ChannelWriteDeniedException();
        if (roomType is "group" or "topic" && (await GroupModeration(db, tx, room, user) & 2) != 0)
            throw new GroupSilencedException();
        string requestKey = RequestKey(input, forward, card, job?.Input);
        // A locking read observes a competing sender's committed retry even under repeatable read.
        await using (var lookup = Command(db, tx, MessageSelect + """
             WHERE msg.room_id=@room AND msg.sender_id=@user AND msg.client_id=@client FOR UPDATE
            """, ("room", room), ("user", user), ("client", input.ClientId)))
        {
            await using var reader = await lookup.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var saved = Message(reader, room);
                if ((!reader.IsDBNull(12) && reader.GetString(12) != requestKey) ||
                    (reader.IsDBNull(12) && (saved.Text != input.Text || saved.ReplyTo != input.ReplyTo || forward is not null || input.AttachmentId is not null)))
                    throw new InvalidOperationException("同一消息 ID 已用于不同内容。");
                return new(saved, [], false);
            }
        }
        if (roomType == "direct")
            foreach (string peer in await MemberIds(db, tx, room))
                if (peer != user && await Blocked(db, tx, user, peer)) throw new DirectMessageBlockedException();
        bool routeToBot = legacyBotOperator;
        if (roomType == "direct" && goalRobot is not null && user != goalRobot && await IsMember(db, tx, room, goalRobot))
        {
            var access = await ReadBotFriendAccess(db, tx, user, goalRobot, room);
            routeToBot = access == BotFriendAccessState.Active || access == BotFriendAccessState.Unmanaged && legacyBotOperator;
            if (!routeToBot) throw new BotFriendshipRequiredException();
        }
        if (job?.Status == "queued" || decisionRequest)
        {
            await using var installed = Command(db, tx, "SELECT enabled FROM chat_bot_rooms WHERE room_id=@room FOR SHARE", ("room", room));
            if (await installed.ExecuteScalarAsync() is not { } value || !Convert.ToBoolean(value))
                throw new InvalidOperationException("会话尚未启用 LittleLoop。");
        }
        Attachment? attachment = null;
        string? forwardedFrom = null;
        if (forward is not null)
        {
            var source = await ForwardSource(db, tx, user, forward) ?? throw new ArgumentException("找不到可转发的消息。");
            input = input with { Text = source.Text, AttachmentId = source.Attachment?.Id };
            forwardedFrom = source.ForwardedFrom ?? source.Sender;
            attachment = source.Attachment;
        }
        else if (input.AttachmentId is { } attachmentId)
        {
            attachment = await OwnedAttachment(db, tx, user, attachmentId) ?? throw new ArgumentException("找不到附件。");
        }
        long? commentRoot = null;
        if (input.ReplyTo is { } reply)
        {
            await using var parent = Command(db, tx, "SELECT seq FROM chat_messages WHERE room_id=@room AND seq=@seq FOR SHARE",
                ("room", room), ("seq", reply));
            if (await parent.ExecuteScalarAsync() is null) throw new ArgumentException("回复目标不在该会话中。");
            if (roomType == "group") commentRoot = await FindCommentRoot(db, tx, room, reply);
        }
        long seq = checked(latest + 1);
        await using (var insert = Command(db, tx, """
            INSERT INTO chat_messages(room_id,seq,sender_id,client_id,body,reply_to,attachment_id,forwarded_from,request_key,card,sent_at)
            VALUES(@room,@seq,@user,@client,@body,@reply,@attachment,@forwarded,@request,@card,UTC_TIMESTAMP(6));
            UPDATE chat_rooms SET latest=@seq,last_activity=CURRENT_TIMESTAMP(6) WHERE id=@room;
            """, ("room", room), ("seq", seq), ("user", user), ("client", input.ClientId), ("body", input.Text), ("reply", input.ReplyTo),
            ("attachment", attachment?.Id), ("forwarded", forwardedFrom), ("request", requestKey), ("card", card is null ? null : ToJson(card))))
            await insert.ExecuteNonQueryAsync();
        if (commentRoot is { } root)
        {
            await using var comment = Command(db, tx, """
                INSERT INTO chat_channel_comments(group_id,seq,root_seq) VALUES(@room,@seq,@root)
                """, ("room", room), ("seq", seq), ("root", root));
            await comment.ExecuteNonQueryAsync();
        }
        if (job is not null) await InsertJob(db, tx, job with { Sequence = seq });
        if (goalRobot is not null && routeToBot && forward is null && (card is null || decisionRequest) && !string.IsNullOrWhiteSpace(input.Text))
            await EnqueueGoalMessage(db, tx, room, roomType, user, seq, input, goalRobot, decisionRequest);
        if (roomType != "legacy")
        {
            await EnqueuePush(db, tx, room, seq, user);
            if (fcmEnabled) await EnqueueFcmMessage(db, tx, room, seq, user);
        }
        await using var select = Command(db, tx, "SELECT user_id FROM chat_members WHERE room_id=@room", ("room", room));
        var recipients = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync()) recipients.Add(reader.GetString(0));
        if (roomType == "channel")
            await RecordChannelAction(db, tx, room, user, "post_published", seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var discussion = roomType == "channel" ? await MirrorChannelPost(db, tx, room, seq) : null;
        await tx.CommitAsync();
        return new(new(room, seq, input.ClientId, name, input.Text, input.ReplyTo, attachment, false, false, forwardedFrom, card, DateTime.UtcNow), recipients.ToArray(), true, discussion);
    }

    public async Task<bool> MarkRead(string user, string room, long seq)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            UPDATE chat_members SET read_to=GREATEST(read_to, LEAST((SELECT latest FROM chat_rooms WHERE id=@room),@seq)) WHERE room_id=@room AND user_id=@user
            """, ("room", room), ("user", user), ("seq", seq));
        return await cmd.ExecuteNonQueryAsync() != 0;
    }

    internal static async Task<bool> IsMember(DbConnection db, DbTransaction? tx, string room, string user)
    {
        await using var cmd = Command(db, tx, "SELECT 1 FROM chat_members WHERE room_id=@room AND user_id=@user",
            ("room", room), ("user", user));
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private const string MessageSelect = """
        SELECT msg.seq,msg.client_id,a.name,msg.body,msg.reply_to,f.id,f.name,f.mime,f.size,
               msg.edited,msg.deleted,msg.forwarded_from,msg.request_key,msg.room_id,msg.card,msg.sent_at,
               EXISTS (SELECT 1 FROM chat_channel_threads t WHERE t.group_id=msg.room_id AND t.root_seq=msg.seq)
        FROM chat_messages msg JOIN chat_accounts a ON a.id=msg.sender_id
        LEFT JOIN chat_attachments f ON f.id=msg.attachment_id
        """;

    private static ChatMessage Message(DbDataReader reader, string room)
    {
        bool deleted = reader.GetBoolean(10);
        var attachment = deleted || reader.IsDBNull(5) ? null : new Attachment(reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8));
        return new(room, reader.GetInt64(0), reader.GetString(1), reader.GetString(2), deleted ? "" : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), attachment, reader.GetBoolean(9), deleted,
            deleted || reader.IsDBNull(11) ? null : reader.GetString(11),
            deleted || reader.IsDBNull(14) ? null : FromJson<MessageCard>(reader.GetString(14)), ReadTimestamp(reader, 15), reader.GetBoolean(16));
    }

    private static DateTime? ReadTimestamp(DbDataReader reader, int index) => reader.IsDBNull(index)
        ? null : DateTime.SpecifyKind(reader.GetDateTime(index), DateTimeKind.Utc);

    internal async Task<DbConnection> Open()
    {
        DbConnection db = sqlite ? new Microsoft.Data.Sqlite.SqliteConnection(connectionString) : new MySqlConnection(connectionString);
        try
        {
            await db.OpenAsync();
            if (db is Microsoft.Data.Sqlite.SqliteConnection local) ConfigureSqlite(local);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    internal static DbCommand Command(DbConnection db, DbTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = db is Microsoft.Data.Sqlite.SqliteConnection ? SqliteSql(sql) : sql;
        foreach (var (name, value) in args)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }
}
