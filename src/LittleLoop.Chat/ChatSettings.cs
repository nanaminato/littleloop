using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;

sealed record ProfileSettings(string DisplayName = "", string Bio = "", string? AvatarVersion = null);
sealed record NotificationSettings(bool Direct = true, bool Groups = true, bool Channels = true, bool Preview = true, bool Sound = false);
sealed record PrivacySettings(bool GroupInvites = true);
sealed record ChatFolder(string Name, string Kind, string[]? Include = null, string[]? Exclude = null);
sealed record PasswordChange(string Current, string NewPassword);
sealed record ChatDevice(string Id, string Agent, DateTime Created, bool Current);
sealed record DeviceRevoke(string Id);

sealed partial class ChatStore
{
    private static async Task UpgradeSettings(MySqlConnection db)
    {
        await using var cmd = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_user_settings (
                user_id CHAR(36) NOT NULL, section VARCHAR(20) NOT NULL, value JSON NOT NULL,
                PRIMARY KEY(user_id,section), FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            );
            CREATE TABLE IF NOT EXISTS chat_devices (
                id CHAR(36) PRIMARY KEY, user_id CHAR(36) NOT NULL, agent VARCHAR(240) NOT NULL,
                created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, expires DATETIME NOT NULL,
                INDEX(user_id,expires), FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            ) CHARACTER SET utf8mb4;
            CREATE TABLE IF NOT EXISTS chat_profile_pictures (
                user_id CHAR(36) PRIMARY KEY, version CHAR(36) NOT NULL, mime VARCHAR(32) NOT NULL,
                content MEDIUMBLOB NOT NULL, FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            );
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> AvatarVersion(string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "SELECT version FROM chat_profile_pictures WHERE user_id=@user", ("user", user));
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task SetAvatar(string user, byte[]? bytes, string? mime)
    {
        await using var db = await Open();
        await using var cmd = bytes is null
            ? Command(db, null, "DELETE FROM chat_profile_pictures WHERE user_id=@user", ("user", user))
            : Command(db, null, """
                INSERT INTO chat_profile_pictures(user_id,version,mime,content) VALUES(@user,@version,@mime,@bytes)
                ON DUPLICATE KEY UPDATE version=@version,mime=@mime,content=@bytes
                """, ("user", user), ("version", Guid.NewGuid().ToString()), ("mime", mime), ("bytes", bytes));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<StoredFile?> Avatar(string viewer, string name)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT p.mime,p.content FROM chat_profile_pictures p JOIN chat_accounts a ON a.id=p.user_id
            WHERE a.name=@name AND (a.id=@viewer OR EXISTS(
                SELECT 1 FROM chat_members m JOIN chat_members other ON other.room_id=m.room_id
                JOIN chat_rooms r ON r.id=m.room_id
                WHERE m.user_id=@viewer AND other.user_id=a.id AND (r.type<>'channel' OR r.owner_id=a.id)
            ))
            """, ("name", name), ("viewer", viewer));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new("avatar", reader.GetString(0), reader.GetFieldValue<byte[]>(1)) : null;
    }

    public async Task<T> GetSetting<T>(string user, string section, T fallback)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "SELECT value FROM chat_user_settings WHERE user_id=@user AND section=@section",
            ("user", user), ("section", section));
        return await cmd.ExecuteScalarAsync() is string json ? FromJson<T>(json) : fallback;
    }

    public async Task PutSetting<T>(string user, string section, T value)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            INSERT INTO chat_user_settings(user_id,section,value) VALUES(@user,@section,@value)
            ON DUPLICATE KEY UPDATE value=@value
            """, ("user", user), ("section", section), ("value", ToJson(value)));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> AddDevice(Account account, string agent)
    {
        string id = Guid.NewGuid().ToString();
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            INSERT INTO chat_devices(id,user_id,agent,created,expires)
            SELECT @id,a.id,@agent,UTC_TIMESTAMP(),UTC_TIMESTAMP()+INTERVAL 7 DAY FROM chat_accounts a
            WHERE a.id=@user AND BINARY a.password=@password
            """, ("id", id), ("user", account.Id), ("password", account.Password), ("agent", agent[..Math.Min(240, agent.Length)]));
        return await cmd.ExecuteNonQueryAsync() == 1 ? id : null;
    }

    public async Task<bool> ActiveDevice(string? user, string? device)
    {
        if (user is null || device is null) return false;
        await using var db = await Open();
        await using var cmd = Command(db, null, "SELECT 1 FROM chat_devices WHERE id=@id AND user_id=@user AND expires>UTC_TIMESTAMP()",
            ("id", device), ("user", user));
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task<ChatDevice[]> Devices(string user, string current)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT id,agent,created FROM chat_devices WHERE user_id=@user AND expires>UTC_TIMESTAMP() ORDER BY created DESC
            """, ("user", user));
        var results = new List<ChatDevice>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string id = reader.GetString(0);
            results.Add(new(id, reader.GetString(1), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc), id == current));
        }
        return results.ToArray();
    }

    public async Task<string[]> RevokeDevices(string user, string id, bool others = false)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        string where = others ? "user_id=@user AND id<>@id" : "user_id=@user AND id=@id";
        await using var select = Command(db, tx, "SELECT id FROM chat_devices WHERE " + where + " FOR UPDATE", ("user", user), ("id", id));
        var ids = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        await using var remove = Command(db, tx, "DELETE FROM chat_devices WHERE " + where, ("user", user), ("id", id));
        await remove.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return ids.ToArray();
    }

    public async Task<bool> ReplacePassword(Account account, string next)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "UPDATE chat_accounts SET password=@next WHERE id=@id AND BINARY password=@old",
            ("next", next), ("id", account.Id), ("old", account.Password));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }
}

