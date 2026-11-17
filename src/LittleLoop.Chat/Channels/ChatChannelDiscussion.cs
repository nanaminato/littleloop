using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using MySqlConnector;

sealed class DiscussionClosedException : Exception;
sealed class GroupSilencedException : Exception;
sealed record FollowDiscussion(bool Following);
sealed record SetPostDiscussion(bool Open);
sealed record DiscussionGroup(string? GroupId);
sealed record DiscussionSummary(long Post, long Count, long Unread, bool Closed);
sealed record ChannelThread(string GroupId, long RootSequence, bool Closed, long Count, ChatMessage[] Messages, long ReadSequence, long Unread, bool CanComment, bool Following);

sealed partial class ChatStore
{
    public async Task InitializeChannelDiscussions()
    {
        await using var db = await Open();
        await using var command = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_channel_discussions (
                channel_id CHAR(36) PRIMARY KEY,
                group_id CHAR(36) NOT NULL UNIQUE,
                FOREIGN KEY(channel_id) REFERENCES chat_rooms(id),
                FOREIGN KEY(group_id) REFERENCES chat_rooms(id)
            );
            CREATE TABLE IF NOT EXISTS chat_channel_threads (
                channel_id CHAR(36) NOT NULL,
                post_seq BIGINT NOT NULL,
                group_id CHAR(36) NOT NULL,
                root_seq BIGINT NOT NULL,
                closed BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY(channel_id,post_seq),
                UNIQUE(group_id,root_seq),
                FOREIGN KEY(channel_id,post_seq) REFERENCES chat_messages(room_id,seq),
                FOREIGN KEY(group_id,root_seq) REFERENCES chat_messages(room_id,seq)
            );
            CREATE TABLE IF NOT EXISTS chat_channel_comments (
                group_id CHAR(36) NOT NULL,
                seq BIGINT NOT NULL,
                root_seq BIGINT NOT NULL,
                PRIMARY KEY(group_id,seq),
                INDEX ix_chat_channel_comments_thread(group_id,root_seq,seq),
                FOREIGN KEY(group_id,seq) REFERENCES chat_messages(room_id,seq),
                FOREIGN KEY(group_id,root_seq) REFERENCES chat_channel_threads(group_id,root_seq)
            );
            CREATE TABLE IF NOT EXISTS chat_channel_comment_reads (
                channel_id CHAR(36) NOT NULL,
                post_seq BIGINT NOT NULL,
                user_id CHAR(36) NOT NULL,
                read_to BIGINT NOT NULL,
                PRIMARY KEY(channel_id,post_seq,user_id),
                FOREIGN KEY(channel_id,post_seq) REFERENCES chat_channel_threads(channel_id,post_seq),
                FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            );
            CREATE TABLE IF NOT EXISTS chat_channel_comment_follows (
                channel_id CHAR(36) NOT NULL,
                post_seq BIGINT NOT NULL,
                user_id CHAR(36) NOT NULL,
                PRIMARY KEY(channel_id,post_seq,user_id),
                FOREIGN KEY(channel_id,post_seq) REFERENCES chat_channel_threads(channel_id,post_seq),
                FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            );
            CREATE TABLE IF NOT EXISTS chat_discussion_join_requests (
                group_id CHAR(36) NOT NULL,
                user_id CHAR(36) NOT NULL,
                requested_at DATETIME(6) NOT NULL,
                PRIMARY KEY(group_id,user_id),
                FOREIGN KEY(group_id) REFERENCES chat_rooms(id),
                FOREIGN KEY(user_id) REFERENCES chat_accounts(id)
            )
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<DiscussionRoomChange?> MirrorChannelPost(
        DbConnection db, DbTransaction tx, string channel, long post)
    {
        await using var link = Command(db, tx, """
            SELECT group_id FROM chat_channel_discussions WHERE channel_id=@channel FOR SHARE
            """, ("channel", channel));
        if (await link.ExecuteScalarAsync() is not string group) return null;
        await using var clock = Command(db, tx, """
            SELECT latest FROM chat_rooms WHERE id=@group AND type='group' FOR UPDATE
            """, ("group", group));
        if (await clock.ExecuteScalarAsync() is not long latest)
            throw new InvalidOperationException("The linked discussion group is unavailable.");
        long root = checked(latest + 1);
        await using var insert = Command(db, tx, """
            INSERT INTO chat_messages(room_id,seq,sender_id,client_id,body,attachment_id,forwarded_from,sent_at)
            SELECT @group,@root,msg.sender_id,@client,msg.body,msg.attachment_id,r.title,msg.sent_at
            FROM chat_messages msg JOIN chat_rooms r ON r.id=msg.room_id
            WHERE msg.room_id=@channel AND msg.seq=@post;
            INSERT INTO chat_channel_threads(channel_id,post_seq,group_id,root_seq)
            VALUES(@channel,@post,@group,@root);
            UPDATE chat_rooms SET latest=@root,last_activity=CURRENT_TIMESTAMP(6) WHERE id=@group;
            """, ("group", group), ("root", root), ("client", Guid.NewGuid().ToString()),
            ("channel", channel), ("post", post));
        await insert.ExecuteNonQueryAsync();
        return new(group, await MessageRecipients(db, tx, group));
    }

    private static async Task<long?> FindCommentRoot(
        DbConnection db, DbTransaction tx, string room, long reply)
    {
        await using var query = Command(db, tx, """
            SELECT t.root_seq,t.closed FROM chat_channel_threads t
            WHERE t.group_id=@room AND t.root_seq=COALESCE(
                (SELECT root_seq FROM chat_channel_comments WHERE group_id=@room AND seq=@reply),@reply)
            FOR SHARE
            """, ("room", room), ("reply", reply));
        await using var reader = await query.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        if (reader.GetBoolean(1)) throw new DiscussionClosedException();
        return reader.GetInt64(0);
    }

    private static async Task<DiscussionRoomChange?> UpdateDiscussionPost(
        DbConnection db, DbTransaction tx, string channel, long post, string? text)
    {
        await using var locate = Command(db, tx, """
            SELECT group_id FROM chat_channel_threads WHERE channel_id=@channel AND post_seq=@post
            """, ("channel", channel), ("post", post));
        if (await locate.ExecuteScalarAsync() is not string group) return null;
        await using var groupLock = Command(db, tx, "SELECT id FROM chat_rooms WHERE id=@group FOR UPDATE", ("group", group));
        await groupLock.ExecuteScalarAsync();
        await using var update = Command(db, tx, """
            UPDATE chat_channel_threads t
            JOIN chat_messages msg ON msg.room_id=t.group_id AND msg.seq=t.root_seq
            JOIN chat_rooms g ON g.id=t.group_id
            SET msg.body=@body,msg.edited=NOT @deleted,msg.deleted=@deleted,
                t.closed=(t.closed OR @deleted),g.revision=g.revision+1,
                g.pinned_message=IF(@deleted AND g.pinned_message=t.root_seq,NULL,g.pinned_message)
            WHERE t.channel_id=@channel AND t.post_seq=@post
            """, ("channel", channel), ("post", post), ("body", text ?? ""), ("deleted", text is null));
        await update.ExecuteNonQueryAsync();
        return new(group, await MessageRecipients(db, tx, group));
    }

    public async Task<(int Status, string[] Recipients, DiscussionRoomChange? Group)> SetChannelPostDiscussion(
        string channel, long post, string user, bool open)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var access = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@channel AND type='channel' FOR UPDATE
            """, ("channel", channel));
        if (await access.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, channel, user))
            return (404, [], null);
        if (user != owner && !await HasChannelRight(db, tx, channel, user, ChannelRights.Discussion))
            return (403, [], null);
        await using var postQuery = Command(db, tx, """
            SELECT deleted FROM chat_messages WHERE room_id=@channel AND seq=@post FOR SHARE
            """, ("channel", channel), ("post", post));
        var deleted = await postQuery.ExecuteScalarAsync();
        if (deleted is null) return (404, [], null);
        if (open && Convert.ToBoolean(deleted)) return (409, [], null);
        await using var thread = Command(db, tx, """
            SELECT group_id FROM chat_channel_threads WHERE channel_id=@channel AND post_seq=@post
            """, ("channel", channel), ("post", post));
        DiscussionRoomChange? changed;
        if (await thread.ExecuteScalarAsync() is string group)
        {
            await using var groupLock = Command(db, tx, "SELECT id FROM chat_rooms WHERE id=@group FOR UPDATE",
                ("group", group));
            await groupLock.ExecuteScalarAsync();
            if (open)
            {
                await using var active = Command(db, tx, """
                    SELECT 1 FROM chat_channel_discussions WHERE channel_id=@channel AND group_id=@group
                    """, ("channel", channel), ("group", group));
                if (await active.ExecuteScalarAsync() is null) return (409, [], null);
            }
            await using var update = Command(db, tx, """
                UPDATE chat_channel_threads SET closed=@closed WHERE channel_id=@channel AND post_seq=@post
                """, ("closed", !open), ("channel", channel), ("post", post));
            await update.ExecuteNonQueryAsync();
            changed = new(group, await MessageRecipients(db, tx, group));
        }
        else
        {
            if (!open) return (404, [], null);
            changed = await MirrorChannelPost(db, tx, channel, post);
            if (changed is null) return (409, [], null);
        }
        await RecordChannelAction(db, tx, channel, user, open ? "comments_opened" : "comments_closed",
            post.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var recipients = await MessageRecipients(db, tx, channel);
        await tx.CommitAsync();
        return (204, recipients, changed);
    }

    public async Task<bool> MarkChannelCommentsRead(string channel, long post, string user, long sequence, bool fromGroup = false)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var access = Command(db, tx, """
            SELECT t.channel_id,t.post_seq FROM chat_channel_threads t
            JOIN chat_members g ON g.room_id=t.group_id AND g.user_id=@user
            LEFT JOIN chat_members m ON m.room_id=t.channel_id AND m.user_id=@user
            JOIN chat_channel_comments c ON c.group_id=t.group_id AND c.root_seq=t.root_seq AND c.seq=@seq
            WHERE (@fromGroup AND t.group_id=@room AND t.root_seq=@post)
               OR (NOT @fromGroup AND m.user_id IS NOT NULL AND t.channel_id=@room AND t.post_seq=@post)
            FOR SHARE
            """, ("room", channel), ("post", post), ("user", user), ("seq", sequence), ("fromGroup", fromGroup));
        string threadChannel;
        long threadPost;
        await using (var reader = await access.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return false;
            threadChannel = reader.GetString(0);
            threadPost = reader.GetInt64(1);
        }
        await using var update = Command(db, tx, """
            INSERT INTO chat_channel_comment_reads(channel_id,post_seq,user_id,read_to)
            VALUES(@channel,@post,@user,@seq)
            ON DUPLICATE KEY UPDATE read_to=GREATEST(read_to,@seq)
            """, ("channel", threadChannel), ("post", threadPost), ("user", user), ("seq", sequence));
        await update.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return true;
    }

    public async Task<bool> SetDiscussionFollow(string channel, long post, string user, bool following)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var access = Command(db, tx, """
            SELECT t.root_seq FROM chat_channel_threads t
            JOIN chat_members g ON g.room_id=t.group_id AND g.user_id=@user
            JOIN chat_members m ON m.room_id=t.channel_id AND m.user_id=@user
            WHERE t.channel_id=@channel AND t.post_seq=@post FOR SHARE
            """, ("channel", channel), ("post", post), ("user", user));
        if (await access.ExecuteScalarAsync() is null) return false;
        await using var update = Command(db, tx, following
            ? "INSERT IGNORE INTO chat_channel_comment_follows(channel_id,post_seq,user_id) VALUES(@channel,@post,@user)"
            : "DELETE FROM chat_channel_comment_follows WHERE channel_id=@channel AND post_seq=@post AND user_id=@user",
            ("channel", channel), ("post", post), ("user", user));
        await update.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return true;
    }

    public async Task<DiscussionSummary[]> DiscussionSummaries(string channel, string user, long[] posts)
    {
        if (posts.Length == 0) return [];
        await using var db = await Open();
        var markers = posts.Select((_, index) => $"post{index}").ToArray();
        var values = posts.Select((post, index) => (markers[index], (object?)post)).ToList();
        values.Add(("channel", channel));
        values.Add(("user", user));
        await using var query = Command(db, null, $"""
            SELECT t.post_seq,COUNT(msg.seq),
                   COALESCE(SUM(msg.seq>COALESCE(rd.read_to,0) AND msg.sender_id<>@user),0),t.closed
            FROM chat_channel_threads t
            JOIN chat_members g ON g.room_id=t.group_id AND g.user_id=@user
            JOIN chat_members m ON m.room_id=t.channel_id AND m.user_id=@user
            LEFT JOIN chat_channel_comment_reads rd ON rd.channel_id=t.channel_id
              AND rd.post_seq=t.post_seq AND rd.user_id=@user
            LEFT JOIN chat_channel_comments c ON c.group_id=t.group_id AND c.root_seq=t.root_seq
            LEFT JOIN chat_messages msg ON msg.room_id=c.group_id AND msg.seq=c.seq AND NOT msg.deleted
            WHERE t.channel_id=@channel AND t.post_seq IN ({string.Join(",", markers.Select(marker => "@" + marker))})
            GROUP BY t.post_seq,t.closed
            """, values.ToArray());
        var items = new List<DiscussionSummary>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3)));
        return items.ToArray();
    }

    public async Task<ChannelThread?> ReadChannelThread(string channel, long post, string user, long? before, bool fromGroup = false)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var access = Command(db, tx, """
            SELECT t.group_id,t.root_seq,t.closed,
                   (SELECT COUNT(*) FROM chat_channel_comments c
                    JOIN chat_messages msg ON msg.room_id=c.group_id AND msg.seq=c.seq
                    WHERE c.group_id=t.group_id AND c.root_seq=t.root_seq AND NOT msg.deleted),
                   COALESCE(rd.read_to,0),
                   (SELECT COUNT(*) FROM chat_channel_comments c
                    JOIN chat_messages msg ON msg.room_id=c.group_id AND msg.seq=c.seq
                    WHERE c.group_id=t.group_id AND c.root_seq=t.root_seq AND NOT msg.deleted
                      AND msg.sender_id<>@user AND c.seq>COALESCE(rd.read_to,0)), NOT t.closed AND NOT COALESCE(gm.silenced,FALSE),
                   (m.user_id IS NOT NULL AND EXISTS (SELECT 1 FROM chat_channel_comment_follows f
                       WHERE f.channel_id=t.channel_id AND f.post_seq=t.post_seq AND f.user_id=@user))
            FROM chat_channel_threads t JOIN chat_members g ON g.room_id=t.group_id AND g.user_id=@user
            LEFT JOIN chat_members m ON m.room_id=t.channel_id AND m.user_id=@user
            LEFT JOIN chat_group_moderation gm ON gm.group_id=t.group_id AND gm.user_id=@user
            LEFT JOIN chat_channel_comment_reads rd ON rd.channel_id=t.channel_id AND rd.post_seq=t.post_seq AND rd.user_id=@user
            WHERE (@fromGroup AND t.group_id=@room AND t.root_seq=@post)
               OR (NOT @fromGroup AND m.user_id IS NOT NULL AND t.channel_id=@room AND t.post_seq=@post)
            """, ("room", channel), ("post", post), ("user", user), ("fromGroup", fromGroup));
        string group;
        long root, count, readTo, unread;
        bool closed, canComment, following;
        await using (var reader = await access.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return null;
            group = reader.GetString(0);
            root = reader.GetInt64(1);
            closed = reader.GetBoolean(2);
            count = reader.GetInt64(3);
            readTo = reader.GetInt64(4);
            unread = reader.GetInt64(5);
            canComment = reader.GetBoolean(6);
            following = reader.GetBoolean(7);
        }
        await using var query = Command(db, tx, MessageSelect + "\n" + """
            JOIN chat_channel_comments c ON c.group_id=msg.room_id AND c.seq=msg.seq
            WHERE c.group_id=@group AND c.root_seq=@root AND c.seq<@before
            ORDER BY c.seq DESC LIMIT 50
            """, ("group", group), ("root", root), ("before", before ?? long.MaxValue));
        var messages = new List<ChatMessage>();
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) messages.Add(Message(reader, group));
        messages.Reverse();
        return new(group, root, closed, count, messages.ToArray(), readTo, unread, canComment, following);
    }

    public async Task<int> RequestDiscussionMembership(string channel, string user)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var guard = Command(db, tx, """
            SELECT id FROM chat_rooms WHERE id=@channel AND type='channel' FOR SHARE
            """, ("channel", channel));
        if (await guard.ExecuteScalarAsync() is null || !await IsMember(db, tx, channel, user)) return 404;
        await using var query = Command(db, tx, """
            SELECT r.id,r.owner_id FROM chat_channel_discussions link
            JOIN chat_rooms r ON r.id=link.group_id
            WHERE link.channel_id=@channel AND r.owner_id IS NOT NULL FOR SHARE
            """, ("channel", channel));
        string group, owner;
        await using (var reader = await query.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return 404;
            group = reader.GetString(0);
            owner = reader.GetString(1);
        }
        if (await IsMember(db, tx, group, user)) return 204;
        if (await Blocked(db, tx, user, owner)) return 403;
        await using var request = Command(db, tx, """
            INSERT IGNORE INTO chat_discussion_join_requests(group_id,user_id,requested_at)
            VALUES(@group,@user,UTC_TIMESTAMP(6))
            """, ("group", group), ("user", user));
        await request.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return 204;
    }

    public async Task<string[]?> DiscussionJoinRequests(string group, string user)
    {
        await using var db = await Open();
        await using var query = Command(db, null, """
            SELECT a.name FROM chat_rooms r
            JOIN chat_discussion_join_requests j ON j.group_id=r.id
            JOIN chat_accounts a ON a.id=j.user_id
            WHERE r.id=@group AND r.type='group' AND r.owner_id=@user
            ORDER BY j.requested_at,j.user_id LIMIT 100
            """, ("group", group), ("user", user));
        var requests = new List<string>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) requests.Add(reader.GetString(0));
        return requests.ToArray();
    }

    public async Task<DiscussionGroup?> ChannelDiscussion(string channel, string user)
    {
        await using var db = await Open();
        await using var query = Command(db, null, """
            SELECT d.group_id FROM chat_rooms r
            JOIN chat_members m ON m.room_id=r.id AND m.user_id=@user
            LEFT JOIN chat_channel_discussions d ON d.channel_id=r.id
            WHERE r.id=@channel AND r.type='channel'
            """, ("channel", channel), ("user", user));
        return await query.ExecuteScalarAsync() switch
        {
            string group => new(group),
            DBNull => new(null),
            _ => null
        };
    }

    public async Task<(int Status, string[] Recipients, DiscussionRoomChange? Previous)> ChangeChannelDiscussion(string channel, string user, string? group)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await using var channelLock = Command(db, tx, """
            SELECT owner_id FROM chat_rooms WHERE id=@channel AND type='channel' FOR UPDATE
            """, ("channel", channel));
        if (await channelLock.ExecuteScalarAsync() is not string owner || !await IsMember(db, tx, channel, user))
            return (404, [], null);
        if (user != owner && !await HasChannelRight(db, tx, channel, user, ChannelRights.Discussion))
            return (403, [], null);
        await using var link = Command(db, tx, """
            SELECT group_id FROM chat_channel_discussions WHERE channel_id=@channel FOR UPDATE
            """, ("channel", channel));
        var oldGroup = await link.ExecuteScalarAsync() as string;
        foreach (var groupId in new[] { oldGroup, group }.OfType<string>().Distinct().Order(StringComparer.Ordinal))
        {
            await using var groupLock = Command(db, tx, """
                SELECT owner_id FROM chat_rooms WHERE id=@group AND type='group' FOR UPDATE
                """, ("group", groupId));
            var groupOwner = await groupLock.ExecuteScalarAsync();
            if (groupId != group) continue;
            if (groupOwner is null || !await IsMember(db, tx, group, user)) return (404, [], null);
            if (!Equals(groupOwner, user)) return (403, [], null);
            await using var other = Command(db, tx, """
                SELECT channel_id FROM chat_channel_discussions WHERE group_id=@group FOR UPDATE
                """, ("group", group));
            if (await other.ExecuteScalarAsync() is string attached && attached != channel) return (409, [], null);
        }
        if (oldGroup == group) return (204, [], null);
        DiscussionRoomChange? previous = null;
        if (oldGroup is not null)
        {
            previous = await DetachChannelDiscussion(db, tx, channel, oldGroup);
        }
        if (group is not null)
        {
            await using var insert = Command(db, tx, """
                INSERT INTO chat_channel_discussions(channel_id,group_id) VALUES(@channel,@group)
                """, ("channel", channel), ("group", group));
            await insert.ExecuteNonQueryAsync();
        }
        await RecordChannelAction(db, tx, channel, user, group is null ? "discussion_unlinked" : "discussion_linked",
            group ?? oldGroup ?? "");
        var recipients = await MessageRecipients(db, tx, channel);
        await tx.CommitAsync();
        return (204, recipients, previous);
    }

    private static async Task<DiscussionRoomChange> DetachChannelDiscussion(
        DbConnection db, DbTransaction tx, string channel, string group)
    {
        await using var close = Command(db, tx, """
            UPDATE chat_channel_threads SET closed=TRUE WHERE channel_id=@channel AND group_id=@group;
            DELETE FROM chat_discussion_join_requests WHERE group_id=@group;
            DELETE FROM chat_channel_discussions WHERE channel_id=@channel;
            """, ("channel", channel), ("group", group));
        await close.ExecuteNonQueryAsync();
        return new(group, await MessageRecipients(db, tx, group));
    }
}

static class ChatChannelDiscussionEndpoints
{
    public static void Map(RouteGroupBuilder api, ChatStore store)
    {
        api.MapGet("/channels/{room:guid}/comments", async (Guid room, string posts, ClaimsPrincipal user) =>
        {
            var parts = posts.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length > 100) return Results.BadRequest();
            var sequences = new HashSet<long>();
            foreach (var part in parts)
            {
                if (!long.TryParse(part, out long seq) || seq <= 0) return Results.BadRequest();
                sequences.Add(seq);
            }
            return Results.Ok(await store.DiscussionSummaries(room.ToString(), Id(user), sequences.ToArray()));
        });
        api.MapPost("/rooms/{room:guid}/discussions/{root:long}/comments/read", async (Guid room, long root,
            ReadTo input, ClaimsPrincipal user) =>
            input.Sequence <= 0 ? Results.BadRequest() :
            await store.MarkChannelCommentsRead(room.ToString(), root, Id(user), input.Sequence, fromGroup: true)
                ? Results.NoContent() : Results.NotFound());
        api.MapGet("/rooms/{room:guid}/discussions/{root:long}/comments", async (Guid room, long root, long? before, ClaimsPrincipal user) =>
            await store.ReadChannelThread(room.ToString(), root, Id(user), before, fromGroup: true) is { } thread
                ? Results.Ok(thread) : Results.NotFound());
        api.MapGet("/channels/{room:guid}/posts/{post:long}/comments", async (Guid room, long post, long? before, ClaimsPrincipal user) =>
            await store.ReadChannelThread(room.ToString(), post, Id(user), before) is { } thread
                ? Results.Ok(thread) : Results.NotFound());
        api.MapPut("/channels/{room:guid}/posts/{post:long}/comments/follow", async (Guid room, long post,
            FollowDiscussion input, ClaimsPrincipal user) =>
            await store.SetDiscussionFollow(room.ToString(), post, Id(user), input.Following)
                ? Results.NoContent() : Results.NotFound());
        api.MapPost("/channels/{room:guid}/posts/{post:long}/comments/read", async (Guid room, long post,
            ReadTo input, ClaimsPrincipal user) =>
            input.Sequence <= 0 ? Results.BadRequest() :
            await store.MarkChannelCommentsRead(room.ToString(), post, Id(user), input.Sequence)
                ? Results.NoContent() : Results.NotFound());
        api.MapPut("/channels/{room:guid}/posts/{post:long}/discussion", async (Guid room, long post,
            SetPostDiscussion input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            var result = await store.SetChannelPostDiscussion(room.ToString(), post, Id(user), input.Open);
            if (result.Status == 204)
            {
                await hub.Clients.Users(result.Recipients).SendAsync("roomChanged", room.ToString());
                if (result.Group is { } group)
                    await hub.Clients.Users(group.Recipients).SendAsync("roomChanged", group.Room);
            }
            return Results.StatusCode(result.Status);
        });
        api.MapPost("/channels/{room:guid}/discussion/join", async (Guid room, ClaimsPrincipal user) =>
            Results.StatusCode(await store.RequestDiscussionMembership(room.ToString(), Id(user))));
        api.MapGet("/rooms/{room:guid}/discussion-requests", (Guid room, ClaimsPrincipal user) =>
            store.DiscussionJoinRequests(room.ToString(), Id(user)));
        api.MapGet("/channels/{room:guid}/discussion", async (Guid room, ClaimsPrincipal user) =>
            await store.ChannelDiscussion(room.ToString(), Id(user)) is { } link ? Results.Ok(link) : Results.NotFound());
        api.MapPut("/channels/{room:guid}/discussion", async (Guid room, DiscussionGroup input,
            ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            Guid group = default;
            if (input.GroupId is not null && !Guid.TryParse(input.GroupId, out group)) return Results.BadRequest();
            var result = await store.ChangeChannelDiscussion(room.ToString(), Id(user),
                input.GroupId is null ? null : group.ToString());
            if (result.Recipients.Length > 0)
                await hub.Clients.Users(result.Recipients).SendAsync("roomChanged", room.ToString());
            if (result.Previous is { } previous && previous.Recipients.Length > 0)
                await hub.Clients.Users(previous.Recipients).SendAsync("roomChanged", previous.Room);
            return Results.StatusCode(result.Status);
        });
    }

    private static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
