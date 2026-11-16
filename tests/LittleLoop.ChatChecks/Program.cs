using LittleLoop.Core;
using MySqlConnector;
using LittleLoop.Tools;

if (args is ["--verify-fixture"])
{
    bool passed = File.ReadAllText("code.txt").Trim() == "fixed";
    Console.WriteLine(passed ? "PASS actual fixture test" : "FAIL code.txt must contain fixed");
    Environment.ExitCode = passed ? 0 : 1;
    return;
}

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var goal = ContinuousGoal.Create("Fix bug") with { Status = "running" };
var changed = goal.Change("revise", "Keep database unchanged", 1);
Check(changed.Finish(1, "completed") == changed, "late completion cannot overwrite revised objective");
var paused = changed.Change("pause", "", 2).Change("revise", "Add test", 3);
Check(paused.Status == "paused" && paused.Requirements.Length == 2, "revision while paused preserves pause and requirements");
Check(paused.Change("resume", "", 4).Status == "queued", "resume makes same objective schedulable");
bool conflict = false; try { paused.Change("resume", "", 1); } catch (InvalidOperationException) { conflict = true; }
Check(conflict, "goal updates require current version");
Check(GoalMessageRouter.Parse("你好", false).Action == "help" && GoalMessageRouter.Parse("帮我修复登录问题", false).Action == "create",
    "greeting is not a task; explicit natural-language request is");

string connection = Environment.GetEnvironmentVariable("LITTLELOOP_CHAT_TEST_CONNECTION") ??
    throw new InvalidOperationException("Set LITTLELOOP_CHAT_TEST_CONNECTION to an isolated test database (name must contain goal_test).");
