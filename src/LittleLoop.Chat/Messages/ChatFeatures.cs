using System.Security.Cryptography;
using System.Text.Json;
using MySqlConnector;

sealed record ForwardMessage(Guid RoomId, long Sequence, Guid ClientId);
sealed record EditMessage(string Text);
sealed record RoomPreference(string Key, bool Value);
sealed record MessagePin(bool Value);
sealed record StoredFile(string Name, string Mime, byte[] Content);

sealed partial class ChatStore
{
    private static async Task Upgrade(MySqlConnection db)
    {
        // Serialize incomplete or concurrent upgrades. Each DDL step commits separately on MySQL.
        await using var lease = Command(db, null, "SELECT GET_LOCK(SHA2(CONCAT('littleloop-chat-schema:',DATABASE()),256),30)");
        if (Convert.ToInt32(await lease.ExecuteScalarAsync()) != 1) throw new IOException("Could not acquire chat schema lock.");
        try
        {
            await using var create = Command(db, null, """
                CREATE TABLE IF NOT EXISTS chat_attachments (
                    id CHAR(36) PRIMARY KEY, owner_id CHAR(36) NOT NULL, name VARCHAR(180) NOT NULL,
                    mime VARCHAR(32) NOT NULL, size INT NOT NULL, content MEDIUMBLOB NOT NULL,
                    FOREIGN KEY(owner_id) REFERENCES chat_accounts(id)
                ) CHARACTER SET utf8mb4;
                CREATE TABLE IF NOT EXISTS chat_message_versions (
                    id BIGINT AUTO_INCREMENT PRIMARY KEY, room_id CHAR(36) NOT NULL, seq BIGINT NOT NULL,
                    body TEXT NOT NULL, changed_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                    INDEX(room_id,seq)
                ) CHARACTER SET utf8mb4;
                """);
            await create.ExecuteNonQueryAsync();

            await AddColumn("chat_rooms", "revision", "BIGINT NOT NULL DEFAULT 0");
            await AddColumn("chat_rooms", "pinned_message", "BIGINT NULL");
            await AddColumn("chat_rooms", "last_activity", "DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
            await AddColumn("chat_members", "pinned", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_members", "archived", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_members", "muted", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_messages", "attachment_id", "CHAR(36) NULL REFERENCES chat_attachments(id)");
            await AddColumn("chat_messages", "edited", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_messages", "deleted", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_messages", "forwarded_from", "VARCHAR(100) NULL");
            // Mirrored channel posts store a channel title, which may exceed a username's 32 characters.
            await using (var length = Command(db, null, """
                SELECT character_maximum_length FROM information_schema.columns
                WHERE table_schema=DATABASE() AND table_name='chat_messages' AND column_name='forwarded_from'
                """))
            {
                if (Convert.ToInt64(await length.ExecuteScalarAsync()) < 100)
                {
                    await using var widen = Command(db, null, "ALTER TABLE chat_messages MODIFY COLUMN forwarded_from VARCHAR(100) NULL");
                    await widen.ExecuteNonQueryAsync();
                }
            }
            await AddColumn("chat_messages", "request_key", "CHAR(64) NULL");
            await AddColumn("chat_messages", "card", "JSON NULL");
            await UpgradeBot(db);
            await AddColumn("chat_bot_work", "output", "TEXT NULL");
            await AddColumn("chat_rooms", "parent_id", "CHAR(36) NULL REFERENCES chat_rooms(id)");
            await AddColumn("chat_rooms", "owner_id", "CHAR(36) NULL REFERENCES chat_accounts(id)");
            await AddColumn("chat_messages", "sent_at", "DATETIME(6) NULL");
            await UpgradeSettings(db);
            await UpgradeIdentity(db);
            await UpgradeSocial(db);
            await UpgradePush(db);
            await AddColumn("chat_fcm_devices", "installation_id", "BOOLEAN NOT NULL DEFAULT FALSE");
        }
        finally
        {
            await using var release = Command(db, null, "SELECT RELEASE_LOCK(SHA2(CONCAT('littleloop-chat-schema:',DATABASE()),256))");
            await release.ExecuteScalarAsync();
        }

        async Task AddColumn(string table, string column, string definition)
        {
            await using var check = Command(db, null, """
                SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE()
                    AND table_name=@table AND column_name=@column
                """, ("table", table), ("column", column));
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) != 0) return;
            await using var alter = Command(db, null, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
            await alter.ExecuteNonQueryAsync();
        }
    }

    public async Task<Attachment?> Upload(string user, StoredFile file, Guid? uploadId)
    {
        var result = new Attachment((uploadId ?? Guid.NewGuid()).ToString(), file.Name, file.Mime, file.Content.Length);
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var cmd = Command(db, tx, """
            INSERT INTO chat_attachments(id,owner_id,name,mime,size,content) VALUES(@id,@user,@name,@mime,@size,@content)
            ON DUPLICATE KEY UPDATE id=id
            """, ("id", result.Id), ("user", user), ("name", file.Name), ("mime", file.Mime), ("size", file.Content.Length), ("content", file.Content));
        await cmd.ExecuteNonQueryAsync();
        await using var lookup = Command(db, tx, """
            SELECT name,mime,content FROM chat_attachments WHERE id=@id AND owner_id=@user FOR UPDATE
            """, ("id", result.Id), ("user", user));
        bool matches;
        await using (var saved = await lookup.ExecuteReaderAsync())
            matches = await saved.ReadAsync() && saved.GetString(0) == file.Name
                && saved.GetString(1) == file.Mime && saved.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(file.Content);
        await tx.CommitAsync();
        return matches ? result : null;
    }

    public async Task<StoredFile?> Download(string user, string room, long seq)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT f.name,f.mime,f.content FROM chat_messages msg
            JOIN chat_members m ON m.room_id=msg.room_id AND m.user_id=@user
            JOIN chat_attachments f ON f.id=msg.attachment_id
            WHERE msg.room_id=@room AND msg.seq=@seq AND NOT msg.deleted
            """, ("user", user), ("room", room), ("seq", seq));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2)) : null;
    }

    private static async Task<Attachment?> OwnedAttachment(MySqlConnection db, MySqlTransaction tx, string user, string id)
    {
        await using var cmd = Command(db, tx, "SELECT id,name,mime,size FROM chat_attachments WHERE id=@id AND owner_id=@user",
            ("id", id), ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)) : null;
    }

    private static async Task<ChatMessage?> ForwardSource(MySqlConnection db, MySqlTransaction tx, string user, ForwardMessage forward)
    {
        string room = forward.RoomId.ToString();
        await using var cmd = Command(db, tx, MessageSelect + """
             JOIN chat_members m ON m.room_id=msg.room_id AND m.user_id=@user
             WHERE msg.room_id=@room AND msg.seq=@seq AND NOT msg.deleted FOR SHARE
            """, ("room", room), ("user", user), ("seq", forward.Sequence));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Message(reader, room) : null;
    }

    private static string RequestKey(PostMessage message, ForwardMessage? forward, MessageCard? card = null, BotInput? input = null) =>
        Convert.ToHexString(SHA256.HashData(card is null && input is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { message, forward })
            : JsonSerializer.SerializeToUtf8Bytes(new { message, forward, card, input })));

    public async Task<ChatMessage[]> Search(string user, string text, string? room, int offset)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, MessageSelect + """
             JOIN chat_members m ON m.room_id=msg.room_id AND m.user_id=@user
             JOIN chat_rooms r ON r.id=msg.room_id
             WHERE NOT msg.deleted AND (@room IS NULL OR msg.room_id=@room)
               AND (LOCATE(@text,msg.body)>0 OR LOCATE(@text,f.name)>0)
             ORDER BY r.last_activity DESC,msg.room_id,msg.seq DESC LIMIT 50 OFFSET @offset
            """, ("user", user), ("text", text), ("room", room), ("offset", offset));
        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<ChatMessage>();
        while (await reader.ReadAsync()) items.Add(Message(reader, reader.GetString(13)));
        return items.ToArray();
    }

    public async Task<MessageChange?> ChangeMessage(string user, string room, long seq, string? text)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var clock = Command(db, tx, "SELECT revision FROM chat_rooms WHERE id=@room FOR UPDATE", ("room", room));
        if (await clock.ExecuteScalarAsync() is null) return null;
        if (!await IsMember(db, tx, room, user)) return null;
        await using var find = Command(db, tx, """
            SELECT msg.body,msg.attachment_id,msg.deleted,msg.client_id,msg.reply_to,msg.request_key
            FROM chat_messages msg JOIN chat_rooms r ON r.id=msg.room_id
            WHERE msg.room_id=@room AND msg.seq=@seq AND msg.card IS NULL
            AND NOT EXISTS (
                SELECT 1 FROM chat_channel_threads t WHERE t.group_id=msg.room_id AND t.root_seq=msg.seq)
            AND (
                (r.type<>'channel' AND msg.sender_id=@user) OR
                (@deleting AND r.type IN ('group','topic') AND EXISTS (
                    SELECT 1 FROM chat_rooms parent
                    LEFT JOIN chat_group_moderation moderation ON moderation.group_id=parent.id AND moderation.user_id=@user
                    WHERE parent.id=IF(r.type='topic',r.parent_id,r.id)
                      AND (parent.owner_id=@user OR moderation.administrator))) OR
                (r.type='channel' AND (r.owner_id=@user OR EXISTS (
                    SELECT 1 FROM chat_channel_admins g WHERE g.room_id=r.id AND g.user_id=@user
                    AND (g.rights & @right)=@right))))
            FOR UPDATE
            """, ("room", room), ("seq", seq), ("user", user),
            ("deleting", text is null), ("right", (int)(text is null ? ChannelRights.Delete : ChannelRights.Edit)));
        string previous, requestKey;
        bool hasAttachment, deleted;
        await using (var reader = await find.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return null;
            previous = reader.GetString(0);
            hasAttachment = !reader.IsDBNull(1);
            deleted = reader.GetBoolean(2);
            requestKey = reader.IsDBNull(5)
                ? RequestKey(new(reader.GetString(3), previous, reader.IsDBNull(4) ? null : reader.GetInt64(4)), null)
                : reader.GetString(5);
        }
        if (deleted) return text is null ? new([], null) : null;
        if (text == previous) return new([], null);
        if (text is not null && string.IsNullOrWhiteSpace(text) && !hasAttachment)
            throw new ArgumentException("文字消息不能为空。");
        await using (var update = Command(db, tx, """
            INSERT INTO chat_message_versions(room_id,seq,body) VALUES(@room,@seq,@previous);
            UPDATE chat_messages SET body=@body,edited=@edited,deleted=@deleted,request_key=@request WHERE room_id=@room AND seq=@seq;
            UPDATE chat_rooms SET revision=revision+1,
                pinned_message=IF(@deleted AND pinned_message=@seq,NULL,pinned_message) WHERE id=@room;
            """, ("room", room), ("seq", seq), ("previous", previous), ("body", text ?? ""),
            ("edited", text is not null), ("deleted", text is null), ("request", requestKey)))
            await update.ExecuteNonQueryAsync();

        var recipients = await MessageRecipients(db, tx, room);
        await RecordChannelPostChange(db, tx, room, user, text is null ? "post_deleted" : "post_edited", seq);
        var discussion = await UpdateDiscussionPost(db, tx, room, seq, text);
        await tx.CommitAsync();
        return new(recipients, discussion);
    }

    public async Task<string[]?> SetPinnedMessage(string user, string room, long seq, bool pinned)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var access = Command(db, tx, """
            SELECT type,owner_id,pinned_message FROM chat_rooms WHERE id=@room FOR UPDATE
            """, ("room", room));
        long? current;
        bool restricted;
        await using (var reader = await access.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return null;
            restricted = reader.GetString(0) == "channel" && (reader.IsDBNull(1) || reader.GetString(1) != user);
            current = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        }
        if (!await IsMember(db, tx, room, user)) return null;
        if (restricted && !await HasChannelRight(db, tx, room, user, ChannelRights.Pin))
            throw new ChannelWriteDeniedException();
        if (pinned)
        {
            await using var target = Command(db, tx, """
                SELECT 1 FROM chat_messages WHERE room_id=@room AND seq=@seq AND NOT deleted FOR UPDATE
                """, ("room", room), ("seq", seq));
            if (await target.ExecuteScalarAsync() is null) return null;
            if (current == seq) return [];
        }
        else if (current != seq) return [];
        await using var update = Command(db, tx, "UPDATE chat_rooms SET pinned_message=@pin WHERE id=@room",
            ("pin", pinned ? seq : (long?)null), ("room", room));
        await update.ExecuteNonQueryAsync();
        await RecordChannelPostChange(db, tx, room, user, pinned ? "post_pinned" : "post_unpinned", seq);
        var recipients = await MessageRecipients(db, tx, room);
        await tx.CommitAsync();
        return recipients;
    }

    private static async Task<string[]> MessageRecipients(MySqlConnection db, MySqlTransaction tx, string room)
    {
        await using var members = Command(db, tx, "SELECT user_id FROM chat_members WHERE room_id=@room", ("room", room));
        await using var reader = await members.ExecuteReaderAsync();
        var recipients = new List<string>();
        while (await reader.ReadAsync()) recipients.Add(reader.GetString(0));
        return recipients.ToArray();
    }

    public async Task<bool> SetPreference(string user, string room, RoomPreference input)
    {
        string column = input.Key switch
        {
            "pinned" => "pinned", "archived" => "archived", "muted" => "muted",
            _ => throw new ArgumentException("未知会话设置。"),
        };
        await using var db = await Open();
        await using var cmd = Command(db, null, $"UPDATE chat_members SET {column}=@value WHERE room_id=@room AND user_id=@user",
            ("room", room), ("user", user), ("value", input.Value));
        return await cmd.ExecuteNonQueryAsync() != 0;
    }
}