static class ChatSettingsEndpoints
{
    public const string DeviceClaim = "chat_device";

    public static void Map(RouteGroupBuilder api, ChatStore store)
    {
        api.MapGet("/settings", async (ClaimsPrincipal user) =>
        {
            var id = Id(user);
            return Results.Ok(new {
                username = user.Identity!.Name,
                profile = (await store.GetSetting(id, "profile", new ProfileSettings())) with { AvatarVersion = await store.AvatarVersion(id) },
                notifications = await store.GetSetting(id, "notifications", new NotificationSettings()),
                privacy = await store.GetSetting(id, "privacy", new PrivacySettings()),
                folders = await store.GetSetting<ChatFolder[]>(id, "folders", []),
            });
        });
        api.MapPost("/settings/avatar", async (HttpRequest request, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest("请选择图片。");
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
            if (form.Files.Count != 1 || form.Files[0].Length is < 1 or > 1_048_576) return Results.BadRequest("头像图片不得超过 1 MiB。");
            using var data = new MemoryStream((int)form.Files[0].Length);
            await form.Files[0].CopyToAsync(data, request.HttpContext.RequestAborted);
            byte[] bytes = data.ToArray();
            string mime = ChatFeatureEndpoints.Mime(bytes);
            if (mime is not ("image/png" or "image/jpeg" or "image/webp")) return Results.BadRequest("头像需为 PNG、JPEG 或 WebP。");
            await store.SetAvatar(Id(user), bytes, mime);
            await Changed(hub, user);
            return Results.NoContent();
        });
        api.MapPost("/settings/avatar/remove", async (ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            await store.SetAvatar(Id(user), null, null);
            await Changed(hub, user);
            return Results.NoContent();
        });
        api.MapGet("/avatars/{name}", async (string name, ClaimsPrincipal user, HttpResponse response) =>
        {
            var avatar = await store.Avatar(Id(user), name);
            if (avatar is null) return Results.NotFound();
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(avatar.Content, avatar.Mime);
        });
        api.MapPost("/settings/profile", async (ProfileSettings value, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            if (value.DisplayName is not { Length: <= 60 } || value.Bio is not { Length: <= 240 })
                return Results.BadRequest("资料超出长度上限。");
            await store.PutSetting(Id(user), "profile", new ProfileSettings(value.DisplayName.Trim(), value.Bio.Trim()));
            await Changed(hub, user);
            return Results.NoContent();
        });
        api.MapPost("/settings/notifications", (NotificationSettings value, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
            Put(value, "notifications", user, hub));
        api.MapPost("/settings/privacy", (PrivacySettings value, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
            Put(value, "privacy", user, hub));
        api.MapPost("/settings/folders", async (ChatFolder[] value, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            if (value.Length > 12 || value.Any(f => f is null || string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 30 ||
                f.Kind is not ("all" or "direct" or "group" or "channel" or "unread") ||
                f.Include?.Length > 200 || f.Exclude?.Length > 200)) return Results.BadRequest("文件夹名称、类型或数量无效。");
            string[] names = value.Select(f => f.Name.Trim()).ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) return Results.BadRequest("文件夹名称不能重复。");
            var visible = (await store.Rooms(Id(user))).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            if (value.Any(f => (f.Include ?? []).Concat(f.Exclude ?? []).Any(id => !visible.Contains(id))))
                return Results.BadRequest("文件夹包含不可访问的会话。");
            return await Put(value.Select((f, i) => f with { Name = names[i], Include = f.Include?.Distinct().ToArray(),
                Exclude = f.Exclude?.Distinct().ToArray() }).ToArray(), "folders", user, hub);
        });
        api.MapGet("/settings/devices", (ClaimsPrincipal user) => store.Devices(Id(user), Device(user)));
        api.MapPost("/settings/devices/revoke", async (DeviceRevoke input, ClaimsPrincipal user, ChatConnections sockets) =>
        {
            if (!Guid.TryParseExact(input.Id, "D", out _)) return Results.BadRequest();
            if (input.Id == Device(user)) return Results.BadRequest("请使用退出登录结束当前会话。");
            sockets.Abort(await store.RevokeDevices(Id(user), input.Id));
            return Results.NoContent();
        });
        api.MapPost("/settings/devices/revoke-others", async (ClaimsPrincipal user, ChatConnections sockets) =>
        {
            sockets.Abort(await store.RevokeDevices(Id(user), Device(user), others: true));
            return Results.NoContent();
        });
        api.MapPost("/settings/password", async (PasswordChange input, ClaimsPrincipal user, PasswordHasher<Account> hasher, ChatConnections sockets) =>
        {
            if (input.Current is not { Length: >= 1 and <= 128 } || input.NewPassword is not { Length: >= 10 and <= 128 })
                return Results.BadRequest("新密码至少 10 位。");
            var account = await store.Find(user.Identity!.Name!);
            if (account is null || hasher.VerifyHashedPassword(account, account.Password, input.Current) == PasswordVerificationResult.Failed)
                return Results.BadRequest("当前密码不正确。");
            if (!await store.ReplacePassword(account, hasher.HashPassword(account, input.NewPassword))) return Results.Conflict("密码已变更，请重试。");
            sockets.Abort(await store.RevokeDevices(Id(user), Device(user), others: true));
            return Results.NoContent();
        });

        async Task<IResult> Put<T>(T value, string section, ClaimsPrincipal user, IHubContext<ChatHub> hub)
        {
            await store.PutSetting(Id(user), section, value);
            await Changed(hub, user);
            return Results.NoContent();
        }
    }

    private static Task Changed(IHubContext<ChatHub> hub, ClaimsPrincipal user) => hub.Clients.User(Id(user)).SendAsync("settingsChanged");
    public static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
    public static string Device(ClaimsPrincipal user) => user.FindFirstValue(DeviceClaim)!;
}