var settings = new MySqlConnectionStringBuilder(connection);
if (!settings.Database.Contains("goal_test", StringComparison.Ordinal)) throw new InvalidOperationException("Use a dedicated goal_test database.");
if (!System.Text.RegularExpressions.Regex.IsMatch(settings.Database, "^[a-zA-Z0-9_]+$")) throw new InvalidOperationException("Invalid fixture database name.");
var adminSettings = new MySqlConnectionStringBuilder(connection) { Database = "" };
await using (var admin = new MySqlConnection(adminSettings.ConnectionString))
{
    await admin.OpenAsync();
    await using var create = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{settings.Database}` CHARACTER SET utf8mb4", admin);
    await create.ExecuteNonQueryAsync();
}
var store = new ChatStore(connection, false);
await store.Initialize(); await store.InitializeChannelAdministration(); await store.InitializeGroupModeration(); await store.InitializeChannelDiscussions();
string tag = Guid.NewGuid().ToString("N")[..8];
var owner = new Account(Guid.NewGuid().ToString(), "goal_" + tag, "!fixture");
var stranger = new Account(Guid.NewGuid().ToString(), "other_" + tag, "!fixture");
await store.Register(owner); await store.Register(stranger);
var robot = await store.GetRobot(ChatStore.BotName);
var room = await store.CreateRoom(owner, new("direct", "", [robot.Name]));
await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);
bool CanOperate(string user) => user == owner.Id || user == stranger.Id;
async Task<SendResult> Send(string text, long? reply = null, string? client = null, string? to = null, Account? sender = null)
{
    var actor = sender ?? owner;
    return await store.Send(actor.Id, actor.Name, to ?? room.Id, new(client ?? Guid.NewGuid().ToString(), text, reply), goalRobot: robot.Id)
        ?? throw new Exception("Message not sent.");
}
async Task Drain() { while (await store.ProcessGoalMessage(robot.Id, CanOperate)) { } }
async Task<BotWork> Read(Guid id) => await store.ReadBotWork(id, owner.Id) ?? throw new Exception("Missing goal");
async Task<Guid> GoalFor(SendResult sent, string? inRoom = null) => (await store.FindMessage(inRoom ?? room.Id, sent.Message.Sequence, owner.Id))!.Card!.TaskId!.Value;

string client = Guid.NewGuid().ToString();
var first = await Send("任务：修复解析问题并通过现有测试", client: client);
await Drain(); Guid id = await GoalFor(first);
var duplicate = await Send("任务：修复解析问题并通过现有测试", client: client);
await Drain(); Check(!duplicate.IsNew && await GoalFor(duplicate) == id, "message retry creates exactly one durable goal");
await using var lease = await store.AcquireBotLease();
var running = await store.ClaimBotWork(lease);
Check(running?.Id == id && running.Goal?.Status == "running", "persistent scheduler claims message-created goal");
await Send("补充：不要修改数据库", first.Message.Sequence); await Drain();
Check(await store.GoalShouldYield(running!), "new requirements signal active executor to yield");
await store.FinishGoal(running!, "completed");
var revised = await Read(id);
Check(revised.Status == "queued" && revised.Goal!.Version == 2 && revised.Input.Text.Contains("不要修改数据库"), "stale result leaves revised goal ready with new prompt");
await Send("暂停", first.Message.Sequence); await Drain();
Check(await store.ClaimBotWork(lease) is null, "paused goal cannot be dispatched");
await Send("补充：增加回归用例", first.Message.Sequence); await Drain();
Check((await Read(id)).Status == "paused", "supplement does not implicitly resume paused work");
await Send("恢复", first.Message.Sequence); await Drain();
var resumed = await store.ClaimBotWork(lease);
Check(resumed?.Id == id && resumed.Goal!.Version == 5, "resume dispatches same goal with accumulated requirements");
await store.FinishGoal(resumed!, "waiting-input", output: "请提供期望的错误输出。");
Check(await store.ClaimBotWork(lease) is null, "waiting for input does not busy-loop execution");
await Send("补充：返回格式错误", first.Message.Sequence); await Drain();
running = await store.ClaimBotWork(lease);
await store.FinishGoal(running!, "completed", output: "Verified receipts");
Check((await Read(id)).Status == "completed", "answered clarification resumes and delivers result");
Check(await store.ReadBotWork(id, stranger.Id) is null, "nonmember cannot read goal");

// Simulate crash between message commit and inbox processing; recreate store and recover.
var pending = await Send("任务：验证重启恢复");
store = new ChatStore(connection, false); await store.RecoverContinuousGoals(); await Drain();
Guid recoveryId = await GoalFor(pending);
Check((await Read(recoveryId)).Status == "queued", "durable inbox survives store recreation before processing");
var crash = await store.ClaimBotWork(lease); await store.RecoverContinuousGoals();
Check((await Read(recoveryId)).Status == "needs-reconciliation" && await store.ClaimBotWork(lease) is null,
    "running work becomes unknown on restart and is not replayed");
await Send("恢复", pending.Message.Sequence); await Drain();
Check((await Read(recoveryId)).Status == "needs-reconciliation", "resume cannot bypass unknown side effects");

var group = await store.CreateRoom(owner, new("group", "Goal test", [stranger.Name]));
await store.InstallRobot(group.Id, owner.Id, [robot.Id], true);
var ordinary = await Send("任务：这只是群里讨论的例子", to: group.Id); await Drain();
Check((await store.FindMessage(group.Id, ordinary.Message.Sequence, owner.Id))!.Card is null, "ordinary group discussion cannot trigger task");
var addressed = await Send("@littleloop 任务：检查边界", to: group.Id); await Drain();
Guid groupId = await GoalFor(addressed, group.Id);
await Send("暂停", addressed.Message.Sequence, to: group.Id, sender: stranger); await Drain();
Check((await Read(groupId)).Status == "queued", "another authorized group operator cannot change owner's goal");
await Send("取消", addressed.Message.Sequence, to: group.Id); await Drain();
Check((await Read(groupId)).Status == "cancelled", "explicit group reply cancels own goal");

var empty = await Send("任务"); await Drain(); Guid emptyId = await GoalFor(empty);
Check((await Read(emptyId)).Status == "waiting-input", "empty objective asks for clarification without execution");
await Send("暂停", empty.Message.Sequence); await Drain(); await Send("恢复", empty.Message.Sequence); await Drain();
Check((await Read(emptyId)).Status == "waiting-input", "resuming empty objective still requires clarification");
await Send("补充：修复空输入并通过测试", empty.Message.Sequence); await Drain();
Check((await Read(emptyId)).Status == "queued", "answer fills missing objective");

int notices = 0;
while (await store.NextGoalNotice() is { } notice)
{
    var message = new PostMessage(notice.Id, notice.Text, notice.ReplyTo);
    var card = notice.TaskId is null ? null : new MessageCard("task", TaskId: notice.TaskId);
    var sent = await store.Send(robot.Id, robot.Name, notice.Room, message, card: card);
    var retried = await store.Send(robot.Id, robot.Name, notice.Room, message, card: card);
    Check(sent is not null && retried is { IsNew: false }, "outbox retry does not duplicate chat reply");
    await store.CompleteGoalNotice(notice.Id); notices++;
}
Check(notices > 10, "durable progress and control responses delivered");
await Send("取消", empty.Message.Sequence); await Drain();
if (Environment.GetEnvironmentVariable("LITTLELOOP_CODEX_EXECUTABLE") is { Length: > 0 } executable)
{
    var task = await Send("任务：把 code.txt 的 broken 改成 fixed，运行 verify 测试并检查通过。仅使用宿主 project 工具。");
    await Drain(); var live = await store.ClaimBotWork(lease) ?? throw new Exception("Live goal not claimed");
    string root = Path.GetFullPath(Path.Combine("data", "continuous-goals-live", tag));
    string project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
    File.WriteAllText(Path.Combine(project, "code.txt"), "broken");
    File.WriteAllText(Path.Combine(project, "verify.txt"), "Expected: fixed");
    string[] arguments = Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? [typeof(ContinuousGoal).Assembly.Location.Replace("LittleLoop.Core.dll", "LittleLoop.ChatChecks.dll"), "--verify-fixture"] : ["--verify-fixture"];
    var development = new DevelopmentPolicy(project, ["code.txt"],
        [new("verify", Environment.ProcessPath!, arguments, true, ["verify.txt"])], [".txt"], Budget: 40);
    var policy = new AgentHostPolicy(executable, project, ApprovalMode: "user", OutsideRule: "decline", MaxTurns: 8,
        TimeoutSeconds: 300, MaxRepairs: 1, AttachDocumentation: false, HostToolsOnly: true, Development: development);
    string folder = Path.Combine(root, "session");
    var adapter = new LittleLoopChatAdapter(policy, root);
    var stopped = await adapter.Run(live, folder, async (_, _) =>
    {
        await Send("暂停", task.Message.Sequence); await Drain();
    }, CancellationToken.None, _ => store.GoalShouldYield(live));
    await store.FinishGoal(live, stopped.Status);
    Check(stopped.Status == "suspended" && (await Read(live.Id)).Status == "paused" &&
        new AgentDelegationHost(folder).Read().Status == "suspended", "real Codex turn acknowledges pause at approval boundary");
    Check(File.ReadAllText(Path.Combine(project, "code.txt")) == "broken", "pending patch was not executed during pause");
    await Send("补充：不要修改 verify.txt", task.Message.Sequence); await Drain();
    await Send("恢复", task.Message.Sequence); await Drain();
    var continuation = await store.ClaimBotWork(lease) ?? throw new Exception("Resume not claimed");
    var completed = await adapter.Run(continuation, folder, (request, _) =>
    {
        new AgentDelegationHost(folder).Answer(request.Nonce, "accept", owner.Id);
        return Task.CompletedTask;
    }, CancellationToken.None, _ => store.GoalShouldYield(continuation));
    await store.FinishGoal(continuation, completed.Status, output: completed.Report);
    Check(completed.Status == "completed" && (await Read(live.Id)).Status == "completed" &&
        File.ReadAllText(Path.Combine(project, "code.txt")).Trim() == "fixed",
        "real message goal resumes same execution session and passes actual host-verified test");
    Console.WriteLine("Live evidence: " + root);
}
await lease.DisposeAsync();
await WorkerChecks.Run(store, owner, stranger, robot, room.Id, Check);
Console.WriteLine($"All {checks} chat goal checks passed.");
