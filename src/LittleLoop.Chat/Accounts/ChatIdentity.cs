using System.Data.Common;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;

sealed record UsernameChange(string Username);
sealed record BlockChange(string Username, bool Blocked);
sealed record BlockedAccount(string Username, string DisplayName);
sealed class DirectMessageBlockedException : Exception;

sealed partial class ChatStore
{
    private static async Task UpgradeIdentity(DbConnection db)
    {
        await using var cmd = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_usernames (
                name VARCHAR(32) PRIMARY KEY, user_id CHAR(36) NOT NULL,
                FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            );
            INSERT IGNORE INTO chat_usernames(name,user_id) SELECT name,id FROM chat_accounts;
            CREATE TABLE IF NOT EXISTS chat_blocks (
                blocker_id CHAR(36) NOT NULL, target_id CHAR(36) NOT NULL, PRIMARY KEY(blocker_id,target_id),
                FOREIGN KEY(blocker_id) REFERENCES chat_accounts(id),
                FOREIGN KEY(target_id) REFERENCES chat_accounts(id)
            );
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Account?> ResolveConfiguredAccount(string username)
    {
        await using var db = await Open();
        await using var command = Command(db, null, """
            SELECT a.id,a.name,a.password FROM chat_usernames n JOIN chat_accounts a ON a.id=n.user_id
            WHERE n.name=@name
            """, ("name", username));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public async Task<string?> ActiveDeviceName(string? user, string? device)
    {
        if (user is null || device is null) return null;
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT a.name FROM chat_devices d JOIN chat_accounts a ON a.id=d.user_id
            WHERE d.id=@id AND d.user_id=@user AND d.expires>UTC_TIMESTAMP()
            """, ("id", device), ("user", user));
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task<bool> Rename(string user, string name)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        try
        {
            await using (var reserve = Command(db, tx, """
                INSERT INTO chat_usernames(name,user_id) VALUES(@name,@user)
                ON DUPLICATE KEY UPDATE user_id=user_id
                """, ("name", name), ("user", user)))
                await reserve.ExecuteNonQueryAsync();
            await using (var check = Command(db, tx, "SELECT user_id FROM chat_usernames WHERE name=@name FOR UPDATE", ("name", name)))
                if (await check.ExecuteScalarAsync() as string != user) return false;
            await using var change = Command(db, tx, "UPDATE chat_accounts SET name=@name WHERE id=@user", ("user", user), ("name", name));
            await change.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return true;
        }
        catch (DbException e) when (ChatStore.IsDuplicate(e)) { return false; }
    }

    public async Task<BlockedAccount[]> Blocks(string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT a.name,COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.displayName')),''),a.name)
            FROM chat_blocks b JOIN chat_accounts a ON a.id=b.target_id
            LEFT JOIN chat_user_settings s ON s.user_id=a.id AND s.section='profile'
            WHERE b.blocker_id=@user ORDER BY a.name
            """, ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<BlockedAccount>();
        while (await reader.ReadAsync()) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result.ToArray();
    }

    public async Task<bool> ChangeBlock(string user, string name, bool blocked)
    {
        var target = await Find(name);
        if (target is null || target.Id == user || IsRobot(target.Name)) return false;
        await using var db = await Open();
        string sql = blocked
            ? "INSERT INTO chat_blocks(blocker_id,target_id) VALUES(@user,@target) ON DUPLICATE KEY UPDATE target_id=target_id"
            : "DELETE FROM chat_blocks WHERE blocker_id=@user AND target_id=@target";
        await using var cmd = Command(db, null, sql, ("user", user), ("target", target.Id));
        await cmd.ExecuteNonQueryAsync();
        return true;
    }

    private static async Task<bool> Blocked(DbConnection db, DbTransaction? tx, string first, string second)
    {
        await using var cmd = Command(db, tx, """
            SELECT 1 FROM chat_blocks WHERE (blocker_id=@first AND target_id=@second)
              OR (blocker_id=@second AND target_id=@first) LIMIT 1
            """ + (tx is null ? "" : " FOR SHARE"), ("first", first), ("second", second));
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task<(string Room, string User)[]> SharedPeers(string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT mine.room_id,peer.user_id FROM chat_members mine
            JOIN chat_members peer ON mine.room_id=peer.room_id AND peer.user_id<>mine.user_id
            WHERE mine.user_id=@user
            """, ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        var peers = new List<(string, string)>();
        while (await reader.ReadAsync()) peers.Add((reader.GetString(0), reader.GetString(1)));
        return peers.ToArray();
    }
}

static class IdentityEndpoints
{
    public static void Map(RouteGroupBuilder api, ChatStore store)
    {
        api.MapPost("/settings/username", async (UsernameChange input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            string name = Normalize(input.Username);
            if (!Regex.IsMatch(name, @"\A[a-z0-9_]{3,32}\z") || ChatStore.IsRobot(name))
                return Results.BadRequest(new { code = "invalid_username" });
            string id = ChatSettingsEndpoints.Id(user);
            if (!await store.Rename(id, name)) return Results.Conflict(new { code = "username_taken" });
            await hub.Clients.User(id).SendAsync("settingsChanged");
            // A rename changes display on existing direct chats without increasing their unread count.
            foreach (var group in (await store.SharedPeers(id)).GroupBy(x => x.Room))
                await hub.Clients.Users(group.Select(x => x.User)).SendAsync("identityChanged", group.Key);
            return Results.Ok(new { username = name });
        });
        api.MapGet("/settings/blocks", (ClaimsPrincipal user) => store.Blocks(ChatSettingsEndpoints.Id(user)));
        api.MapPost("/settings/blocks", async (BlockChange input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            string id = ChatSettingsEndpoints.Id(user);
            if (!await store.ChangeBlock(id, Normalize(input.Username), input.Blocked))
                return Results.BadRequest(new { code = "invalid_block_target" });
            await hub.Clients.User(id).SendAsync("settingsChanged");
            return Results.NoContent();
        });
    }

    private static string Normalize(string? username) => (username ?? "").Trim().ToLowerInvariant();
}
