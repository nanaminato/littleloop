using System.Data.Common;

enum BotFriendAccessState { Unmanaged, Inactive, Active }

sealed record BotFriendAccessChange(string UserId, string RobotId, string State, string? RoomId);
sealed record BotFriendAccessEntry(string UserId, string Username, bool Listed, string State, string? RoomId);

sealed partial class ChatStore
{
    private enum BotFriendAction { Request, Allowlist, Decide, Revoke }
    private sealed record BotFriendGrant(bool Listed, string State, string? RoomId)
    {
        public bool Granted => State is "automatic" or "manual";
    }

    private static async Task UpgradeBotFriendAccess(DbConnection db)
    {
        await using var create = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_bot_friend_access (
                robot_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL, listed BOOLEAN NOT NULL,
                state VARCHAR(12) NOT NULL, room_id CHAR(36) NULL,
                PRIMARY KEY(robot_id,user_id),
                FOREIGN KEY(robot_id) REFERENCES chat_accounts(id),
                FOREIGN KEY(user_id) REFERENCES chat_accounts(id),
                FOREIGN KEY(room_id) REFERENCES chat_rooms(id)
            );
            CREATE TABLE IF NOT EXISTS chat_bot_friend_access_gate (id INT PRIMARY KEY);
            INSERT IGNORE INTO chat_bot_friend_access_gate(id) SELECT 1;
            """);
        await create.ExecuteNonQueryAsync();
    }

    public Task<BotFriendAccessChange?> RequestBotFriend(string user, string robot) =>
        ChangeBotFriend(robot, user, BotFriendAction.Request);

    public Task<BotFriendAccessChange?> SetBotFriendAllowlist(string robot, string username, bool listed) =>
        ChangeBotFriend(robot, username, BotFriendAction.Allowlist, listed);

    public Task<BotFriendAccessChange?> DecideBotFriend(string robot, string user, bool accepted) =>
        ChangeBotFriend(robot, user, BotFriendAction.Decide, accepted);

    public Task<BotFriendAccessChange?> RevokeBotFriend(string robot, string user) =>
        ChangeBotFriend(robot, user, BotFriendAction.Revoke);

    public async Task<BotFriendAccessEntry[]> GetBotFriendAccess(string robot)
    {
        await using var db = await Open();
        await using var query = Command(db, null, """
            SELECT a.id,a.name,p.listed,COALESCE(p.state,'pending'),p.room_id
            FROM chat_accounts a
            LEFT JOIN chat_bot_friend_access p ON p.robot_id=@robot AND p.user_id=a.id
            LEFT JOIN chat_friends f ON f.requester_id=a.id AND NOT f.accepted
                AND ((f.low_id=a.id AND f.high_id=@robot) OR (f.high_id=a.id AND f.low_id=@robot))
            WHERE a.id<>@robot AND (p.user_id IS NOT NULL OR f.requester_id IS NOT NULL)
            ORDER BY a.name
            """, ("robot", robot));
        var results = new List<BotFriendAccessEntry>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (!IsRobot(reader.GetString(1)))
                results.Add(new(reader.GetString(0), reader.GetString(1), !reader.IsDBNull(2) && reader.GetBoolean(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return results.ToArray();
    }

    public async Task<BotFriendAccessState> ReadBotFriendAccess(string user, string robot, string? room = null)
    {
        await using var db = await Open();
        return await ReadBotFriendAccess(db, null, user, robot, room);
    }

    internal static async Task<BotFriendAccessState> ReadBotFriendAccess(DbConnection db, DbTransaction? tx,
        string user, string robot, string? room = null)
    {
        var (low, high) = FriendPair(user, robot);
        await using var query = Command(db, tx, """
            SELECT CASE WHEN (p.state='manual' OR (p.state='automatic' AND p.listed))
                AND f.accepted AND f.requester_id=@user AND b.enabled
                AND (@room IS NULL OR p.room_id=@room)
                AND NOT EXISTS (SELECT 1 FROM chat_blocks x WHERE
                    (x.blocker_id=@user AND x.target_id=@robot) OR (x.blocker_id=@robot AND x.target_id=@user))
                THEN TRUE ELSE FALSE END
            FROM (SELECT 1) seed
            LEFT JOIN chat_bot_friend_access p ON p.robot_id=@robot AND p.user_id=@user
            LEFT JOIN chat_friends f ON f.low_id=@low AND f.high_id=@high
            LEFT JOIN chat_bot_rooms b ON b.room_id=p.room_id
            WHERE (p.user_id IS NOT NULL OR (f.requester_id=@user AND NOT f.accepted)) AND (@room IS NULL OR EXISTS (
                SELECT 1 FROM chat_rooms r WHERE r.id=@room AND r.type='direct' AND r.direct_key=@key))
            """, ("user", user), ("robot", robot), ("room", room), ("low", low), ("high", high),
            ("key", low + ":" + high));
        var active = await query.ExecuteScalarAsync();
        return active is null ? BotFriendAccessState.Unmanaged :
            Convert.ToBoolean(active) ? BotFriendAccessState.Active : BotFriendAccessState.Inactive;
    }

    private async Task<BotFriendAccessChange?> ChangeBotFriend(string robot, string participant,
        BotFriendAction action, bool decision = false)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        // Serialize friendship policy changes, then lock the room before changing its access state.
        // The chat message path does not acquire this gate.
        await using (var gate = Command(db, tx, "SELECT id FROM chat_bot_friend_access_gate WHERE id=1 FOR UPDATE"))
            await gate.ExecuteScalarAsync();
        await using (var chatbot = Command(db, tx, "SELECT 1 FROM chat_accounts WHERE id=@id AND name=@name AND password=@marker",
            ("id", robot), ("name", BotName), ("marker", RobotCredential)))
            if (await chatbot.ExecuteScalarAsync() is null) return null;
        string user;
        string accountColumn = action == BotFriendAction.Allowlist ? "name" : "id";
        await using (var candidate = Command(db, tx, $"SELECT id,name FROM chat_accounts WHERE {accountColumn}=@value",
            ("value", participant)))
        await using (var reader = await candidate.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync() || IsRobot(reader.GetString(1))) return null;
            user = reader.GetString(0);
        }

        var (low, high) = FriendPair(user, robot);
        string key = low + ":" + high;
        string? room;
        await using (var direct = Command(db, tx, "SELECT id FROM chat_rooms WHERE direct_key=@key FOR UPDATE", ("key", key)))
            room = await direct.ExecuteScalarAsync() as string;
        var current = await FindBotFriendGrant(db, tx, user, robot, low, high, room);
        var next = NextBotFriendGrant(current, action, decision);
        if (next is null) return null;
        if ((action == BotFriendAction.Request || !current.Granted && next.Granted) && await Blocked(db, tx, user, robot)) return null;
        if (next == current && action != BotFriendAction.Request)
            return new(user, robot, next.State, next.RoomId);

        bool opening = next.Granted && (!current.Granted || action == BotFriendAction.Request);
        if (opening)
        {
            room = await SaveRoom(db, tx, "direct", "", key, null, [user, robot]);
            next = next with { RoomId = room };
        }
        if (room is not null && (opening || !next.Granted))
        {
            await using var enable = Command(db, tx, """
                INSERT INTO chat_bot_rooms(room_id,enabled) VALUES(@room,@enabled)
                ON DUPLICATE KEY UPDATE enabled=@enabled
                """, ("room", room), ("enabled", next.Granted));
            await enable.ExecuteNonQueryAsync();
            if (!next.Granted) await CancelOldBotFriendWork(db, tx, user, room);
        }
        await using (var grant = Command(db, tx, """
            INSERT INTO chat_bot_friend_access(robot_id,user_id,listed,state,room_id)
            VALUES(@robot,@user,@listed,@state,@room)
            ON DUPLICATE KEY UPDATE listed=@listed,state=@state,room_id=@room
            """, ("robot", robot), ("user", user), ("listed", next.Listed), ("state", next.State), ("room", next.RoomId)))
            await grant.ExecuteNonQueryAsync();
        await UpdateBotFriendship(db, tx, user, low, high, next);
        await tx.CommitAsync();
        return new(user, robot, next.State, next.RoomId);
    }

    private static async Task<BotFriendGrant> FindBotFriendGrant(DbConnection db, DbTransaction tx, string user,
        string robot, string low, string high, string? room)
    {
        await using (var query = Command(db, tx, """
            SELECT listed,state,room_id FROM chat_bot_friend_access WHERE robot_id=@robot AND user_id=@user
            """, ("robot", robot), ("user", user)))
        await using (var reader = await query.ExecuteReaderAsync())
            if (await reader.ReadAsync())
                return new(reader.GetBoolean(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        // A request could have arrived before the service supported robot access.
        await using var oldRequest = Command(db, tx, """
            SELECT 1 FROM chat_friends WHERE low_id=@low AND high_id=@high AND requester_id=@user AND NOT accepted
            """, ("low", low), ("high", high), ("user", user));
        return new(false, await oldRequest.ExecuteScalarAsync() is null ? "none" : "pending", room);
    }

    private static BotFriendGrant? NextBotFriendGrant(BotFriendGrant current, BotFriendAction action, bool decision) =>
        action switch
        {
            BotFriendAction.Request when current.Granted => current,
            BotFriendAction.Request => current with { State = current.Listed ? "automatic" : "pending" },
            BotFriendAction.Allowlist when decision && current.State == "pending" =>
                current with { Listed = true, State = "automatic" },
            BotFriendAction.Allowlist when !decision && current.State == "automatic" =>
                current with { Listed = false, State = "revoked" },
            BotFriendAction.Allowlist => current with { Listed = decision },
            BotFriendAction.Decide when current.State == "pending" =>
                current with { State = decision ? "manual" : "rejected" },
            BotFriendAction.Decide when decision && current.Granted || !decision && current.State == "rejected" => current,
            BotFriendAction.Revoke when current.State != "none" => current with { Listed = false, State = "revoked" },
            _ => null
        };

    private static async Task UpdateBotFriendship(DbConnection db, DbTransaction tx, string user, string low, string high,
        BotFriendGrant grant)
    {
        if (grant.State == "none") return;
        const string upsert = """
            INSERT INTO chat_friends(low_id,high_id,requester_id,accepted) VALUES(@low,@high,@user,@accepted)
            ON DUPLICATE KEY UPDATE requester_id=@user,accepted=@accepted
            """;
        const string delete = "DELETE FROM chat_friends WHERE low_id=@low AND high_id=@high";
        await using var change = grant.State is "revoked" or "rejected"
            ? Command(db, tx, delete, ("low", low), ("high", high))
            : Command(db, tx, upsert, ("low", low), ("high", high), ("user", user), ("accepted", grant.Granted));
        await change.ExecuteNonQueryAsync();
    }

    private static async Task CancelOldBotFriendWork(DbConnection db, DbTransaction tx, string user, string room)
    {
        const string reason = "机器人访问已撤销，旧任务已停止。";
        await using (var inbox = Command(db, tx, """
            UPDATE chat_goal_inbox SET processed=TRUE WHERE room_id=@room AND owner_id=@user AND NOT processed
            """, ("room", room), ("user", user)))
            await inbox.ExecuteNonQueryAsync();

        var pending = new List<BotWork>();
        await using (var work = Command(db, tx, """
            SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work
            WHERE room_id=@room AND owner_id=@user AND status IN ('queued','deciding','waiting-input','paused','running')
            FOR UPDATE
            """, ("room", room), ("user", user)))
        await using (var reader = await work.ExecuteReaderAsync())
            while (await reader.ReadAsync()) pending.Add(ReadWork(reader));
        foreach (var old in pending)
        {
            if (old.Goal is { } goal)
            {
                await SaveGoal(db, tx, old, goal.Change("cancel", "", goal.Version), old.Sequence, "access-revoked", reason);
                continue;
            }
            await using var update = Command(db, tx, "UPDATE chat_bot_work SET status='cancelled',error=@error WHERE id=@id",
                ("id", old.Id.ToString()), ("error", reason));
            await update.ExecuteNonQueryAsync();
        }
    }
}
