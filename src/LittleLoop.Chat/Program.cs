using LittleLoop.Core;
using LittleLoop.Tools;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 6 * 1024 * 1024);
if (builder.Configuration["Chat:Url"] is null) builder.WebHost.UseUrls("http://0.0.0.0:5190");
else builder.WebHost.UseUrls(builder.Configuration["Chat:Url"]!);
var config = builder.Configuration;
string Required(string key) => config[key] ?? throw new InvalidOperationException("Set " + key);
string invite = Required("Chat:Invite");
if (invite.Length < 24) throw new InvalidOperationException("Chat invite must be at least 24 characters.");
var keyDirectory = Directory.CreateDirectory(Path.GetFullPath(Required("Chat:Keys")));
builder.Services.AddDataProtection().SetApplicationName("LittleLoop.Chat").PersistKeysToFileSystem(keyDirectory);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "LittleLoop.Chat";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async ctx =>
    {
        var store = ctx.HttpContext.RequestServices.GetRequiredService<ChatStore>();
        var name = await store.ActiveDeviceName(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),
            ctx.Principal?.FindFirstValue(ChatSettingsEndpoints.DeviceClaim));
        if (name is null) ctx.RejectPrincipal();
        else if (ctx.Principal!.Identity!.Name != name)
        {
            var identity = new ClaimsIdentity(ctx.Principal.Claims.Where(c => c.Type != ClaimTypes.Name), CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new(ClaimTypes.Name, name));
            ctx.ReplacePrincipal(new ClaimsPrincipal(identity));
        }
    };
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ChatConnections>();
builder.Services.AddSingleton<PasswordHasher<Account>>();
var fcmProject = config["ChatFcm:ProjectId"]?.Trim();
if (string.IsNullOrEmpty(fcmProject)) fcmProject = null;
var store = new ChatStore(builder.Configuration.GetConnectionString("Chat") ?? throw new InvalidOperationException("Set ConnectionStrings__Chat"),
    fcmProject is not null, config["ChatDatabase:Provider"] ?? "MySql", requireObligations: true);
await store.Initialize();
await store.InitializeChannelAdministration();
await store.InitializeGroupModeration();
await store.InitializeChannelDiscussions();
builder.Services.AddSingleton(store);
var pushIdentity = ChatPush.LoadIdentity(config["ChatPush:KeyFile"] ?? Path.Combine(keyDirectory.FullName, "web-push.json"));
builder.Services.AddHostedService(sp => new ChatPush(store, pushIdentity,
    config["ChatPush:Subject"] ?? "mailto:chat-admin@example.invalid", sp.GetRequiredService<ILogger<ChatPush>>()));
if (config["ChatBot:Policy"] is not null)
{
    builder.Services.AddSingleton(sp =>
    {
        string path = config["ChatBot:DecisionModel"] ?? config["ChatBot:ConversationModel"] ??
            throw new InvalidOperationException("Set ChatBot__DecisionModel to a semantic model configuration.");
        return ChatStore.FromJson<SemanticModelConfig>(File.ReadAllText(path));
    });
    builder.Services.AddSingleton<ISemanticModelClient>(sp => new HttpSemanticModelClient(sp.GetRequiredService<SemanticModelConfig>()));
    builder.Services.AddSingleton<IBotDecisionEngine>(sp => new LocalBotCognition(new ModelBotDecisionEngine(
        sp.GetRequiredService<ISemanticModelClient>(), sp.GetRequiredService<SemanticModelConfig>().MaxInputCharacters,
        maxFormatAttempts: 3)));
    builder.Services.AddSingleton<ChatBot>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatBot>());
}
if (config.GetValue<bool>("ChatCommitments:Enabled"))
{
    builder.Services.AddSingleton<ChatCommitments>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatCommitments>());
}
if (fcmProject is not null)
    builder.Services.AddHostedService(sp => new ChatFcm(store, fcmProject,
        sp.GetRequiredService<ILogger<ChatFcm>>()));

