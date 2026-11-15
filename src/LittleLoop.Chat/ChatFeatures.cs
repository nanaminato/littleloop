using System.Security.Cryptography;
using System.Text.Json;
using MySqlConnector;

sealed record ForwardMessage(Guid RoomId, long Sequence, Guid ClientId);
sealed record EditMessage(string Text);
sealed record RoomPreference(string Key, bool Value);
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
            await AddColumn("chat_rooms", "last_activity", "DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)");
            await AddColumn("chat_members", "pinned", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_members", "archived", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_members", "muted", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_messages", "attachment_id", "CHAR(36) NULL REFERENCES chat_attachments(id)");
            await AddColumn("chat_messages", "edited", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_messages", "deleted", "BOOLEAN NOT NULL DEFAULT FALSE");
            await AddColumn("chat_messages", "forwarded_from", "VARCHAR(32) NULL");
            await AddColumn("chat_messages", "request_key", "CHAR(64) NULL");
            await AddColumn("chat_messages", "card", "JSON NULL");
            await UpgradeBot(db);
            await AddColumn("chat_bot_work", "output", "TEXT NULL");
            await AddColumn("chat_rooms", "parent_id", "CHAR(36) NULL REFERENCES chat_rooms(id)");
            await AddColumn("chat_rooms", "owner_id", "CHAR(36) NULL REFERENCES chat_accounts(id)");
            await AddColumn("chat_messages", "sent_at", "DATETIME(6) NULL");
            await UpgradeSettings(db);
            await UpgradeIdentity(db);
            await UpgradePush(db);
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

    public async Task<Attachment> Upload(string user, StoredFile file)
    {
        var result = new Attachment(Guid.NewGuid().ToString(), file.Name, file.Mime, file.Content.Length);
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            INSERT INTO chat_attachments(id,owner_id,name,mime,size,content) VALUES(@id,@user,@name,@mime,@size,@content)
            """, ("id", result.Id), ("user", user), ("name", file.Name), ("mime", file.Mime), ("size", file.Content.Length), ("content", file.Content));
        await cmd.ExecuteNonQueryAsync();
        return result;
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
             WHERE msg.room_id=@room AND msg.seq=@seq AND NOT msg.deleted
            """, ("room", room), ("user", user), ("seq", forward.Sequence));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Message(reader, room) : null;
    }

    private static string RequestKey(PostMessage message, ForwardMessage? forward, MessageCard? card = null, BotInput? input = null) =>
        Convert.ToHexString(SHA256.HashData(card is null && input is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { message, forward })
            : JsonSerializer.SerializeToUtf8Bytes(new { message, forward, card, input })));

    public async Task<ChatMessage[]> Search(string user, string text, string? room)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, MessageSelect + """
             JOIN chat_members m ON m.room_id=msg.room_id AND m.user_id=@user
             JOIN chat_rooms r ON r.id=msg.room_id
             WHERE NOT msg.deleted AND (@room IS NULL OR msg.room_id=@room)
               AND (LOCATE(@text,msg.body)>0 OR LOCATE(@text,f.name)>0)
             ORDER BY r.last_activity DESC,msg.room_id,msg.seq DESC LIMIT 50
            """, ("user", user), ("text", text), ("room", room));
        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<ChatMessage>();
        while (await reader.ReadAsync()) items.Add(Message(reader, reader.GetString(13)));
        return items.ToArray();
    }

    public async Task<string[]?> ChangeMessage(string user, string room, long seq, string? text)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var clock = Command(db, tx, "SELECT revision FROM chat_rooms WHERE id=@room FOR UPDATE", ("room", room));
        if (await clock.ExecuteScalarAsync() is null) return null;
        await using var find = Command(db, tx, """
            SELECT body,attachment_id,deleted,client_id,reply_to,request_key FROM chat_messages
            WHERE room_id=@room AND seq=@seq AND sender_id=@user AND card IS NULL
            """, ("room", room), ("seq", seq), ("user", user));
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
        if (deleted) return text is null ? [] : null;
        if (text == previous) return [];
        if (text is not null && string.IsNullOrWhiteSpace(text) && !hasAttachment)
            throw new ArgumentException("文字消息不能为空。");
        await using (var update = Command(db, tx, """
            INSERT INTO chat_message_versions(room_id,seq,body) VALUES(@room,@seq,@previous);
            UPDATE chat_messages SET body=@body,edited=@edited,deleted=@deleted,request_key=@request WHERE room_id=@room AND seq=@seq;
            UPDATE chat_rooms SET revision=revision+1 WHERE id=@room;
            """, ("room", room), ("seq", seq), ("previous", previous), ("body", text ?? ""),
            ("edited", text is not null), ("deleted", text is null), ("request", requestKey)))
            await update.ExecuteNonQueryAsync();

        await using var members = Command(db, tx, "SELECT user_id FROM chat_members WHERE room_id=@room", ("room", room));
        var recipients = new List<string>();
        await using (var reader = await members.ExecuteReaderAsync())
            while (await reader.ReadAsync()) recipients.Add(reader.GetString(0));
        await tx.CommitAsync();
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
