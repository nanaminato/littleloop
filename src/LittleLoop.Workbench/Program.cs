using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LittleLoop.Tools;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5188");
var config = builder.Configuration;
string Required(string key) => config[key] ?? throw new InvalidOperationException("Set " + key);
string token = Required("Workbench:Token");
if (token.Length < 24) throw new InvalidOperationException("Workbench token must be at least 24 characters.");
var policy = JsonSerializer.Deserialize<AgentHostPolicy>(File.ReadAllText(Required("Workbench:Policy")), WorkbenchStore.Json)
    ?? throw new InvalidOperationException("Missing agent policy.");
if (policy.Development is null) throw new InvalidOperationException("A bounded development policy is required.");
policy = policy with { ApprovalMode = "user", OutsideRule = "decline", Commands = [], AttachDocumentation = false, HostToolsOnly = true };
string directory = Path.GetFullPath(config["Workbench:Data"] ?? "data/workbench");
string target = Path.GetFullPath(policy.Project);
if (Path.GetRelativePath(target, directory).Split(Path.DirectorySeparatorChar)[0] != ".." &&
    !Path.IsPathFullyQualified(Path.GetRelativePath(target, directory)))
    throw new InvalidOperationException("Workbench state must be outside the target.");
Directory.CreateDirectory(directory);
using var lease = new FileStream(Path.Combine(directory, "workbench.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
await using var store = new WorkbenchStore(directory, config.GetConnectionString("Workbench"));
await store.Initialize();
using var model = config["Workbench:Model"] is { } modelPath
    ? new HttpSemanticModelClient(JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(modelPath), WorkbenchStore.Json)!) : null;
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(sp => new Workbench(store, policy, directory, model, sp.GetRequiredService<ILogger<Workbench>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<Workbench>());
var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
    { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Path.StartsWithSegments("/api") &&
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + token)))
    { ctx.Response.StatusCode = 401; return; }
    await next();
});

if (config["Workbench:Client"] is { } client)
{
    var provider = new PhysicalFileProvider(Path.GetFullPath(client));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}

app.MapGet("/api/conversation", store.Read);
app.MapGet("/api/sources", (Workbench workbench) => workbench.Sources());
app.MapGet("/api/source", (string path, int start, int end, Workbench workbench) =>
{
    try { return Results.Ok(workbench.Cite(path, start, end)); }
    catch (Exception e) when (e is ArgumentException or IOException) { return Results.BadRequest(e.Message); }
});
app.MapPost("/api/ask", async (Ask input, Workbench workbench, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 4000 || string.IsNullOrWhiteSpace(input.Path))
        return Results.BadRequest("请输入问题并选择源码引用。");
    Citation source;
    try { source = workbench.Cite(input.Path, input.Start, input.End); }
    catch (Exception e) when (e is ArgumentException or IOException) { return Results.BadRequest(e.Message); }
    var question = new Message(Guid.NewGuid(), "user", input.Text, source);
    await store.Update(s => s with { Messages = [.. s.Messages, question] });
    try
    {
        string answer = await workbench.Answer(input, source, cancellationToken);
        var reply = new Message(Guid.NewGuid(), "assistant", answer, source, question.Id);
        await store.Update(s => s with { Messages = [.. s.Messages, reply] });
        return Results.Ok(reply);
    }
    catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
    {
        app.Logger.LogError(e, "Source question {Id} failed", question.Id);
        return Results.Problem("模型未能完成回答，问题已保存。请检查后端日志。", statusCode: 502);
    }
});
app.MapPost("/api/tasks", async (Schedule input) =>
{
    if (string.IsNullOrWhiteSpace(input.Goal) || input.Goal.Length > 6000) return Results.BadRequest("请输入开发目标和验收要求。");
    var job = new Work(Guid.NewGuid(), input.MessageId, input.Goal, "queued");
    bool found = false;
    await store.Update(s =>
    {
        found = s.Messages.Any(m => m.Id == input.MessageId && m.Role == "user" && m.Source is not null);
        return found ? s with { Tasks = [.. s.Tasks, job] } : s;
    });
    return found ? Results.Accepted($"/api/tasks/{job.Id}", job) : Results.NotFound();
});
app.MapGet("/api/tasks/{id:guid}", async (Guid id, Workbench workbench) =>
{
    var job = (await store.Read()).Tasks.SingleOrDefault(t => t.Id == id);
    return job is null ? Results.NotFound() : Results.Ok(new { job, execution = workbench.Details(job) });
});
app.MapPost("/api/tasks/{id:guid}/decision", async (Guid id, Decision input, Workbench workbench) =>
{
    var job = (await store.Read()).Tasks.SingleOrDefault(t => t.Id == id && t.Status == "running");
    if (job is null) return Results.Conflict("任务没有待处理的运行。");
    try { new AgentDelegationHost(workbench.Folder(id)).Answer(input.Nonce, input.Value); return Results.NoContent(); }
    catch (Exception e) when (e is IOException or ArgumentException) { return Results.Conflict("审批已失效或已处理。"); }
});
await app.RunAsync();
