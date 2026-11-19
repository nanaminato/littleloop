using System.Data.Common;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;

sealed record FriendEntry(string Id, string Username, string DisplayName, string State);
sealed record FriendRequest(string Username);
sealed record FriendDecision(bool Accepted);
sealed record GroupMemberChange(string Username, string Operation);

sealed partial class ChatStore
{
    private static async Task UpgradeSocial(DbConnection db)
    {
        await using var create = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_friends (
                low_id CHAR(36) NOT NULL, high_id CHAR(36) NOT NULL, requester_id CHAR(36) NOT NULL,
                accepted BOOLEAN NOT NULL DEFAULT FALSE, PRIMARY KEY(low_id,high_id), INDEX(high_id),
                FOREIGN KEY(low_id) REFERENCES chat_accounts(id), FOREIGN KEY(high_id) REFERENCES chat_accounts(id),
                FOREIGN KEY(requester_id) REFERENCES chat_accounts(id)
            );
            """);
        await create.ExecuteNonQueryAsync();
        await UpgradeBotFriendAccess(db);
        // SQLite starts with the corrected schema; this migrates only historical MySQL schemas.
        if (db is Microsoft.Data.Sqlite.SqliteConnection) return;
        await using var lookup = Command(db, null, """
            SELECT DISTINCT CONSTRAINT_NAME FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='chat_messages' AND REFERENCED_TABLE_NAME='chat_members'
            """);
        if (await lookup.ExecuteScalarAsync() is not string former) return;
        string quoted = "`" + former.Replace("`", "``") + "`";
        // Historical messages keep their sender when that sender loses membership.
        await using var alter = Command(db, null, $"""
            ALTER TABLE chat_messages
              ADD CONSTRAINT fk_chat_message_room FOREIGN KEY(room_id) REFERENCES chat_rooms(id),
              ADD CONSTRAINT fk_chat_message_sender FOREIGN KEY(sender_id) REFERENCES chat_accounts(id),
              DROP FOREIGN KEY {quoted}
            """);
        await alter.ExecuteNonQueryAsync();
    }

    public async Task<FriendEntry[]> Friends(string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT a.id,a.name,COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.displayName')),''),a.name),
              CASE WHEN f.accepted THEN 'friend' WHEN f.requester_id=@user THEN 'outgoing' ELSE 'incoming' END
            FROM chat_friends f JOIN chat_accounts a ON a.id=IF(f.low_id=@user,f.high_id,f.low_id)
            LEFT JOIN chat_user_settings s ON s.user_id=a.id AND s.section='profile'
            WHERE (f.low_id=@user OR f.high_id=@user) AND NOT EXISTS(
              SELECT 1 FROM chat_blocks b WHERE (b.blocker_id=@user AND b.target_id=a.id)
                   OR (b.blocker_id=a.id AND b.target_id=@user))
            ORDER BY a.name
            """, ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<FriendEntry>();
        while (await reader.ReadAsync()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows.ToArray();
    }

    public async Task<string?> RequestFriend(string user, string username)
    {
        await using var db = await Open();
        var target = await Find(db, username);
        if (target is null || target.Id == user || target.Name == ReviewBotName || await Blocked(db, null, user, target.Id)) return null;
        var (low, high) = FriendPair(user, target.Id);
        await using var insert = Command(db, null, """
            INSERT INTO chat_friends(low_id,high_id,requester_id) VALUES(@low,@high,@user)
            ON DUPLICATE KEY UPDATE requester_id=requester_id
            """, ("low", low), ("high", high), ("user", user));
        await insert.ExecuteNonQueryAsync();
        return target.Id;
    }

    public async Task<bool> RespondFriend(string user, string peer, bool accepted)
    {
        if (user == peer) return false;
        await using var db = await Open();
        if (accepted && await Blocked(db, null, user, peer)) return false;
        var (low, high) = FriendPair(user, peer);
        const string where = " WHERE low_id=@low AND high_id=@high AND requester_id=@peer AND NOT accepted";
        await using var cmd = Command(db, null, (accepted ? "UPDATE chat_friends SET accepted=TRUE" : "DELETE FROM chat_friends") + where,
            ("low", low), ("high", high), ("peer", peer));
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    private static (string Low, string High) FriendPair(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? (a,b) : (b,a);

    public async Task InitializeGroupModeration()
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_group_moderation (
                group_id CHAR(36) NOT NULL,
                user_id CHAR(36) NOT NULL,
                administrator BOOLEAN NOT NULL DEFAULT FALSE,
                silenced BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY(group_id,user_id),
                FOREIGN KEY(group_id) REFERENCES chat_rooms(id),
                FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            )
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> GroupModeration(DbConnection db, DbTransaction tx,
        string room, string user)
    {
        await using var query = Command(db, tx, """
            SELECT g.administrator,g.silenced FROM chat_rooms r
            JOIN chat_group_moderation g ON g.group_id=IF(r.type='topic',r.parent_id,r.id) AND g.user_id=@user
            WHERE r.id=@room AND r.type IN ('group','topic')
            """, ("room", room), ("user", user));
        await using var reader = await query.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetBoolean(0) ? 1 : 0) | (reader.GetBoolean(1) ? 2 : 0) : 0;
    }

    public async Task<(int Status, string[] Recipients)> ChangeGroupMember(string user, string group, string username, string operation)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var guard = Command(db, tx, "SELECT owner_id FROM chat_rooms WHERE id=@room AND type='group' FOR UPDATE", ("room", group));
        var owner = await guard.ExecuteScalarAsync();
        if (owner is null || !await IsMember(db, tx, group, user)) return (404, []);
        bool ownsGroup = owner as string == user;
        if (!ownsGroup && (operation is not ("remove" or "silence" or "unsilence")
            || (await GroupModeration(db, tx, group, user) & 1) == 0)) return (403, []);
        var target = await Find(db, username, tx);
        if (target is null || target.Name == ReviewBotName) return (404, []);
        if (target.Id == user) return (400, []);
        if (Equals(owner, target.Id)) return (403, []);
        if (!ownsGroup && (await GroupModeration(db, tx, group, target.Id) & 1) != 0) return (403, []);
        if (operation is "promote" or "demote" or "silence" or "unsilence")
        {
            if (!await IsMember(db, tx, group, target.Id)) return (404, []);
            string column = operation is "promote" or "demote" ? "administrator" : "silenced";
            await using var moderation = Command(db, tx, $"""
                INSERT INTO chat_group_moderation(group_id,user_id,{column}) VALUES(@room,@target,@value)
                ON DUPLICATE KEY UPDATE {column}=@value
                """, ("room", group), ("target", target.Id), ("value", operation is "promote" or "silence"));
            await moderation.ExecuteNonQueryAsync();
            var members = await MessageRecipients(db, tx, group);
            await tx.CommitAsync();
            return (204, members);
        }
        if (operation is "remove" or "transfer")
        {
            await using var clear = Command(db, tx, """
                DELETE FROM chat_group_moderation WHERE group_id=@room AND user_id=@target
                """, ("room", group), ("target", target.Id));
            await clear.ExecuteNonQueryAsync();
        }
        if (operation is "approve_join" or "decline_join")
        {
            await using var request = Command(db, tx, """
                DELETE FROM chat_discussion_join_requests WHERE group_id=@room AND user_id=@target
                """, ("room", group), ("target", target.Id));
            if (await request.ExecuteNonQueryAsync() == 0) return (404, []);
            if (operation == "decline_join")
            {
                await tx.CommitAsync();
                return (204, [user, target.Id]);
            }
            if (await Blocked(db, tx, user, target.Id)) return (403, []);
            operation = "accept_invited_join";
        }
        if (operation == "invite" && (await Blocked(db, tx, user, target.Id) ||
            !(await GetSetting(target.Id, "privacy", new PrivacySettings())).GroupInvites)) return (403, []);
        if (operation == "transfer")
        {
            if (!await IsMember(db, tx, group, target.Id)) return (404, []);
            await using var update = Command(db, tx, "UPDATE chat_rooms SET owner_id=@owner WHERE id=@room",
                ("owner", target.Id), ("room", group));
            await update.ExecuteNonQueryAsync();
        }
        else
        {
            var rooms = new List<string> { group };
            await using (var children = Command(db, tx, "SELECT id FROM chat_rooms WHERE parent_id=@room AND type='topic' ORDER BY id", ("room", group)))
            await using (var reader = await children.ExecuteReaderAsync())
                while (await reader.ReadAsync()) rooms.Add(reader.GetString(0));
            foreach (string room in rooms)
            {
                if (room != group)
                {
                    await using var hold = Command(db, tx, "SELECT id FROM chat_rooms WHERE id=@room FOR UPDATE", ("room", room));
                    await hold.ExecuteScalarAsync();
                }
                string sql = operation is "invite" or "accept_invited_join" ? """
                    INSERT INTO chat_members(room_id,user_id,read_to)
                    SELECT id,@target,latest FROM chat_rooms WHERE id=@room ON DUPLICATE KEY UPDATE read_to=chat_members.read_to
                    """ : "DELETE FROM chat_members WHERE room_id=@room AND user_id=@target";
                await using var membership = Command(db, tx, sql, ("room", room), ("target", target.Id));
                await membership.ExecuteNonQueryAsync();
            }
        }
        if (operation is "invite" or "remove")
        {
            await using var clearJoin = Command(db, tx, """
                DELETE FROM chat_discussion_join_requests WHERE group_id=@room AND user_id=@target
                """, ("room", group), ("target", target.Id));
            await clearJoin.ExecuteNonQueryAsync();
        }
        var recipients = await MessageRecipients(db, tx, group);
        await tx.CommitAsync();
        return (204, recipients.Append(target.Id).Distinct().ToArray());
    }
}

static class ChatSocialEndpoints
{
    public static void Map(RouteGroupBuilder api, ChatStore store, ChatBot? bot = null)
    {
        api.MapGet("/friends", (ClaimsPrincipal user) => store.Friends(Id(user)));
        api.MapPost("/friends", async (FriendRequest input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            string id = Id(user);
            string? name = Username(input.Username);
            if (name is null) return Results.BadRequest();
            if (name == ChatStore.BotName && bot is not null)
            {
                var change = await store.RequestBotFriend(id, bot.Robot(null).Id);
                if (change is null) return Results.NotFound();
                await bot.NotifyFriendAccess(change);
                return change.RoomId is not null && change.State is ("automatic" or "manual")
                    ? Results.Ok(new { change.RoomId }) : Results.NoContent();
            }
            var peer = await store.RequestFriend(id, name);
            if (peer is null) return Results.NotFound();
            await hub.Clients.Users([id, peer]).SendAsync("socialChanged", "");
            return Results.NoContent();
        });
        api.MapPost("/friends/{peer:guid}/respond", async (Guid peer, FriendDecision input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            string id = Id(user), other = peer.ToString();
            if (bot is not null && (id == bot.Robot(null).Id || other == bot.Robot(null).Id))
                return Results.StatusCode(403);
            if (!await store.RespondFriend(id, other, input.Accepted)) return Results.NotFound();
            await hub.Clients.Users([id, other]).SendAsync("socialChanged", "");
            return Results.NoContent();
        });
        api.MapPost("/rooms/{room:guid}/members", async (Guid room, GroupMemberChange input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            string? name = Username(input.Username);
            if (name is null || input.Operation is not ("invite" or "remove" or "transfer" or "promote" or "demote" or "silence" or "unsilence" or "approve_join" or "decline_join")) return Results.BadRequest();
            var change = await store.ChangeGroupMember(Id(user), room.ToString(), name, input.Operation);
            if (change.Status == 204) await hub.Clients.Users(change.Recipients).SendAsync("socialChanged", room.ToString());
            return Results.StatusCode(change.Status);
        });
    }

    private static string? Username(string? value)
    {
        var name = value?.Trim().ToLowerInvariant();
        return name is not null && Regex.IsMatch(name, @"\A[a-z0-9_]{3,32}\z", RegexOptions.CultureInvariant) ? name : null;
    }
    private static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
