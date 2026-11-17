using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;

[Flags]
enum ChannelRights { Publish = 1, Edit = 2, Delete = 4, Pin = 8, Profile = 16, Subscribers = 32, Discussion = 64 }

sealed record ChannelAdmin(string Name, ChannelRights Rights);
sealed record ChannelAuditEntry(long Id, string Actor, string Action, string Detail, DateTime? CreatedAt);
sealed record ChannelProfile(string Title, bool SignPosts);
sealed record ChannelOwnership(string Username);
sealed record SetChannelAdmin(ChannelRights Rights);

sealed partial class ChatStore
{
    public async Task InitializeChannelAdministration()
    {
        await using var db = await Open();
        await using var command = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_channel_admins (
                room_id CHAR(36) NOT NULL,
                user_id CHAR(36) NOT NULL,
                rights INT NOT NULL,
                PRIMARY KEY(room_id,user_id),
                FOREIGN KEY(room_id) REFERENCES chat_rooms(id),
                FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            );
            CREATE TABLE IF NOT EXISTS chat_channel_profiles (
                room_id CHAR(36) PRIMARY KEY,
                sign_posts BOOLEAN NOT NULL,
                FOREIGN KEY(room_id) REFERENCES chat_rooms(id)
            );
            CREATE TABLE IF NOT EXISTS chat_channel_audit (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                room_id CHAR(36) NOT NULL,
                actor_id CHAR(36) NOT NULL,
                action VARCHAR(40) NOT NULL,
                detail VARCHAR(255) NOT NULL,
                created_at DATETIME(6) NOT NULL,
                INDEX ix_chat_channel_audit_room(room_id,id)
            )
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RecordChannelAction(DbConnection db, DbTransaction tx, string room,
        string actor, string action, string detail)
    {
        await using var entry = Command(db, tx, """
            INSERT INTO chat_channel_audit(room_id,actor_id,action,detail,created_at)
            VALUES(@room,@actor,@action,@detail,UTC_TIMESTAMP(6))
            """, ("room", room), ("actor", actor), ("action", action), ("detail", detail));
        await entry.ExecuteNonQueryAsync();
    }

    private static async Task RecordChannelPostChange(DbConnection db, DbTransaction tx, string room,
        string actor, string action, long sequence)
    {
        await using var entry = Command(db, tx, """
            INSERT INTO chat_channel_audit(room_id,actor_id,action,detail,created_at)
            SELECT id,@actor,@action,@sequence,UTC_TIMESTAMP(6)
            FROM chat_rooms WHERE id=@room AND type='channel'
            """, ("room", room), ("actor", actor), ("action", action), ("sequence", sequence));
        await entry.ExecuteNonQueryAsync();
    }

    public async Task<(int Status, ChannelAuditEntry[] Entries)> ChannelAudit(string room, string user, long? before)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var access = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@room AND type='channel' FOR SHARE
            """, ("room", room));
        if (await access.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, room, user))
            return (404, []);
        await using var query = Command(db, tx, """
            SELECT log.id,a.name,log.action,log.detail,log.created_at
            FROM chat_channel_audit log JOIN chat_accounts a ON a.id=log.actor_id
            WHERE log.room_id=@room AND log.id<@before
            ORDER BY log.id DESC LIMIT 50
            """, ("room", room), ("before", before ?? long.MaxValue));
        if (user != owner) return (403, []);
        var entries = new List<ChannelAuditEntry>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), ReadTimestamp(reader, 4)));
        return (200, entries.ToArray());
    }

    public async Task<(int Status, string[] Recipients)> TransferChannelOwnership(string room, string user, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32) return (400, []);
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var read = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@room AND type='channel' FOR UPDATE
            """, ("room", room));
        if (await read.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, room, user))
            return (404, []);
        if (owner != user) return (403, []);
        await using var member = Command(db, tx, """
            SELECT a.id FROM chat_accounts a JOIN chat_members m ON m.user_id=a.id
            WHERE a.name=@name AND m.room_id=@room AND a.password<>@robot FOR SHARE
            """, ("name", name.Trim().ToLowerInvariant()), ("room", room), ("robot", RobotCredential));
        if (await member.ExecuteScalarAsync() is not string target) return (404, []);
        if (target == user) return (400, []);
        await using var update = Command(db, tx, """
            UPDATE chat_rooms SET owner_id=@target,revision=revision+1 WHERE id=@room;
            DELETE FROM chat_channel_admins WHERE room_id=@room AND user_id IN (@target,@user);
            """, ("room", room), ("target", target), ("user", user));
        await update.ExecuteNonQueryAsync();
        await RecordChannelAction(db, tx, room, user, "ownership_transferred", target);
        var recipients = await MessageRecipients(db, tx, room);
        await tx.CommitAsync();
        return (204, recipients);
    }

    public async Task<(int Status, string[] Recipients, DiscussionRoomChange? Discussion)> DeleteChannel(string room, string user)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var read = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@room AND type='channel' FOR UPDATE
            """, ("room", room));
        if (await read.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, room, user))
            return (404, [], null);
        if (owner != user) return (403, [], null);
        await using var link = Command(db, tx, """
            SELECT group_id FROM chat_channel_discussions WHERE channel_id=@room FOR UPDATE
            """, ("room", room));
        DiscussionRoomChange? discussion = null;
        if (await link.ExecuteScalarAsync() is string group)
        {
            await using var groupLock = Command(db, tx, "SELECT id FROM chat_rooms WHERE id=@group FOR UPDATE", ("group", group));
            await groupLock.ExecuteScalarAsync();
            discussion = await DetachChannelDiscussion(db, tx, room, group);
        }
        var recipients = await MessageRecipients(db, tx, room);
        // Keep the room identity for mirrored posts that remain in discussion group history.
        await using var delete = Command(db, tx, """
            UPDATE chat_rooms SET type='deleted',owner_id=NULL,revision=revision+1 WHERE id=@room;
            DELETE FROM chat_channel_admins WHERE room_id=@room;
            DELETE FROM chat_members WHERE room_id=@room;
            """, ("room", room));
        await delete.ExecuteNonQueryAsync();
        await RecordChannelAction(db, tx, room, user, "channel_deleted", "");
        await tx.CommitAsync();
        return (204, recipients, discussion);
    }

    public async Task<(int Status, string[] Recipients)> UpdateChannelProfile(string room, string user, ChannelProfile input)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 100) return (400, []);
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var access = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@room AND type='channel' FOR UPDATE
            """, ("room", room));
        if (await access.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, room, user))
            return (404, []);
        if (user != owner && !await HasChannelRight(db, tx, room, user, ChannelRights.Profile))
            return (403, []);
        await using var update = Command(db, tx, """
            UPDATE chat_rooms SET title=@title,revision=revision+1 WHERE id=@room;
            INSERT INTO chat_channel_profiles(room_id,sign_posts) VALUES(@room,@signed)
            ON DUPLICATE KEY UPDATE sign_posts=@signed;
            """, ("room", room), ("title", title), ("signed", input.SignPosts));
        await update.ExecuteNonQueryAsync();
        await RecordChannelAction(db, tx, room, user, "profile_changed", title);
        var recipients = await MessageRecipients(db, tx, room);
        await tx.CommitAsync();
        return (204, recipients);
    }

    private static async Task<bool> HasChannelRight(DbConnection db, DbTransaction tx,
        string room, string user, ChannelRights right)
    {
        await using var command = Command(db, tx, """
            SELECT rights FROM chat_channel_admins WHERE room_id=@room AND user_id=@user FOR SHARE
            """, ("room", room), ("user", user));
        return await command.ExecuteScalarAsync() is int rights && (((ChannelRights)rights & right) == right);
    }

    public async Task<ChannelAdmin[]?> ChannelAdmins(string room, string user)
    {
        await using var db = await Open();
        if (!await IsMember(db, null, room, user)) return null;
        await using var command = Command(db, null, """
            SELECT a.name, g.rights
            FROM chat_channel_admins g JOIN chat_accounts a ON a.id=g.user_id
            JOIN chat_members m ON m.room_id=g.room_id AND m.user_id=g.user_id
            WHERE g.room_id=@room ORDER BY a.name
            """, ("room", room));
        await using var reader = await command.ExecuteReaderAsync();
        var admins = new List<ChannelAdmin>();
        while (await reader.ReadAsync()) admins.Add(new(reader.GetString(0), (ChannelRights)reader.GetInt32(1)));
        return admins.ToArray();
    }

    private static async Task RemoveChannelSubscription(DbConnection db, DbTransaction tx, string room, string user)
    {
        await using var remove = Command(db, tx, """
            DELETE FROM chat_channel_admins WHERE room_id=@room AND user_id=@user;
            DELETE FROM chat_members WHERE room_id=@room AND user_id=@user;
            """, ("room", room), ("user", user));
        await remove.ExecuteNonQueryAsync();
    }

    public async Task<(int Status, string? Target)> RemoveChannelSubscriber(string room, string user, string name)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var channel = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@room AND type='channel' FOR UPDATE
            """, ("room", room));
        if (await channel.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, room, user))
            return (404, null);
        if (user != owner && !await HasChannelRight(db, tx, room, user, ChannelRights.Subscribers))
            return (403, null);
        await using var account = Command(db, tx, """
            SELECT a.id FROM chat_accounts a JOIN chat_members m ON m.user_id=a.id
            WHERE a.name=@name AND m.room_id=@room FOR SHARE
            """, ("name", name), ("room", room));
        if (await account.ExecuteScalarAsync() is not string target) return (404, null);
        if (target == owner) return (403, null);
        if (user != owner)
        {
            await using var admin = Command(db, tx, """
                SELECT 1 FROM chat_channel_admins WHERE room_id=@room AND user_id=@target FOR SHARE
                """, ("room", room), ("target", target));
            if (await admin.ExecuteScalarAsync() is not null) return (403, null);
        }
        await RemoveChannelSubscription(db, tx, room, target);
        await RecordChannelAction(db, tx, room, user, "subscriber_removed", target);
        await tx.CommitAsync();
        return (204, target);
    }

    public async Task<(int Status, string[] Recipients)> SetChannelAdministrator(
        string room, string user, string name, ChannelRights rights)
    {
        const ChannelRights all = ChannelRights.Publish | ChannelRights.Edit | ChannelRights.Delete
            | ChannelRights.Pin | ChannelRights.Profile | ChannelRights.Subscribers | ChannelRights.Discussion;
        if ((rights & ~all) != 0) return (400, []);
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var channel = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@room AND type='channel' FOR UPDATE
            """, ("room", room));
        if (await channel.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, room, user))
            return (404, []);
        if (owner != user) return (403, []);
        await using var account = Command(db, tx, """
            SELECT a.id FROM chat_accounts a JOIN chat_members m ON m.user_id=a.id
            WHERE a.name=@name AND m.room_id=@room FOR SHARE
            """, ("name", name), ("room", room));
        if (await account.ExecuteScalarAsync() is not string target) return (404, []);
        if (target == owner) return (400, []);
        await using var change = Command(db, tx, rights == 0
            ? "DELETE FROM chat_channel_admins WHERE room_id=@room AND user_id=@target"
            : """
              INSERT INTO chat_channel_admins(room_id,user_id,rights) VALUES(@room,@target,@rights)
              ON DUPLICATE KEY UPDATE rights=@rights
              """, ("room", room), ("target", target), ("rights", (int)rights));
        await change.ExecuteNonQueryAsync();
        await RecordChannelAction(db, tx, room, user, rights == 0 ? "administrator_revoked" : "administrator_granted",
            $"{target}:{(int)rights}");
        var recipients = await MessageRecipients(db, tx, room);
        await tx.CommitAsync();
        return (204, recipients);
    }
}

