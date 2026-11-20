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
if (args is ["--harness"])
{
    await NativeHarnessChecks.Run(Check);
    return;
}
if (args is ["--decision-loop"])
{
    await WorkerChecks.Run(Check);
    Console.WriteLine($"All {checks} focused chat checks passed.");
    return;
}
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
Check(GoalMessageRouter.Parse("查询进度？", true).Action == "status", "progress questions route without changing the goal");
foreach (string discussion in new[] { "如果修改数据库会怎样", "“帮我修改配置”", "> 帮我修复登录", "帮我修复登录问题？" })
    Check(GoalMessageRouter.Parse(discussion, false).Action == "help" && GoalMessageRouter.Parse(discussion, true).Action == "help",
        "discussion does not authorize creation or revision: " + discussion);
const string spreadsheetRequest = "帮我查询一下最近黄金的价格走势，整理为一个excel发给我，尽快，我仅仅测试用";
foreach (var prefix in new[] { "", "任务：", "新任务:" })
    Check(GoalMessageRouter.Parse(prefix + spreadsheetRequest, true, privateChat: true) == new GoalMessageIntent("chat", spreadsheetRequest),
        "general work stays in private conversation with an active project task, prefix " + prefix);
Check(GoalMessageRouter.Parse("明天会下雨吗？", true, privateChat: true).Action == "chat" &&
    GoalMessageRouter.Parse("保留原来的错误提示", true, privateChat: true).Action == "revise" &&
    GoalMessageRouter.Parse("输出空字符串", true, privateChat: true, awaitingInput: true).Action == "revise",
    "ordinary questions, recognizable requirements and clarification answers keep distinct meanings");
Check(GoalMessageRouter.Parse("任务：帮我修复登录问题", false, privateChat: true).Action == "create" &&
    GoalMessageRouter.Parse("修复这个函数", true, privateChat: true, taskReply: true).Action == "revise",
    "project work still supports task creation and targeted corrections");
foreach (string everyday in new[] { "帮我修改这台电脑的时区", "请修复这张老照片", "任务：解释一下这段代码", "请修改明天的行程" })
    Check(GoalMessageRouter.Parse(everyday, false, privateChat: true).Action == "chat",
        "development vocabulary alone does not turn everyday requests into project work: " + everyday);

await SqliteChecks.Run();
await CommitmentHostChecks.Run();

