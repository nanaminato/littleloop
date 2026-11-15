using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

static class ChatFeatureEndpoints
{
    private const int MaxFileSize = 5 * 1024 * 1024;

    public static void Map(RouteGroupBuilder api, ChatStore store)
    {
        api.MapPost("/saved", async (ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            var room = await store.CreateRoom(new(Id(user), user.Identity!.Name!, ""), new("saved", "个人收藏", []));
            await Notify(hub, room.Members, room.Id);
            return Results.Ok(new { room.Id });
        });

        api.MapGet("/search", async (string q, Guid? room, int? offset, ClaimsPrincipal user) =>
        {
            string term = q.Trim();
            return term.Length is < 1 or > 100
                ? Results.BadRequest("请输入 1–100 个字符。")
                : offset < 0 ? Results.BadRequest("搜索偏移量无效。")
                : Results.Ok(await store.Search(Id(user), term, room?.ToString(), offset ?? 0));
        });

        api.MapPost("/attachments", async (HttpRequest request, ClaimsPrincipal user) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest("请选择一个文件。");
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
            Guid? uploadId = null;
            if (form.TryGetValue("uploadId", out var supplied))
            {
                if (supplied.Count != 1 || !Guid.TryParseExact(supplied[0], "D", out var id) || id == Guid.Empty)
                    return Results.BadRequest("上传标识无效。");
                uploadId = id;
            }
            if (form.Files.Count != 1 || form.Files[0].Length is <= 0 or > MaxFileSize)
                return Results.BadRequest("每条消息可添加一个不超过 5 MB 的非空文件。");
            var upload = form.Files[0];
            string name = Path.GetFileName(upload.FileName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name.Any(char.IsControl))
                return Results.BadRequest("文件名无效。");
            using var stream = new MemoryStream((int)upload.Length);
            await upload.CopyToAsync(stream, request.HttpContext.RequestAborted);
            byte[] bytes = stream.ToArray();
            var saved = await store.Upload(Id(user), new(name, Mime(bytes), bytes), uploadId);
            return saved is null ? Results.Conflict("上传标识已被使用。") : Results.Ok(saved);
        });

        api.MapGet("/rooms/{room:guid}/messages/{seq:long}/attachment",
            async (Guid room, long seq, bool? download, ClaimsPrincipal user, HttpResponse response) =>
            {
                var file = await store.Download(Id(user), room.ToString(), seq);
                if (file is null) return Results.NotFound();
                response.Headers.CacheControl = "private, no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.File(file.Content, file.Mime, download == true || !file.Mime.StartsWith("image/") ? file.Name : null);
            });

        api.MapPost("/rooms/{room:guid}/messages/{seq:long}/edit",
            (Guid room, long seq, EditMessage input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
                input.Text is not { Length: <= 4000 }
                    ? Task.FromResult<IResult>(Results.BadRequest("消息最多 4000 个字符。"))
                    : Change(store, hub, user, room, seq, input.Text));

        api.MapPost("/rooms/{room:guid}/messages/{seq:long}/delete",
            (Guid room, long seq, ClaimsPrincipal user, IHubContext<ChatHub> hub) => Change(store, hub, user, room, seq, null));

        api.MapPost("/rooms/{room:guid}/messages/{seq:long}/pin",
            async (Guid room, long seq, MessagePin input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
            {
                if (seq <= 0) return Results.BadRequest();
                try
                {
                    var recipients = await store.SetPinnedMessage(Id(user), room.ToString(), seq, input.Value);
                    if (recipients is null) return Results.NotFound();
                    if (recipients.Length > 0) await Notify(hub, recipients, room.ToString());
                    return Results.NoContent();
                }
                catch (ChannelWriteDeniedException) { return Results.StatusCode(403); }
            });

        api.MapPost("/rooms/{room:guid}/forward", async (Guid room, ForwardMessage input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            if (input.ClientId == Guid.Empty || input.Sequence <= 0) return Results.BadRequest();
            try
            {
                var result = await store.Send(Id(user), user.Identity!.Name!, room.ToString(), new(input.ClientId.ToString(), "", null), input);
                if (result is null) return Results.NotFound();
                if (result.IsNew) await Notify(hub, result.Recipients, room.ToString());
                if (result.Discussion is { } discussion)
                    await Notify(hub, discussion.Recipients, discussion.Room);
                return Results.Ok(result.Message);
            }
            catch (ArgumentException e) { return Results.BadRequest(e.Message); }
            catch (InvalidOperationException e) { return Results.Conflict(e.Message); }
            catch (DiscussionClosedException) { return Results.Json(new { code = "discussion_closed" }, statusCode: 403); }
            catch (GroupSilencedException) { return Results.Json(new { code = "group_silenced" }, statusCode: 403); }
            catch (ChannelWriteDeniedException) { return Results.StatusCode(403); }
            catch (DirectMessageBlockedException) { return Results.Json(new { code = "communication_unavailable" }, statusCode: 403); }
        });

        api.MapPost("/rooms/{room:guid}/preference", async (Guid room, RoomPreference input, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
        {
            try
            {
                if (!await store.SetPreference(Id(user), room.ToString(), input)) return Results.NotFound();
                await hub.Clients.User(Id(user)).SendAsync("readChanged", room.ToString());
                return Results.NoContent();
            }
            catch (ArgumentException e) { return Results.BadRequest(e.Message); }
        });
    }

    private static async Task<IResult> Change(ChatStore store, IHubContext<ChatHub> hub, ClaimsPrincipal user, Guid room, long seq, string? text)
    {
        try
        {
            var result = await store.ChangeMessage(Id(user), room.ToString(), seq, text);
            if (result is null) return Results.NotFound();
            if (result.Recipients.Length > 0) await Notify(hub, result.Recipients, room.ToString());
            if (result.Discussion is { } discussion)
                await Notify(hub, discussion.Recipients, discussion.Room);
            return Results.NoContent();
        }
        catch (ArgumentException e) { return Results.BadRequest(e.Message); }
    }

    private static Task Notify(IHubContext<ChatHub> hub, string[] users, string room) => hub.Clients.Users(users).SendAsync("roomChanged", room);
    private static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    internal static string Mime(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return "application/octet-stream";
    }
}