static class ChatChannelAdminEndpoints
{
    public static void Map(RouteGroupBuilder api, ChatStore store)
    {
        api.MapDelete("/channels/{room:guid}", async (Guid room, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            var result = await store.DeleteChannel(room.ToString(), Id(user));
            if (result.Status == 204)
            {
                await hub.Clients.Users(result.Recipients).SendAsync("roomChanged", room.ToString());
                if (result.Discussion is { } discussion)
                    await hub.Clients.Users(discussion.Recipients).SendAsync("roomChanged", discussion.Room);
            }
            return Results.StatusCode(result.Status);
        });
        api.MapGet("/channels/{room:guid}/audit", async (Guid room, long? before, ClaimsPrincipal user) =>
        {
            var result = await store.ChannelAudit(room.ToString(), Id(user), before);
            return result.Status == 200 ? Results.Ok(result.Entries) : Results.StatusCode(result.Status);
        });
        api.MapPut("/channels/{room:guid}/ownership", async (Guid room, ChannelOwnership input,
            ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            var result = await store.TransferChannelOwnership(room.ToString(), Id(user), input.Username);
            if (result.Status == 204)
                await hub.Clients.Users(result.Recipients).SendAsync("roomChanged", room.ToString());
            return Results.StatusCode(result.Status);
        });
        api.MapPut("/channels/{room:guid}/profile", async (Guid room, ChannelProfile input,
            ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            var result = await store.UpdateChannelProfile(room.ToString(), Id(user), input);
            if (result.Status == 204)
                await hub.Clients.Users(result.Recipients).SendAsync("roomChanged", room.ToString());
            return Results.StatusCode(result.Status);
        });
        api.MapDelete("/channels/{room:guid}/subscribers/{name}", async (Guid room, string name,
            ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            var result = await store.RemoveChannelSubscriber(room.ToString(), Id(user), name);
            if (result.Target is { } target)
                await hub.Clients.User(target).SendAsync("readChanged", room.ToString());
            return Results.StatusCode(result.Status);
        });
        api.MapGet("/channels/{room:guid}/admins", async (Guid room, ClaimsPrincipal user) =>
            await store.ChannelAdmins(room.ToString(), Id(user)) is { } admins
                ? Results.Ok(admins) : Results.NotFound());
        api.MapPut("/channels/{room:guid}/admins/{name}", async (Guid room, string name, SetChannelAdmin input,
            ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            var result = await store.SetChannelAdministrator(room.ToString(), Id(user), name, input.Rights);
            if (result.Status == 204)
                await hub.Clients.Users(result.Recipients).SendAsync("roomChanged", room.ToString());
            return Results.StatusCode(result.Status);
        });
    }

    private static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
