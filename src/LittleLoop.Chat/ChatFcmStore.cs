using MySqlConnector;

sealed record FcmDelivery(long Id, string Device, string Token, string User, string Room, long Sequence,
    string Title, string Body, bool Sound, Guid? Work, string? Nonce);

sealed partial class ChatStore
{
    public async Task<bool> SaveFcm(string device, string token)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        // Rebind a Firebase installation after sign-in. Queued records on the former session cascade away.
        await using var release = Command(db, tx, """
            DELETE FROM chat_fcm_devices WHERE token_hash=SHA2(@token,256) AND device_id<>@device
            """, ("device", device), ("token", token));
        await release.ExecuteNonQueryAsync();
        await using var save = Command(db, tx, """
            INSERT INTO chat_fcm_devices(device_id,token,token_hash) VALUES(@device,@token,SHA2(@token,256))
            ON DUPLICATE KEY UPDATE token=@token,token_hash=SHA2(@token,256)
            """, ("device", device), ("token", token));
        try { await save.ExecuteNonQueryAsync(); await tx.CommitAsync(); return true; }
        catch (MySqlException e) when (e.Number is 1062 or 1213 or 1452) { return false; }
    }

    public async Task RemoveFcm(string device, string? expectedToken = null)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null,
            "DELETE FROM chat_fcm_devices WHERE device_id=@device AND (@token IS NULL OR token=@token)",
            ("device", device), ("token", expectedToken));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnqueueFcmMessage(MySqlConnection db, MySqlTransaction tx, string room, long seq, string author)
    {
        await using var cmd = Command(db, tx, """
            INSERT IGNORE INTO chat_fcm_queue(device_id,room_id,seq,event_key,due,expires)
            SELECT f.device_id,@room,@seq,CONCAT('m:',@seq),UTC_TIMESTAMP(),UTC_TIMESTAMP()+INTERVAL 15 MINUTE
            FROM chat_members m JOIN chat_devices d ON d.user_id=m.user_id
            JOIN chat_fcm_devices f ON f.device_id=d.id
            WHERE m.room_id=@room AND m.user_id<>@author AND NOT m.muted AND d.expires>UTC_TIMESTAMP()
            """, ("room", room), ("seq", seq), ("author", author));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task EnqueueFcmApproval(BotWork work, string nonce, IEnumerable<string> approvers)
    {
        if (!fcmEnabled) return;
        await using var db = await Open();
        foreach (string approver in approvers)
        {
            await using var cmd = Command(db, null, """
                INSERT IGNORE INTO chat_fcm_queue(device_id,room_id,seq,event_key,work_id,nonce,due,expires)
                SELECT f.device_id,@room,@seq,CONCAT('a:',@nonce),@work,@nonce,UTC_TIMESTAMP(),UTC_TIMESTAMP()+INTERVAL 15 MINUTE
                FROM chat_members m JOIN chat_devices d ON d.user_id=m.user_id
                JOIN chat_fcm_devices f ON f.device_id=d.id
                WHERE m.room_id=@room AND m.user_id=@approver AND NOT m.muted AND d.expires>UTC_TIMESTAMP()
                """, ("room", work.Room), ("seq", work.Sequence), ("nonce", nonce), ("work", work.Id.ToString()), ("approver", approver));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<long?> ClaimFcm()
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using (var purge = Command(db, tx, "DELETE FROM chat_fcm_queue WHERE expires<=UTC_TIMESTAMP() LIMIT 200"))
            await purge.ExecuteNonQueryAsync();
        await using var pick = Command(db, tx, """
            SELECT id FROM chat_fcm_queue WHERE NOT sent AND due<=UTC_TIMESTAMP() AND expires>UTC_TIMESTAMP()
            ORDER BY due,id LIMIT 1 FOR UPDATE SKIP LOCKED
            """);
        if (await pick.ExecuteScalarAsync() is not long id) return null;
        await using var lease = Command(db, tx, "UPDATE chat_fcm_queue SET due=UTC_TIMESTAMP()+INTERVAL 30 SECOND WHERE id=@id", ("id", id));
        await lease.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return id;
    }

    public async Task<FcmDelivery?> ReadFcm(long id, string? device = null)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT q.id,f.device_id,f.token,d.user_id,q.room_id,q.seq,
              IF(r.type='direct',a.name,r.title),
              IF(q.work_id IS NOT NULL,'LittleLoop 有待审批的任务',
                IF(JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.preview'))='false','新消息',COALESCE(NULLIF(LEFT(msg.body,140),''),f2.name,'新消息'))),
              COALESCE(JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.sound')),'true')<>'false',q.work_id,q.nonce
            FROM chat_fcm_queue q JOIN chat_fcm_devices f ON f.device_id=q.device_id
            JOIN chat_devices d ON d.id=f.device_id
            JOIN chat_rooms r ON r.id=q.room_id
            JOIN chat_members m ON m.room_id=r.id AND m.user_id=d.user_id
            JOIN chat_messages msg ON msg.room_id=r.id AND msg.seq=q.seq
            JOIN chat_accounts a ON a.id=msg.sender_id
            LEFT JOIN chat_attachments f2 ON f2.id=msg.attachment_id
            LEFT JOIN chat_user_settings s ON s.user_id=d.user_id AND s.section='notifications'
            WHERE q.id=@id AND (@device IS NULL OR q.device_id=@device) AND q.expires>UTC_TIMESTAMP()
              AND d.expires>UTC_TIMESTAMP() AND NOT m.muted AND NOT msg.deleted
              AND (q.work_id IS NOT NULL OR m.read_to<q.seq)
              AND COALESCE(JSON_UNQUOTE(JSON_EXTRACT(s.value,
                CASE r.type WHEN 'channel' THEN '$.channels' WHEN 'group' THEN '$.groups'
                WHEN 'topic' THEN '$.groups' ELSE '$.direct' END)),'true')<>'false'
              AND (r.type<>'direct' OR NOT EXISTS(
                SELECT 1 FROM chat_blocks b
                WHERE (b.blocker_id=d.user_id AND b.target_id=msg.sender_id)
                   OR (b.blocker_id=msg.sender_id AND b.target_id=d.user_id)))
            """, ("id", id), ("device", device));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
            reader.GetBoolean(8), reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10)) : null;
    }

    public async Task CompleteFcm(long id, bool delivered)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, delivered
            ? "UPDATE chat_fcm_queue SET sent=TRUE WHERE id=@id"
            : "DELETE FROM chat_fcm_queue WHERE id=@id", ("id", id));
        await cmd.ExecuteNonQueryAsync();
    }
}
