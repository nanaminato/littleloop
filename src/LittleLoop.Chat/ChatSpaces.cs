using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

sealed class ChannelWriteDeniedException : Exception;
sealed record SpaceName(string Title);
sealed record ChangeSubscription(bool Subscribed);
sealed record ChannelSummary(string Id, string Title, bool Subscribed, bool Owner);

sealed partial class ChatStore
{
    public async Task<(string Id, string[] Recipients)?> CreateSpace(string user, string title, string? parent)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        string[] members;
        if (parent is null) members = [user];
        else
        {
            await using var check = Command(db, tx, "SELECT type FROM chat_rooms WHERE id=@room FOR SHARE", ("room", parent));
            if (await check.ExecuteScalarAsync() as string != "group" || !await IsMember(db, tx, parent, user)) return null;
            await using var select = Command(db, tx, """
                SELECT m.user_id FROM chat_members m JOIN chat_accounts a ON a.id=m.user_id
                WHERE m.room_id=@room AND a.password<>@robot ORDER BY m.user_id
                """, ("room", parent), ("robot", RobotCredential));
            var humanMembers = new List<string>();
            await using (var reader = await select.ExecuteReaderAsync())
                while (await reader.ReadAsync()) humanMembers.Add(reader.GetString(0));
            members = humanMembers.ToArray();
        }

        string id = Guid.NewGuid().ToString();
        await using var add = Command(db, tx, """
            INSERT INTO chat_rooms(id,type,title,parent_id,owner_id) VALUES(@id,@type,@title,@parent,@owner)
            """, ("id", id), ("type", parent is null ? "channel" : "topic"), ("title", title),
            ("parent", parent), ("owner", user));
        await add.ExecuteNonQueryAsync();
        foreach (string member in members) await AddMember(db, tx, id, member);
        await tx.CommitAsync();
        return (id, members);
    }

    public async Task<ChannelSummary[]> Channels(string user)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT r.id,r.title,(m.user_id IS NOT NULL),(r.owner_id=@user) FROM chat_rooms r
            LEFT JOIN chat_members m ON m.room_id=r.id AND m.user_id=@user
            WHERE r.type='channel' ORDER BY r.title,r.id LIMIT 200
            """, ("user", user));
        await using var reader = await cmd.ExecuteReaderAsync();
        var channels = new List<ChannelSummary>();
        while (await reader.ReadAsync()) channels.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        return channels.ToArray();
    }

    public async Task<int> Subscribe(string user, string channel, bool enabled)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var read = Command(db, tx, "SELECT owner_id FROM chat_rooms WHERE id=@room AND type='channel' FOR UPDATE", ("room", channel));
        if (await read.ExecuteScalarAsync() is not string owner) return 404;
        if (!enabled && owner == user) return 403;
        if (enabled)
        {
            // A new subscription can read history without treating old broadcasts as unread.
            await using var add = Command(db, tx, """
                INSERT INTO chat_members(room_id,user_id,read_to) SELECT id,@user,latest FROM chat_rooms WHERE id=@room
                ON DUPLICATE KEY UPDATE read_to=chat_members.read_to
                """, ("room", channel), ("user", user));
            await add.ExecuteNonQueryAsync();
        }
        else
        {
            await using var remove = Command(db, tx, "DELETE FROM chat_members WHERE room_id=@room AND user_id=@user",
                ("room", channel), ("user", user));
            await remove.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return 204;
    }
}

static class ChatSpaceEndpoints
{
    public static void Map(RouteGroupBuilder api, ChatStore store)
    {
        api.MapGet("/channels", (ClaimsPrincipal user) => store.Channels(Id(user)));
        api.MapPost("/channels", (SpaceName body, ClaimsPrincipal user, IHubContext<ChatHub> hub) => Create(body, user, hub, null));
        api.MapPost("/rooms/{parent:guid}/topics", (Guid parent, SpaceName body, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
            Create(body, user, hub, parent.ToString()));
        api.MapPost("/channels/{room:guid}/subscription", async (Guid room, ChangeSubscription body, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            int result = await store.Subscribe(Id(user), room.ToString(), body.Subscribed);
            if (result == 204) await hub.Clients.User(Id(user)).SendAsync("readChanged", room.ToString());
            return Results.StatusCode(result);
        });

        async Task<IResult> Create(SpaceName body, ClaimsPrincipal user, IHubContext<ChatHub> hub, string? parent)
        {
            if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 100)
                return Results.BadRequest("请填写 1–100 个字符的名称。");
            var result = await store.CreateSpace(Id(user), body.Title.Trim(), parent);
            if (result is null) return Results.NotFound();
            await hub.Clients.Users(result.Value.Recipients).SendAsync("roomChanged", result.Value.Id);
            return Results.Ok(new { result.Value.Id });
        }
    }

    private static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