var app = builder.Build();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/chat")) ctx.Response.Headers.CacheControl = "private, no-store";
    if (ctx.Request.Path.StartsWithSegments("/hubs/chat") && ctx.Request.Headers.TryGetValue("Origin", out var origin) &&
        !string.Equals(origin.ToString(), $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) &&
        ctx.Request.Path.StartsWithSegments("/api/chat") &&
        ctx.Request.Headers["X-LittleLoop-Chat"] != "1")
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
var client = new PhysicalFileProvider(Path.GetFullPath(Required("Chat:Client")));
app.UseStaticFiles(new StaticFileOptions { FileProvider = client });
app.MapGet("/", () => Results.Redirect("/chat"));
app.MapGet("/chat", () => Results.File(client.GetFileInfo("index.html").PhysicalPath!, "text/html; charset=utf-8"));
app.MapHub<ChatHub>("/hubs/chat", options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization();

var api = app.MapGroup("/api/chat");
api.MapPost("/register", async (Login body, PasswordHasher<Account> hash) =>
{
    string name = Normalize(body.Name);
    if (!ValidName(name) || ChatStore.IsRobot(name) || body.Password is not { Length: >= 10 and <= 128 }) return Results.BadRequest("账号为 3–32 位英文字母、数字或下划线，密码至少 10 位，且不可使用保留账号。");
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(body.Invite ?? ""), Encoding.UTF8.GetBytes(invite)))
        return Results.StatusCode(403);
    var account = new Account(Guid.NewGuid().ToString(), name, "");
    return await store.Register(account with { Password = hash.HashPassword(account, body.Password) })
        ? Results.NoContent() : Results.Conflict("账号已存在。");
});
string? automationSecret = config["ChatAutomation:LittleLoopSecret"];
if (automationSecret is not null && automationSecret.Length is < 32 or > 128)
    throw new InvalidOperationException("ChatAutomation:LittleLoopSecret must contain 32–128 characters.");
