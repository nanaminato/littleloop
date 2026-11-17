using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using MySqlConnector;

sealed record PushKeys(string P256dh, string Auth);
sealed record DevicePush(string Endpoint, PushKeys Keys, string Language);
sealed record PushDelivery(long Id, string Device, string Endpoint, string P256dh, string Auth,
    string Room, long Sequence, string Title, string Preview, string Language, string? CommentChannel, long? CommentPost);
sealed record PushIdentity(string PublicKey, string PrivateKey);

sealed partial class ChatStore
{
    private static async Task UpgradePush(DbConnection db)
    {
        await using var cmd = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_push_devices (
                device_id CHAR(36) PRIMARY KEY, endpoint VARCHAR(1800) NOT NULL,
                endpoint_hash CHAR(64) NOT NULL UNIQUE,
                p256dh VARCHAR(100) NOT NULL, auth VARCHAR(30) NOT NULL, language CHAR(2) NOT NULL,
                FOREIGN KEY(device_id) REFERENCES chat_devices(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS chat_push_queue (
                id BIGINT AUTO_INCREMENT PRIMARY KEY, device_id CHAR(36) NOT NULL,
                room_id CHAR(36) NOT NULL, seq BIGINT NOT NULL,
                due DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, expires DATETIME NOT NULL,
                INDEX(due), INDEX(expires), UNIQUE(device_id,room_id,seq),
                FOREIGN KEY(device_id) REFERENCES chat_push_devices(device_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS chat_fcm_devices (
                device_id CHAR(36) PRIMARY KEY, token VARCHAR(4096) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                token_hash CHAR(64) NOT NULL UNIQUE,
                FOREIGN KEY(device_id) REFERENCES chat_devices(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS chat_fcm_queue (
                id BIGINT AUTO_INCREMENT PRIMARY KEY, device_id CHAR(36) NOT NULL,
                room_id CHAR(36) NOT NULL, seq BIGINT NOT NULL, event_key VARCHAR(42) NOT NULL,
                work_id CHAR(36) NULL, nonce CHAR(32) NULL, sent BOOLEAN NOT NULL DEFAULT FALSE,
                due DATETIME NOT NULL, expires DATETIME NOT NULL,
                INDEX(sent,due), INDEX(expires), UNIQUE(device_id,room_id,event_key),
                FOREIGN KEY(device_id) REFERENCES chat_fcm_devices(device_id) ON DELETE CASCADE
            );
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> PushEnabled(string device)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "SELECT 1 FROM chat_push_devices WHERE device_id=@id", ("id", device));
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task<bool> SavePush(string device, DevicePush push)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var hash = Command(db, tx, "SELECT device_id,p256dh,auth FROM chat_push_devices WHERE endpoint_hash=SHA2(@endpoint,256) FOR UPDATE",
            ("endpoint", push.Endpoint));
        string? previous = null;
        await using (var reader = await hash.ExecuteReaderAsync())
            if (await reader.ReadAsync())
            {
                previous = reader.GetString(0);
                if (previous != device && (reader.GetString(1) != push.Keys.P256dh || reader.GetString(2) != push.Keys.Auth)) return false;
            }
        if (previous is not null && previous != device)
        {
            // Re-registering a browser profile after login replaces its old device association.
            await using var release = Command(db, tx, "DELETE FROM chat_push_devices WHERE device_id=@id", ("id", previous));
            await release.ExecuteNonQueryAsync();
        }
        await using var cmd = Command(db, tx, """
            INSERT INTO chat_push_devices(device_id,endpoint,endpoint_hash,p256dh,auth,language)
            VALUES(@device,@endpoint,SHA2(@endpoint,256),@p256dh,@auth,@language)
            ON DUPLICATE KEY UPDATE endpoint=@endpoint,endpoint_hash=SHA2(@endpoint,256),p256dh=@p256dh,auth=@auth,language=@language
            """, ("device", device), ("endpoint", push.Endpoint), ("p256dh", push.Keys.P256dh), ("auth", push.Keys.Auth), ("language", push.Language));
        try { await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync(); return true; }
        catch (DbException e) when (IsConstraintOrBusy(e)) { return false; }
    }

    public async Task DropPush(string device, string? expectedEndpoint = null)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "DELETE FROM chat_push_devices WHERE device_id=@id AND (@endpoint IS NULL OR endpoint=@endpoint)",
            ("id", device), ("endpoint", expectedEndpoint));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnqueuePush(DbConnection db, DbTransaction tx, string room, long seq, string author)
    {
        await using var cmd = Command(db, tx, """
            INSERT INTO chat_push_queue(device_id,room_id,seq,due,expires)
            SELECT p.device_id,@room,@seq,UTC_TIMESTAMP(),UTC_TIMESTAMP()+INTERVAL 15 MINUTE
            FROM chat_members m JOIN chat_devices d ON d.user_id=m.user_id
            JOIN chat_push_devices p ON p.device_id=d.id
            WHERE m.room_id=@room AND m.user_id<>@author AND NOT m.muted AND d.expires>UTC_TIMESTAMP()
            """, ("room", room), ("seq", seq), ("author", author));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long?> ClaimPush()
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using (var purge = Command(db, tx, "DELETE FROM chat_push_queue WHERE expires<=UTC_TIMESTAMP() LIMIT 200"))
            await purge.ExecuteNonQueryAsync();
        await using var pick = Command(db, tx, """
            SELECT id FROM chat_push_queue WHERE due<=UTC_TIMESTAMP() ORDER BY due,id LIMIT 1 FOR UPDATE SKIP LOCKED
            """);
        if (await pick.ExecuteScalarAsync() is not long id) return null;
        await using var lease = Command(db, tx, "UPDATE chat_push_queue SET due=UTC_TIMESTAMP()+INTERVAL 30 SECOND WHERE id=@id", ("id", id));
        await lease.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return id;
    }

    public async Task<PushDelivery?> ReadPush(long id)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT q.id,p.device_id,p.endpoint,p.p256dh,p.auth,r.id,q.seq,
              IF(r.type='direct',a.name,r.title),
              IF(JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.preview'))='false','',COALESCE(NULLIF(LEFT(msg.body,140),''),f.name,'')),p.language, IF(channel_post.deleted, NULL, subscription.room_id),
              IF(subscription.room_id IS NULL OR channel_post.deleted, NULL, thread.post_seq)
            FROM chat_push_queue q JOIN chat_push_devices p ON p.device_id=q.device_id
            JOIN chat_devices d ON d.id=p.device_id
            JOIN chat_rooms r ON r.id=q.room_id
            JOIN chat_members m ON m.room_id=r.id AND m.user_id=d.user_id
            JOIN chat_messages msg ON msg.room_id=r.id AND msg.seq=q.seq
            JOIN chat_accounts a ON a.id=msg.sender_id
            LEFT JOIN chat_channel_comments cc ON cc.group_id=q.room_id AND cc.seq=q.seq
            LEFT JOIN chat_channel_threads thread ON thread.group_id=cc.group_id AND thread.root_seq=cc.root_seq
            LEFT JOIN chat_messages channel_post ON channel_post.room_id=thread.channel_id AND channel_post.seq=thread.post_seq
            LEFT JOIN chat_members subscription ON subscription.room_id=thread.channel_id AND subscription.user_id=d.user_id
            LEFT JOIN chat_channel_comment_reads comment_read ON comment_read.channel_id=thread.channel_id
              AND comment_read.post_seq=thread.post_seq AND comment_read.user_id=d.user_id
            LEFT JOIN chat_accounts receiver ON receiver.id=d.user_id
            LEFT JOIN chat_messages reply_target ON reply_target.room_id=q.room_id AND reply_target.seq=msg.reply_to
            LEFT JOIN chat_attachments f ON f.id=msg.attachment_id
            LEFT JOIN chat_user_settings s ON s.user_id=d.user_id AND s.section='notifications'
            WHERE q.id=@id AND q.expires>UTC_TIMESTAMP() AND d.expires>UTC_TIMESTAMP() AND NOT m.muted
              AND m.read_to<q.seq AND NOT msg.deleted
              AND (cc.seq IS NULL OR COALESCE(comment_read.read_to,0)<q.seq)
              AND (COALESCE(JSON_UNQUOTE(JSON_EXTRACT(s.value,
                CASE r.type WHEN 'channel' THEN '$.channels' WHEN 'group' THEN '$.groups'
                WHEN 'topic' THEN '$.groups' ELSE '$.direct' END)),'true')<>'false'
                OR (cc.seq IS NOT NULL AND (
                     reply_target.sender_id=d.user_id
                     OR REGEXP_LIKE(msg.body,CONCAT('(^|[^a-z0-9_@])@',receiver.name,'($|[^a-z0-9_])'),'i')
                     OR (subscription.room_id IS NOT NULL AND NOT subscription.muted AND EXISTS (
                         SELECT 1 FROM chat_channel_comment_follows followed
                         WHERE followed.channel_id=thread.channel_id AND followed.post_seq=thread.post_seq
                           AND followed.user_id=d.user_id)))))
              AND (r.type<>'direct' OR NOT EXISTS(
                SELECT 1 FROM chat_blocks b
                WHERE (b.blocker_id=d.user_id AND b.target_id=msg.sender_id)
                   OR (b.blocker_id=msg.sender_id AND b.target_id=d.user_id)
              ))
            """, ("id", id));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt64(11)) : null;
    }

    public async Task FinishPush(long id)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "DELETE FROM chat_push_queue WHERE id=@id", ("id", id));
        await cmd.ExecuteNonQueryAsync();
    }
}

sealed class ChatPush(ChatStore store, PushIdentity identity, string subject, ILogger<ChatPush> log) : BackgroundService
{
    public static PushIdentity LoadIdentity(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            using var crypto = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keys = crypto.ExportParameters(true);
            var identity = new PushIdentity(WebEncoders.Base64UrlEncode([4, .. keys.Q.X!, .. keys.Q.Y!]), WebEncoders.Base64UrlEncode(keys.D!));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, ChatStore.ToJson(identity));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, path, false);
            }
            catch (IOException) when (File.Exists(path)) { }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return ChatStore.FromJson<PushIdentity>(File.ReadAllText(path));
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var transport = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new PushServiceClient(transport) { AutoRetryAfter = false };
        using var vapid = new VapidAuthentication(identity.PublicKey, identity.PrivateKey) { Subject = subject };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token))
        {
            try
            {
                for (int attempt = 0; attempt < 20 && !token.IsCancellationRequested; attempt++)
                {
                    if (await store.ClaimPush() is not { } id) break;
                    var push = await store.ReadPush(id);
                    if (push is null || !AllowedEndpoint(push.Endpoint)) { await store.FinishPush(id); continue; }
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(8));
                        var subscription = new PushSubscription { Endpoint = push.Endpoint };
                        subscription.SetKey(PushEncryptionKeyName.P256DH, push.P256dh);
                        subscription.SetKey(PushEncryptionKeyName.Auth, push.Auth);
                        var notification = new PushMessage(ChatStore.ToJson(new { room = push.Room, sequence = push.Sequence, title = push.Title,
                            commentChannel = push.CommentChannel, commentPost = push.CommentPost,
                            body = push.Preview.Length > 0 ? push.Preview : push.Language == "en" ? "New message" : "新消息" })) { TimeToLive = 900 };
                        await client.RequestPushMessageDeliveryAsync(subscription, notification, vapid, VapidAuthenticationScheme.Vapid, timeout.Token);
                        await store.FinishPush(id);
                        log.LogInformation("Push {QueueId} accepted by browser gateway.", id);
                    }
                    catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                    {
                        await store.DropPush(push.Device, push.Endpoint);
                        await store.FinishPush(id);
                    }
                    catch (PushServiceClientException e)
                    {
                        log.LogWarning("Push {QueueId} returned HTTP {Status}.", id, (int)e.StatusCode);
                        if ((int)e.StatusCode is >= 400 and < 500 and not 429) await store.FinishPush(id);
                    }
                    catch (Exception e) when ((e is HttpRequestException or OperationCanceledException) && !token.IsCancellationRequested)
                    { log.LogWarning("Push {QueueId} transport unavailable; will retry.", id); }
                    catch (Exception e) when (e is ArgumentException or FormatException or CryptographicException)
                    {
                        log.LogWarning("Push {QueueId} contained unusable subscription data.", id);
                        await store.DropPush(push.Device, push.Endpoint);
                    }
                }
            }
            catch (DbException e) when (!token.IsCancellationRequested)
            { log.LogWarning("Push database temporarily unavailable, database error {Error}.", e.ErrorCode); }
        }
    }

    public static bool AllowedEndpoint(string? endpoint)
    {
        if (endpoint is not { Length: > 0 and <= 1800 } || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        return uri.IdnHost is "fcm.googleapis.com" or "updates.push.services.mozilla.com" or "web.push.apple.com" ||
            uri.IdnHost.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ValidKey(string? value, int bytes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100 || value.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-')) return false;
        try
        {
            byte[] decoded = WebEncoders.Base64UrlDecode(value);
            if (decoded.Length != bytes) return false;
            if (bytes == 65)
            {
                if (decoded[0] != 4) return false;
                using var point = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = decoded[1..33], Y = decoded[33..] } });
            }
            return true;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    public static void Map(RouteGroupBuilder api, ChatStore store, PushIdentity? identity)
    {
        api.MapGet("/settings/push", async (ClaimsPrincipal user) => Results.Ok(new {
            publicKey = identity?.PublicKey,
            subscribed = identity is not null && await store.PushEnabled(ChatSettingsEndpoints.Device(user)),
        }));
        api.MapPost("/settings/push", async (DevicePush push, ClaimsPrincipal user) =>
        {
            if (identity is null) return Results.NotFound();
            if (!AllowedEndpoint(push.Endpoint) || push.Keys is null ||
                !ValidKey(push.Keys.P256dh, 65) || !ValidKey(push.Keys.Auth, 16) || push.Language is not ("en" or "zh"))
                return Results.BadRequest(new { code = "invalid_push_subscription" });
            return await store.SavePush(ChatSettingsEndpoints.Device(user), push) ? Results.NoContent() : Results.Conflict();
        });
        api.MapPost("/settings/push/remove", async (ClaimsPrincipal user) =>
        {
            await store.DropPush(ChatSettingsEndpoints.Device(user));
            return Results.NoContent();
        });
    }
}
