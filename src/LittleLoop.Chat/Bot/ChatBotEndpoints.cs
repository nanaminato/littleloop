using System.Data.Common;
using System.Security.Claims;
using LittleLoop.Tools;
using MySqlConnector;

sealed record BotAccessListChange(string Username, bool Allowed);
sealed record BotAccessDecision(string Decision);
sealed class BotFriendshipRequiredException : Exception;

static class ChatBotEndpoints
{
    public static void Map(RouteGroupBuilder routes, ChatStore store, ChatBot? bot)
    {
        var api = routes.MapGroup("/bot");
        api.MapGet("/capabilities", (ClaimsPrincipal user) => new
        {
            enabled = bot is not null, canOperate = bot?.CanOperate(Id(user)) == true,
            canApprove = false, canManageAccess = bot?.CanManageAccess(Id(user)) == true,
            providers = bot?.Providers ?? [],
        });
        if (bot is null) return;

        api.MapGet("/access", async (ClaimsPrincipal user) =>
        {
            if (!bot.CanManageAccess(Id(user))) return Results.StatusCode(403);
            var rows = await store.GetBotFriendAccess(bot.Robot(null).Id);
            return Results.Ok(new { entries = rows.Select(row => new
                { id = row.UserId, row.Username, displayName = row.Username, allowed = row.Listed, row.State, row.RoomId }) });
        });

        api.MapPost("/access/allowlist", async (ClaimsPrincipal user, BotAccessListChange input) =>
        {
            if (!bot.CanManageAccess(Id(user))) return Results.StatusCode(403);
            var name = input.Username?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) return Results.BadRequest();
            var change = await store.SetBotFriendAllowlist(bot.Robot(null).Id, name, input.Allowed);
            if (change is null) return Results.NotFound();
            await bot.NotifyFriendAccess(change);
            return Results.NoContent();
        });

        api.MapPost("/access/{id:guid}/decision", async (ClaimsPrincipal user, Guid id, BotAccessDecision input) =>
        {
            if (!bot.CanManageAccess(Id(user))) return Results.StatusCode(403);
            if (input.Decision is not ("accept" or "reject" or "revoke")) return Results.BadRequest();
            string robot = bot.Robot(null).Id;
            var change = input.Decision == "revoke" ? await store.RevokeBotFriend(robot, id.ToString()) :
                await store.DecideBotFriend(robot, id.ToString(), input.Decision == "accept");
            if (change is null) return Results.Conflict("申请或访问授权状态已变化。");
            await bot.NotifyFriendAccess(change);
            return Results.NoContent();
        });

        api.MapPost("/direct", async (ClaimsPrincipal user, string? provider) =>
        {
            if (!bot.CanUse(provider)) return Results.BadRequest("未知机器人。");
            var robot = bot.Robot(provider);
            var access = provider is null or ChatStore.BotName
                ? await store.ReadBotFriendAccess(Id(user), robot.Id) : BotFriendAccessState.Unmanaged;
            if (access != BotFriendAccessState.Active &&
                (access != BotFriendAccessState.Unmanaged || !bot.CanOperate(Id(user)))) return Results.StatusCode(403);
            var room = await store.CreateRoom(new(Id(user), user.Identity!.Name!, ""), new("direct", "", [robot.Name]));
            if (access == BotFriendAccessState.Unmanaged)
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
            return Results.Ok(new { current.Installed, current.Type, providers,
                canOperate = current.Installed && await bot.CanOperateRoom(Id(user), room.ToString()) });
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
            if (!await bot.CanOperateRoom(Id(user), room.ToString())) return Results.StatusCode(403);
            var current = await store.BotRoom(room.ToString(), Id(user));
            return !current.Member || !current.Installed ? Results.NotFound() : Results.Ok(bot.Sources());
        });

        api.MapPost("/rooms/{room:guid}/ask", async (Guid room, BotAsk ask, ClaimsPrincipal user) =>
        {
            if (!await bot.CanOperateRoom(Id(user), room.ToString(), ask.Provider)) return Results.StatusCode(403);
            if (ChatStore.IsProjectTaskOnly(ask.Provider)) return Results.BadRequest("请使用项目执行器的安排任务入口。");
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
            if (!await bot.CanOperateRoom(Id(user), room.ToString(), input.Provider)) return Results.StatusCode(403);
            if (input.ClientId == Guid.Empty || input.Sequence <= 0 || string.IsNullOrWhiteSpace(input.Goal) || input.Goal.Length > 6000)
                return Results.BadRequest("请输入目标、验收要求和来源消息。");
            var current = await store.BotRoom(room.ToString(), Id(user));
            if (!current.Member || !current.Installed) return Results.NotFound();
            if (!await AllowedProvider(room, input.Provider)) return Results.BadRequest("机器人未安装到此会话。");
            var source = await store.FindMessage(room.ToString(), input.Sequence, Id(user));
            if (source is null) return Results.NotFound();
            return await Queue(room, user, input.ClientId, input.Sequence, "task",
                new(input.Goal, source.Text[..Math.Min(source.Text.Length, 12000)],
                    ChatStore.IsProjectTaskOnly(input.Provider) ? null : source.Card?.Source, input.Provider));
        });

        api.MapGet("/work/{id:guid}", async (Guid id, ClaimsPrincipal user) =>
        {
            var job = await store.ReadBotWork(id, Id(user));
            if (job is null) return Results.NotFound();
            bool seeEvidence = bot.CanReadHistoricalEvidence(Id(user)) || job.Owner == Id(user);
            object? execution = job.Input.Loop is null && job.Kind != "question" && seeEvidence && (job.Kind != "legacy" || bot.HasLegacyEvidence)
                ? AgentConversationSupport.Details(bot.Folder(job), false) : null;
            var events = job.Input.Loop is not null && seeEvidence ? await store.GoalObservations(job.Id) : null;
            var goal = !seeEvidence && job.Goal is { } shared ? shared with { Obligations = null, PendingRequirementSources = null } : job.Goal;
            return Results.Ok(new { job.Id, job.Status, job.Error, canApprove = false, execution, events,
                goal,
                wakeAtUnixMilliseconds = job.Input.Loop?.Wake?.AtUnixMilliseconds,
                provider = job.Input.Provider ?? ChatStore.BotName,
                output = seeEvidence && job.Output is not null ? AgentConversationSupport.ReportText(job.Output) : null });
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
            catch (BotFriendshipRequiredException) { return Results.StatusCode(403); }
            catch (DbException e) when (ChatStore.IsDuplicate(e)) { return Results.Conflict("任务 ID 已使用。"); }
        }

        Task<bool> AllowedProvider(Guid room, string? provider) => bot.CanUse(provider)
            ? store.RobotIsMember(room.ToString(), bot.Robot(provider).Id) : Task.FromResult(false);
    }

    private static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