if (automationSecret is not null) await store.GetRobot(ChatStore.BotName);
api.MapPost("/login", async (Login body, PasswordHasher<Account> hash, HttpContext ctx) =>
{
    string name = Normalize(body.Name);
    if (!ValidName(name) || body.Password is not { Length: >= 10 and <= 128 }) return Results.Unauthorized();
    var account = await store.Find(name);
    if (account is null) return Results.Unauthorized();
    if (ChatStore.IsRobot(name))
    {
        if (name != ChatStore.BotName || automationSecret is null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(automationSecret), Encoding.UTF8.GetBytes(body.Password)))
            return Results.Unauthorized();
    }
    else if (hash.VerifyHashedPassword(account, account.Password, body.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();
    string? device = await store.AddDevice(account, ctx.Request.Headers.UserAgent.ToString());
    if (device is null) return Results.Unauthorized();
    var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, account.Id), new(ClaimTypes.Name, account.Name),
        new(ChatSettingsEndpoints.DeviceClaim, device)], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { account.Name });
});
var secured = api.MapGroup("").RequireAuthorization();
secured.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { name = user.Identity!.Name }));
secured.MapPost("/logout", async (HttpContext ctx, ChatConnections sockets) =>
{
    sockets.Abort(await store.RevokeDevices(Id(ctx.User), ChatSettingsEndpoints.Device(ctx.User)));
    await ctx.SignOutAsync();
    return Results.NoContent();
});
secured.MapGet("/rooms", (ClaimsPrincipal user) => store.Rooms(Id(user)));
secured.MapGet("/rooms/{room:guid}/members", async (Guid room, ClaimsPrincipal user) =>
{
    var members = await store.Members(room.ToString(), Id(user));
    return members is null ? Results.NotFound() : Results.Ok(members);
});
secured.MapPost("/rooms", async (OpenRoom request, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
{
    if (request.Type is not ("direct" or "group") || request.Members is not { Length: <= 30 }
        || (request.Type == "direct" && request.Members.Length == 0)
        || request.Members.Any(m => !ValidName(Normalize(m))) || request.Title?.Length > 100
        || (request.Type == "group" && string.IsNullOrWhiteSpace(request.Title))) return Results.BadRequest("请填写会话类型、名称和已注册成员。");
    try
    {
        if (request.Type == "direct" && request.Members.Any(name => Normalize(name) == ChatStore.BotName) &&
            app.Services.GetService<ChatBot>() is { } bot && Id(user) != bot.Robot(null).Id)
        {
            var access = await store.ReadBotFriendAccess(Id(user), bot.Robot(null).Id);
            if (access != BotFriendAccessState.Active &&
                (access != BotFriendAccessState.Unmanaged || !bot.CanOperate(Id(user))))
                return Results.Json(new { code = "bot_friendship_required" }, statusCode: 403);
        }
        var room = await store.CreateRoom(new(Id(user), user.Identity!.Name!, ""), request);
        await Push(hub, room.Members, room.Id);
        return Results.Ok(new { room.Id });
    }
    catch (ArgumentException e) { return Results.BadRequest(e.Message); }
    catch (DirectMessageBlockedException) { return Results.Json(new { code = "communication_unavailable" }, statusCode: 403); }
});
secured.MapGet("/rooms/{room:guid}/messages", async (Guid room, long? after, long? before, ClaimsPrincipal user) =>
{
    if ((after.HasValue && before.HasValue) || after < 0 || before <= 0) return Results.BadRequest();
    var messages = await store.History(Id(user), room.ToString(), after, before);
    return messages is null ? Results.NotFound() : Results.Ok(messages);
});
secured.MapPost("/rooms/{room:guid}/messages", async (Guid room, PostMessage request, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
{
    if (!Guid.TryParse(request.ClientId, out var clientId) || request.Text is not { Length: <= 4000 }
        || (string.IsNullOrWhiteSpace(request.Text) && request.AttachmentId is null) || request.ReplyTo <= 0
        || (request.AttachmentId is not null && !Guid.TryParseExact(request.AttachmentId, "D", out _)))
        return Results.BadRequest("消息内容、附件或引用无效。");
    try
    {
        var chatBot = app.Services.GetService<ChatBot>();
        var result = await store.Send(Id(user), user.Identity!.Name!, room.ToString(), request with { ClientId = clientId.ToString() },
            goalRobot: chatBot?.Robot(null).Id, legacyBotOperator: chatBot?.CanOperate(Id(user)) == true);
        if (result is null) return Results.NotFound();
        if (result.IsNew) await Push(hub, result.Recipients, room.ToString());
        if (app.Services.GetService<ChatCommitments>() is { } commitments)
            await commitments.Handle(Id(user), result.Message);
        if (result.Discussion is { } discussion)
            await Push(hub, discussion.Recipients, discussion.Room);
        return Results.Ok(result.Message);
    }
    catch (ArgumentException e) { return Results.BadRequest(e.Message); }
    catch (InvalidOperationException e) { return Results.Conflict(e.Message); }
    catch (DiscussionClosedException) { return Results.Json(new { code = "discussion_closed" }, statusCode: 403); }
    catch (GroupSilencedException) { return Results.Json(new { code = "group_silenced" }, statusCode: 403); }
    catch (ChannelWriteDeniedException) { return Results.StatusCode(403); }
    catch (BotFriendshipRequiredException) { return Results.Json(new { code = "bot_friendship_required" }, statusCode: 403); }
    catch (DirectMessageBlockedException) { return Results.Json(new { code = "communication_unavailable" }, statusCode: 403); }
});
secured.MapPost("/rooms/{room:guid}/read", async (Guid room, ReadTo request, ClaimsPrincipal user, IHubContext<ChatHub> hub) =>
{
    if (request.Sequence < 0) return Results.BadRequest();
    if (!await store.MarkRead(Id(user), room.ToString(), request.Sequence)) return Results.NotFound();
    await hub.Clients.User(Id(user)).SendAsync("readChanged", room.ToString());
    return Results.NoContent();
});
ChatFeatureEndpoints.Map(secured, store, app.Services.GetService<ChatBot>());
ChatSpaceEndpoints.Map(secured, store);
ChatChannelAdminEndpoints.Map(secured, store);
ChatChannelDiscussionEndpoints.Map(secured, store);
ChatSettingsEndpoints.Map(secured, store);
IdentityEndpoints.Map(secured, store);
ChatPush.Map(secured, store, pushIdentity);
ChatFcm.Map(secured, store, fcmProject);
ChatSocialEndpoints.Map(secured, store, app.Services.GetService<ChatBot>());
ChatBotEndpoints.Map(secured, store, app.Services.GetService<ChatBot>());
ChatCommitments.Map(secured, app.Services.GetService<ChatCommitments>());
await app.RunAsync();

static Task Push(IHubContext<ChatHub> hub, string[] users, string room) => hub.Clients.Users(users).SendAsync("roomChanged", room);
static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
static string Normalize(string? name) => name?.Trim().ToLowerInvariant() ?? "";
static bool ValidName(string name) => Regex.IsMatch(name, @"\A[a-z0-9_]{3,32}\z", RegexOptions.CultureInvariant);
