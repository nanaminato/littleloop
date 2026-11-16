using System.Data.Common;
using System.Security.Claims;
using LittleLoop.Tools;
using MySqlConnector;

static class ChatBotEndpoints
{
    public static void Map(RouteGroupBuilder routes, ChatStore store, ChatBot? bot)
    {
        var api = routes.MapGroup("/bot");
        api.MapGet("/capabilities", (ClaimsPrincipal user) => new
        {
            enabled = bot is not null, canOperate = bot?.CanOperate(Id(user)) == true,
            canApprove = bot?.CanApprove(Id(user)) == true, providers = bot?.Providers ?? [],
        });
        if (bot is null) return;

        api.MapPost("/direct", async (ClaimsPrincipal user, string? provider) =>
        {
            if (!bot.CanOperate(Id(user))) return Results.StatusCode(403);
            if (!bot.CanUse(provider)) return Results.BadRequest("未知机器人。");
            var robot = bot.Robot(provider);
            var room = await store.CreateRoom(new(Id(user), user.Identity!.Name!, ""), new("direct", "", [robot.Name]));
            await store.InstallRobot(room.Id, Id(user), [robot.Id], true);
            await bot.Notify(room.Members, room.Id);
            return Results.Ok(new { room.Id });
        });

        api.MapGet("/rooms/{room:guid}", async (Guid room, ClaimsPrincipal user) =>
        {
            var current = await store.BotRoom(room.ToString(), Id(user));
            if (!current.Member) return Results.NotFound();
            var providers = new List<string>();
            if (current.Installed)
                foreach (var name in bot.Providers)
                    if (await store.RobotIsMember(room.ToString(), bot.Robot(name).Id)) providers.Add(name);
            return Results.Ok(new { current.Installed, current.Type, providers });
        });

        api.MapPost("/rooms/{room:guid}/install", async (Guid room, BotInstall input, ClaimsPrincipal user) =>
        {
            if (!bot.CanOperate(Id(user))) return Results.StatusCode(403);
            var recipients = await store.InstallRobot(room.ToString(), Id(user), bot.Providers.Select(name => bot.Robot(name).Id).ToArray(), input.Enabled);
            if (recipients is null) return Results.NotFound();
            await bot.Notify(recipients, room.ToString());
            return Results.NoContent();
        });

        api.MapGet("/rooms/{room:guid}/sources", async (Guid room, ClaimsPrincipal user) =>
        {
            if (!bot.CanOperate(Id(user))) return Results.StatusCode(403);
            var current = await store.BotRoom(room.ToString(), Id(user));
            return !current.Member || !current.Installed ? Results.NotFound() : Results.Ok(bot.Sources());
        });

        api.MapPost("/rooms/{room:guid}/ask", async (Guid room, BotAsk ask, ClaimsPrincipal user) =>
        {
            if (!bot.CanOperate(Id(user))) return Results.StatusCode(403);
            if (ask.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(ask.Text) || ask.Text.Length > 4000 || string.IsNullOrWhiteSpace(ask.Path))
                return Results.BadRequest("请输入问题和引用。");
            var current = await store.BotRoom(room.ToString(), Id(user));
            if (!current.Member || !current.Installed) return Results.NotFound();
            if (!await AllowedProvider(room, ask.Provider)) return Results.BadRequest("机器人未安装到此会话。");
            try
            {
                var cite = bot.Cite(ask.Path, ask.Start, ask.End);
                return await Queue(room, user, ask.ClientId, ask.ReplyTo, "question", new(ask.Text, Source: cite, Provider: ask.Provider));
            }
            catch (Exception e) when (e is ArgumentException or IOException) { return Results.BadRequest(e.Message); }
        });

        api.MapPost("/rooms/{room:guid}/schedule", async (Guid room, BotSchedule input, ClaimsPrincipal user) =>
        {
            if (!bot.CanOperate(Id(user))) return Results.StatusCode(403);
            if (input.ClientId == Guid.Empty || input.Sequence <= 0 || string.IsNullOrWhiteSpace(input.Goal) || input.Goal.Length > 6000)
                return Results.BadRequest("请输入目标、验收要求和来源消息。");
            var current = await store.BotRoom(room.ToString(), Id(user));
            if (!current.Member || !current.Installed) return Results.NotFound();
            if (!await AllowedProvider(room, input.Provider)) return Results.BadRequest("机器人未安装到此会话。");
            var source = await store.FindMessage(room.ToString(), input.Sequence, Id(user));
            if (source is null) return Results.NotFound();
            return await Queue(room, user, input.ClientId, input.Sequence, "task",
                new(input.Goal, source.Text[..Math.Min(source.Text.Length, 12000)], source.Card?.Source, input.Provider));
        });

        api.MapGet("/work/{id:guid}", async (Guid id, ClaimsPrincipal user) =>
        {
            var job = await store.ReadBotWork(id, Id(user));
            if (job is null) return Results.NotFound();
            bool canApprove = job.Kind == "task" && bot.SupportsApproval(job.Input.Provider) && bot.CanApprove(Id(user));
            bool seeEvidence = bot.CanApprove(Id(user)) || job.Owner == Id(user);
            object? execution = job.Kind != "question" && seeEvidence && (job.Kind != "legacy" || bot.HasLegacyEvidence)
                ? AgentConversationSupport.Details(bot.Folder(job), canApprove && job.Status == "running") : null;
            return Results.Ok(new { job.Id, job.Status, job.Error, canApprove, execution,
                provider = job.Input.Provider ?? ChatStore.BotName, output = seeEvidence ? job.Output : null });
        });

        api.MapPost("/work/{id:guid}/decision", async (Guid id, BotDecision input, ClaimsPrincipal user) =>
        {
            var job = await store.ReadBotWork(id, Id(user));
            if (job is null) return Results.NotFound();
            if (!bot.CanApprove(Id(user))) return Results.StatusCode(403);
            if (job.Kind != "task" || !bot.SupportsApproval(job.Input.Provider) || job.Status != "running")
                return Results.Conflict("任务没有待处理的授权。");
            if (!Guid.TryParseExact(input.Nonce, "N", out _) || input.Value is not ("accept" or "decline")) return Results.BadRequest();
            try
            {
                new AgentDelegationHost(bot.Folder(job)).Answer(input.Nonce, input.Value, Id(user));
                return Results.NoContent();
            }
            catch (Exception e) when (e is IOException or ArgumentException) { return Results.Conflict("审批已失效或已处理。"); }
        });

        async Task<IResult> Queue(Guid room, ClaimsPrincipal user, Guid client, long? anchor, string kind, BotInput input)
        {
            try
            {
                var message = await bot.Queue(room.ToString(), Id(user), user.Identity!.Name!, client, anchor, kind, input);
                return message is null ? Results.NotFound() : Results.Accepted(value: message);
            }
            catch (ArgumentException e) { return Results.BadRequest(e.Message); }
            catch (InvalidOperationException e) { return Results.Conflict(e.Message); }
            catch (DbException e) when (ChatStore.IsDuplicate(e)) { return Results.Conflict("任务 ID 已使用。"); }
        }

        Task<bool> AllowedProvider(Guid room, string? provider) => bot.CanUse(provider)
            ? store.RobotIsMember(room.ToString(), bot.Robot(provider).Id) : Task.FromResult(false);
    }

    private static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