using var fixture = new TemporaryChatDatabase();
string? mysql = Environment.GetEnvironmentVariable("LITTLELOOP_CHAT_TEST_CONNECTION");
string connection = fixture.Connection;
if (!string.IsNullOrWhiteSpace(mysql))
{
    var settings = new MySqlConnectionStringBuilder(mysql);
    if (!settings.Database.Contains("goal_test", StringComparison.Ordinal) ||
        !System.Text.RegularExpressions.Regex.IsMatch(settings.Database, "^[a-zA-Z0-9_]+$"))
        throw new InvalidOperationException("Use a dedicated goal_test database.");
    var adminSettings = new MySqlConnectionStringBuilder(mysql) { Database = "" };
    await using var admin = new MySqlConnection(adminSettings.ConnectionString);
    await admin.OpenAsync();
    await using var create = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{settings.Database}` CHARACTER SET utf8mb4", admin);
    await create.ExecuteNonQueryAsync();
    connection = mysql;
}
string provider = string.IsNullOrWhiteSpace(mysql) ? "Sqlite" : "MySql";
var store = new ChatStore(connection, false, provider);
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
const string clarificationReport = """{"summary":"已复现问题。","questions":["期望的错误输出是什么？","要保留原格式吗？"]}""";
await store.FinishGoal(resumed!, "waiting-input", output: clarificationReport);
Check((await Read(id)).Output == clarificationReport, "raw agent report is retained as task evidence");
const string matrixReport = """{"summary":"验证矩阵","questions":[],"evidence":["line 12"]}""";
Check(AgentConversationSupport.ReportText(matrixReport) == matrixReport &&
    AgentConversationSupport.ReportText("普通文本") == "普通文本", "specialized and plain-text reports keep their contents");
Check(await store.ClaimBotWork(lease) is null, "waiting for input does not busy-loop execution");
await Send("返回格式错误", first.Message.Sequence); await Drain();
running = await store.ClaimBotWork(lease);
await store.FinishGoal(running!, "completed", output: "Verified receipts");
Check((await Read(id)).Status == "completed", "answered clarification resumes and delivers result");
Check(await store.ReadBotWork(id, stranger.Id) is null, "nonmember cannot read goal");
var progress = await Send("查询进度？"); await Drain();
Check(await GoalFor(progress) == id && (await Read(id)).Status == "completed", "unanchored progress resolves completed goal without requeuing it");

var discussionMessage = await Send("你好"); await Drain();
var activeMessage = await Send("帮我修改配置并通过现有测试"); await Drain();
Guid activeId = await GoalFor(activeMessage);
await Send("取消", discussionMessage.Message.Sequence); await Drain();
Check((await Read(activeId)).Status == "queued", "reply to unrelated message cannot cancel active work");
var activeDiscussion = await Send("如果修改数据库会怎样"); await Drain();
Check((await Read(activeId)).Goal!.Version == 1, "hypothetical discussion cannot revise live work");
Check((await store.FindMessage(room.Id, activeDiscussion.Message.Sequence, owner.Id))!.Card is null,
    "discussion during active work does not become an implicit task reference");
var discussionWork = await store.ClaimBotWork(lease, w => w.Kind == "chat");
Check(discussionWork?.Sequence == activeDiscussion.Message.Sequence && discussionWork.Goal is null,
    "hypothetical questions queue a conversational answer without development work");
await store.SetBotStatus(discussionWork!, "completed");
string sheetClient = Guid.NewGuid().ToString();
var sheetMessage = await Send(spreadsheetRequest, client: sheetClient);
await Send(spreadsheetRequest, client: sheetClient);
await Drain();
var sheetWork = await store.ClaimBotWork(lease, w => w.Kind == "chat");
Check(sheetWork is { Goal: null } && sheetWork.Input.Text == spreadsheetRequest &&
    sheetWork.Sequence == sheetMessage.Message.Sequence && await store.ClaimBotWork(lease, w => w.Kind == "chat") is null &&
    (await store.FindMessage(room.Id, sheetMessage.Message.Sequence, owner.Id))!.Card is null && (await Read(activeId)).Goal!.Version == 1,
    "retried spreadsheet request queues one conversational answer without creating or revising a project task");
await store.SetBotStatus(sheetWork!, "completed");
await Send("取消", activeDiscussion.Message.Sequence); await Drain();
Check((await Read(activeId)).Status == "queued", "reply to discussion during work cannot cancel that work");
await Send("取消", activeMessage.Message.Sequence); await Drain();
var cancelledProgress = await Send("进度"); await Drain();
Check(await GoalFor(cancelledProgress) == activeId && (await Read(activeId)).Status == "cancelled", "plain progress can find cancelled work without resuming");

// Reproduce the records already written by the pre-upgrade message router.
var oldGoal = ContinuousGoal.Create(spreadsheetRequest) with { Status = "waiting-input" };
var oldId = Guid.NewGuid();
var oldSource = (await store.Send(owner.Id, owner.Name, room.Id,
    new(Guid.NewGuid().ToString(), "任务：" + spreadsheetRequest, null), card: new("task", TaskId: oldId),
    job: new(oldId, room.Id, owner.Id, 0, "task", oldGoal.Status, new(oldGoal.Objective), Goal: oldGoal)))!.Message;
await using (System.Data.Common.DbConnection db = provider == "Sqlite"
    ? new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection) { Pooling = false }.ConnectionString)
    : new MySqlConnection(connection))
{
    await db.OpenAsync();
    await using var inbox = ChatStore.Command(db, null, """
        INSERT INTO chat_goal_inbox(room_id,seq,owner_id,body,processed) VALUES(@room,@seq,@owner,@body,TRUE)
        """, ("room", room.Id), ("seq", oldSource.Sequence), ("owner", owner.Id), ("body", oldSource.Text));
    await inbox.ExecuteNonQueryAsync();
}
var oldQuestion = (await store.Send(robot.Id, robot.Name, room.Id,
    new(Guid.NewGuid().ToString(), "继续制作金价 Excel，还是修复应用 ID？", oldSource.Sequence),
    card: new("task", TaskId: oldId)))!.Message;
foreach (var (reply, quote) in new[] { ("继续制作金价表格", (long?)null), ("最近一个月就好", (long?)oldQuestion.Sequence), ("继续", (long?)oldQuestion.Sequence) })
{
    await Send(reply, quote); await Drain();
    var followup = await store.ClaimBotWork(lease, w => w.Kind == "chat");
    Check(followup?.Input is { } input && input.Text == reply && input.Anchor == spreadsheetRequest &&
        (await Read(oldId)).Goal is { Status: "waiting-input", Version: 1 },
        "reply continues the old spreadsheet conversation without requeuing its development session: " + reply);
    await store.SetBotStatus(followup!, "completed");
}
await Send("取消", oldQuestion.Sequence); await Drain();
Check((await Read(oldId)).Status == "cancelled", "an explicit control still applies to the old goal");

// Simulate crash between message commit and inbox processing; recreate store and recover.
var pending = await Send("开发任务：验证重启恢复");
store = new ChatStore(connection, false, provider); await store.RecoverContinuousGoals(); await Drain();
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

async Task<Guid> CompleteMemoryFixture(string objective, string? targetRoom = null, Account? actor = null)
{
    string command = (targetRoom == group.Id ? "@littleloop " : "") + "/task " + objective;
    var sent = await Send(command, to: targetRoom, sender: actor);
    await Drain();
    var task = await store.ClaimBotWork(lease) ?? throw new Exception("Missing memory fixture task");
    Check(task.Room == (targetRoom ?? room.Id) && task.Goal?.Objective == objective, "memory leaves the task objective intact");
    await store.FinishGoal(task, "completed", output: "Verified fixture receipt");
    return task.Id;
}

var savedPreference = await Send("记住偏好：完成通知含目标"); await Drain();
var duplicatedPreference = await Send("记住偏好：完成通知含目标", client: savedPreference.Message.ClientId); await Drain();
Check(!duplicatedPreference.IsNew && (await store.FindMessage(room.Id, savedPreference.Message.Sequence, owner.Id))?.Card is null,
    "explicit preference write is idempotent and creates no task");
store = new ChatStore(connection, false, provider);
var rememberedQuery = await Send("查看偏好"); await Drain();
var detailedGoal = await CompleteMemoryFixture("私聊记忆目标");

var otherPrivate = await store.CreateRoom(stranger, new("direct", "", [robot.Name]));
await store.InstallRobot(otherPrivate.Id, stranger.Id, [robot.Id], true);
var isolatedQuery = await Send("查看偏好", to: otherPrivate.Id, sender: stranger); await Drain();
var isolatedRecall = await Send("回顾经验", to: otherPrivate.Id, sender: stranger); await Drain();
var groupMemory = await Send("@littleloop 忘记偏好", to: group.Id); await Drain();
var afterGroupQuery = await Send("查看偏好"); await Drain();
var otherGoal = await CompleteMemoryFixture("另一个用户的目标", otherPrivate.Id, stranger);
var sharedGoal = await CompleteMemoryFixture("群聊已完成的目标", group.Id);
var recalled = await Send("回顾经验"); await Drain();

await Send("记住偏好：完成通知简洁"); await Drain();
var correctedGoal = await CompleteMemoryFixture("更正后的目标");
await Send("忘记偏好"); await Drain();
var forgottenQuery = await Send("查看偏好"); await Drain();
var forgottenGoal = await CompleteMemoryFixture("忘记后的目标");

var empty = await Send("任务"); await Drain(); Guid emptyId = await GoalFor(empty);
Check((await Read(emptyId)).Status == "waiting-input", "empty objective asks for clarification without execution");
await Send("暂停", empty.Message.Sequence); await Drain(); await Send("恢复", empty.Message.Sequence); await Drain();
Check((await Read(emptyId)).Status == "waiting-input", "resuming empty objective still requires clarification");
await Send("补充：修复空输入并通过测试", empty.Message.Sequence); await Drain();
Check((await Read(emptyId)).Status == "queued", "answer fills missing objective");

int notices = 0;
var deliveredNotices = new List<GoalNotice>();
while (await store.NextGoalNotice() is { } notice)
{
    deliveredNotices.Add(notice);
    var message = new PostMessage(notice.Id, notice.Text, notice.ReplyTo);
    var card = notice.TaskId is null ? null : new MessageCard("task", TaskId: notice.TaskId);
    var sent = await store.Send(robot.Id, robot.Name, notice.Room, message, card: card);
    var retried = await store.Send(robot.Id, robot.Name, notice.Room, message, card: card);
    Check(sent is not null && retried is { IsNew: false }, "outbox retry does not duplicate chat reply");
    await store.CompleteGoalNotice(notice.Id); notices++;
}
Check(notices > 10, "durable progress and control responses delivered");
Check(deliveredNotices.Any(n => n.TaskId == id &&
    n.Text.EndsWith("已复现问题。\n\n期望的错误输出是什么？\n\n要保留原格式吗？")), "clarification arrives in chat as readable summary and questions");
string ReplyFor(SendResult sent) => deliveredNotices.Single(n => n.Room == sent.Message.RoomId && n.ReplyTo == sent.Message.Sequence).Text;
string CompletionFor(Guid task) => deliveredNotices.Single(n => n.TaskId == task && n.Text.StartsWith("任务 v")).Text;
Check(ReplyFor(rememberedQuery).Contains("当前偏好：完成通知含目标") && CompletionFor(detailedGoal).Contains("任务目标：私聊记忆目标"),
    "saved preference survives reopening the store and changes later completion notices");
Check(!CompletionFor(correctedGoal).Contains("任务目标：") && !CompletionFor(forgottenGoal).Contains("任务目标：") &&
    ReplyFor(forgottenQuery).Contains("尚未保存偏好"), "correction and forgetting restore concise completion");
Check(ReplyFor(isolatedQuery).Contains("尚未保存偏好") && ReplyFor(isolatedRecall).Contains("暂无") &&
    !CompletionFor(otherGoal).Contains("任务目标：") && !CompletionFor(sharedGoal).Contains("任务目标：") &&
    ReplyFor(groupMemory).Contains("仅在 LittleLoop 私聊") &&
    ReplyFor(afterGroupQuery).Contains("当前偏好：完成通知含目标"),
    "preferences and experience remain isolated across users and room types");
Check(ReplyFor(recalled).Contains("私聊记忆目标") && !ReplyFor(recalled).Contains("另一个用户的目标") &&
    !ReplyFor(recalled).Contains("群聊已完成的目标") &&
    !ReplyFor(recalled).Contains("验证重启恢复"), "recall only lists completed goals in the current private room");
await Send("取消", empty.Message.Sequence); await Drain();

string memoryProject = Path.GetFullPath(Path.Combine("data", "experience-checks", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(memoryProject);
File.WriteAllText(Path.Combine(memoryProject, "code.txt"), "fixture");
File.WriteAllText(Path.Combine(memoryProject, "verify.txt"), "protected fixture");
var memoryFiles = new ProjectFiles(new(memoryProject, ["code.txt"],
    [new("verify", Environment.ProcessPath!, ["--verify-fixture"], true, ["verify.txt"])], [".txt"]));
var patchResult = new ActionResult("fixture-patch", "fixture-decision", "patch", "fixture", ExecutionStatus.Completed,
    ["code.changed"], "fixture", "REPORT BODY MUST NOT BE COPIED", true)
{
    ToolReceipt = new("project.patch", "1", memoryProject,
        [new("code.txt", "input", 7, "old-hash"), new("code.txt", "output", 7, "new-hash"),
            new("verify.txt", "output", 7, "protected-hash")], "TOOL BODY MUST NOT BE COPIED")
};
var experience = VerifiedGoalExperience.FromReceipts(memoryFiles, [new(null!, patchResult, [])])
    ?? throw new Exception("No experience derived from fixture patch");
Check(experience.Paths.SequenceEqual(["code.txt"]) &&
    VerifiedGoalExperience.FromReceipts(memoryFiles, [new(null!, patchResult with { Status = ExecutionStatus.Failed }, [])]) is null,
    "distillation keeps only authorized paths from successful patch outputs");

const string repeatObjective = "任务：更新示例文字并通过固定测试";
async Task<(SendResult Message, BotWork Work)> ClaimExperienceTask(bool other = false)
{
    var sent = await Send(repeatObjective, to: other ? otherPrivate.Id : null, sender: other ? stranger : null);
    await Drain();
    var work = await store.ClaimBotWork(lease) ?? throw new Exception("Experience fixture was not claimed");
    return (sent, work);
}
var beforeOptIn = await ClaimExperienceTask();
await store.FinishGoal(beforeOptIn.Work, "completed", experience: experience);
await Send("启用经验"); await Drain();
var training = await ClaimExperienceTask();
Check(await store.ReadGoalExperience(training.Work, experience.GrantHash) is null,
    "successful tasks before opt-in provide the no-memory baseline");
await store.FinishGoal(training.Work, "completed", experience: experience);
store = new ChatStore(connection, false, provider);
var repeat = await ClaimExperienceTask();
var hint = await store.ReadGoalExperience(repeat.Work, experience.GrantHash);
string repeatPrompt = LittleLoopChatAdapter.BuildPrompt(repeat.Work with { ExperienceHint = hint });
Check(hint is not null && hint.SourceSequence == training.Message.Message.Sequence &&
    repeatPrompt.Contains("code.txt") && !repeatPrompt.Contains("REPORT BODY") && !repeatPrompt.Contains("new-hash") &&
    !LittleLoopChatAdapter.BuildPrompt(repeat.Work).Contains("code.txt"),
    "restart preserves source provenance and matching work gets only the bounded hint");
Check(await store.ReadGoalExperience(repeat.Work, new string('A', 64)) is null,
    "experience does not cross development grants");
await Send("补充：保持输出格式", repeat.Message.Message.Sequence); await Drain();
await store.FinishGoal(repeat.Work, "completed", experience: experience);
var revisedExperience = await store.ClaimBotWork(lease) ?? throw new Exception("Revised experience fixture missing");
Check(await store.ReadGoalExperience(revisedExperience, experience.GrantHash) is null,
    "new requirements prevent reuse and stale completion cannot replace the current version");
await Send("取消", repeat.Message.Message.Sequence); await Drain();

await Send("启用经验", to: otherPrivate.Id, sender: stranger); await Drain();
var otherRepeat = await ClaimExperienceTask(other: true);
Check(await store.ReadGoalExperience(otherRepeat.Work, experience.GrantHash) is null, "experience does not cross users or private rooms");
await store.FinishGoal(otherRepeat.Work, "failed", experience: experience);
await Send("停用经验"); await Drain();
var disabledRepeat = await ClaimExperienceTask();
Check(await store.ReadGoalExperience(disabledRepeat.Work, experience.GrantHash) is null, "disable stops matching experience reuse");
await store.FinishGoal(disabledRepeat.Work, "completed", experience: experience);
await Send("启用经验"); await Drain();
await Send("撤回经验", training.Message.Message.Sequence); await Drain();
var revokedRepeat = await ClaimExperienceTask();
Check(await store.ReadGoalExperience(revokedRepeat.Work, experience.GrantHash) is null,
    "revocation removes the source and completion while disabled does not create a replacement");
await store.FinishGoal(revokedRepeat.Work, "failed", experience: experience);

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
await NaturalMemoryChecks.Run(connection, provider, memoryFiles, experience, Check);
await ConversationChecks.Run(Check);
await WorkerChecks.Run(Check);
await BotFriendAccessChecks.Run(store, robot, Check);
Console.WriteLine($"All {checks} chat goal checks passed on {provider}.");

sealed class TemporaryChatDatabase : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("littleloop-goals-").FullName;
    public string Connection => new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "chat.db") }.ConnectionString;
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
