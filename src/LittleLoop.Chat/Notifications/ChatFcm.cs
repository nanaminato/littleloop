using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using MySqlConnector;

sealed record FcmSubscription(string Token, string Project, bool InstallationId = false);

sealed class ChatFcm(ChatStore store, string project, ILogger<ChatFcm> log) : BackgroundService
{
    private const string CloudMessagingScope = "https://www.googleapis.com/auth/firebase.messaging";

    public static async Task<FcmDelivery?> Resolve(ChatStore store, long id, string? device = null)
    {
        var notice = await store.ReadFcm(id, device);
        if (notice?.Work is not null) return null; // Retired project approval notifications.
        return notice;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var endpoint = new Uri("https://fcm.googleapis.com/v1/projects/" + Uri.EscapeDataString(project) + "/messages:send");
        using var transport = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        GoogleCredential? credential = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token))
        {
            long? id = null;
            try
            {
                id = await store.ClaimFcm();
                if (!id.HasValue) continue;
                var notice = await Resolve(store, id.Value);
                if (notice is null) { await store.CompleteFcm(id.Value, false); continue; }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                credential ??= (await GoogleCredential.GetApplicationDefaultAsync(timeout.Token)).CreateScoped(CloudMessagingScope);
                string bearer = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: timeout.Token);
                var message = new Dictionary<string, object>
                {
                    [notice.InstallationId ? "fid" : "token"] = notice.Token,
                    ["data"] = new { littleloop = notice.Id.ToString(CultureInfo.InvariantCulture) },
                    ["android"] = new { priority = "HIGH", ttl = "900s" },
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(new { message }) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using var response = await transport.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    await store.CompleteFcm(id.Value, true);
                    log.LogInformation("FCM notice {NoticeId} accepted by gateway.", id);
                    continue;
                }
                string body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (FcmError(body) == "UNREGISTERED")
                {
                    await store.RemoveFcm(notice.Device, notice.Token);
                }
                else if (response.StatusCode is HttpStatusCode.BadRequest)
                {
                    await store.CompleteFcm(id.Value, false);
                }
                log.LogWarning("FCM notice {NoticeId} returned HTTP {Status}.", id, (int)response.StatusCode);
            }
            catch (DbException e) when (!token.IsCancellationRequested)
            { log.LogWarning("FCM database temporarily unavailable, database error {Error}.", e.ErrorCode); }
            catch (Exception e) when (!token.IsCancellationRequested &&
                e is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException or TokenResponseException)
            { log.LogWarning("FCM notice {NoticeId} awaits credentials or transport.", id); }
        }
    }

    private static string? FcmError(string body)
    {
        try
        {
            using var result = JsonDocument.Parse(body);
            if (!result.RootElement.TryGetProperty("error", out var error) ||
                !error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array) return null;
            foreach (var detail in details.EnumerateArray())
                if (detail.TryGetProperty("errorCode", out var code)) return code.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public static void Map(RouteGroupBuilder api, ChatStore store, string? project)
    {
        api.MapGet("/settings/fcm", () => Results.Ok(new { project }));
        api.MapPost("/settings/fcm", async (FcmSubscription input, ClaimsPrincipal user) =>
        {
            if (project is null) return Results.NotFound();
            if (input.Project != project || input.Token is not { Length: >= 20 and <= 4096 } ||
                input.Token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (':' or '_' or '-')) ||
                (input.InstallationId && (input.Token.Length != 22 || input.Token.Contains(':'))))
                return Results.BadRequest();
            return await store.SaveFcm(ChatSettingsEndpoints.Device(user), input.Token, input.InstallationId)
                ? Results.NoContent() : Results.Conflict();
        });
        api.MapPost("/settings/fcm/remove", async (ClaimsPrincipal user) =>
        {
            await store.RemoveFcm(ChatSettingsEndpoints.Device(user));
            return Results.NoContent();
        });
        api.MapGet("/settings/fcm/notices/{id:long}", async (long id, ClaimsPrincipal user) =>
        {
            if (project is null || id <= 0) return Results.NotFound();
            var notice = await Resolve(store, id, ChatSettingsEndpoints.Device(user));
            return notice is null ? Results.NotFound() : Results.Ok(new {
                notice.Room, notice.Sequence, notice.Title, notice.Body, notice.Sound, notice.Work, notice.CommentChannel, notice.CommentPost
            });
        });
    }
}
