using LittleLoop.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class WorkerChecks
{
    private static async Task CheckBotTimers(IConfiguration config, string project, Account owner, Action<bool, string> check)
    {
        using var fixture = new TemporaryChatDatabase();
        var clock = new TestClock();
        ChatStore Open() => new(fixture.Connection, false, "Sqlite", clock);
        var store = Open();
        await store.Initialize();
        await store.InitializeChannelAdministration();
        await store.InitializeGroupModeration();
        await store.InitializeChannelDiscussions();
        await store.Register(owner);
        var robot = await store.GetRobot(ChatStore.BotName);
        var room = await store.CreateRoom(owner, new("direct", null, [robot.Name]));
        await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);
        var engine = new ControlledEngine();
        var harness = new ControlledHarness("codex", project);
        ChatBot Create() => new(store, new QuietHub(), config, NullLogger<ChatBot>.Instance, engine, [harness]);
        async Task Say(string text) => await store.Send(owner.Id, owner.Name, room.Id,
            new(Guid.NewGuid().ToString(), text, null), goalRobot: robot.Id);
        async Task<BotWork> Until(Guid id, Func<BotWork, bool> matches)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            while (true)
            {
                if (await store.ReadBotWork(id, owner.Id) is { } work && matches(work)) return work;
                await Task.Delay(25, deadline.Token);
            }
        }

        ChatBot worker = Create();
        await worker.StartAsync(default);
        try
        {
            await Say("用合成回执检查任务定时恢复");
            (await engine.Next()).Choose(new(BotAction.Delegate, Message: "先看证据", Objective: "确认合成数据",
                Harness: "codex", Instruction: "第一轮检查"));
            (await harness.Next()).Return("等待稍后出现的证据");
            var receipt = await engine.Next();
            Guid id = receipt.Context.Trigger.GoalId!.Value;
            receipt.Choose(new(BotAction.Wait, id, WakeAfterSeconds: 5));
            var sleeping = await Until(id, w => w.Input.Loop?.Wake is not null);
            var firstWake = sleeping.Input.Loop!.Wake!;
            clock.Advance(4_999);
            check(firstWake.GoalVersion == sleeping.Goal!.Version && await store.NextBotInput(goal: true) is null,
                "a stored timer is fenced by goal version and does not fire early");

            await worker.StopAsync(default);
            worker.Dispose();
            clock.Advance(1);
            store = Open();
            worker = Create();
            await worker.StartAsync(default);
            var timer = await engine.Next();
            check(timer.Context.Trigger is { Kind: BotEventKind.Timer } observation &&
                observation.GoalId == id && observation.WakeAtUnixMilliseconds == firstWake.AtUnixMilliseconds,
                "an overdue timer reaches the regular decision engine after reopening SQLite and restarting the worker");
            timer.Choose(new(BotAction.Wait, id, Message: "先报告仍在等待", WakeAfterSeconds: 5));
            await Until(id, w => w.Input.Loop?.ScheduledWakeups == 2);
            check(await store.NextBotInput(goal: true) is null &&
                (await store.GoalObservations(id)).Count(e => e.Kind == BotEventKind.Timer) == 1,
                "reporting and scheduling a new check consumes the old timer transactionally");
            clock.Advance(5_000);
            var resume = await engine.Next();
            resume.Choose(new(BotAction.Delegate, id, Harness: "codex", Instruction: "后续检查"));
            var second = await harness.Next();
            second.Return("得到所需证据");
            (await engine.Next()).Choose(new(BotAction.Wait, id, WakeAfterSeconds: 5));
            await Until(id, w => w.Input.Loop?.ScheduledWakeups == 3);
            clock.Advance(5_000);
            var stale = await engine.Next();
            await Say("暂停这个目标");
            stale.Choose(new(BotAction.Delegate, id, Harness: "codex", Instruction: "过期的检查"));
            (await engine.Next()).Choose(new(BotAction.Pause, id));
            var paused = await Until(id, w => w.Status == "paused");
            check(paused.Input.Loop!.Wake is null && await store.NextBotInput(goal: true) is null &&
                harness.Calls.Count == 2 && (await store.GoalObservations(id)).Count(e => e.Kind == BotEventKind.Timer) == 2,
                "a concurrent user pause supersedes a timer decision and clears the schedule");

            foreach (var action in new[] { BotAction.Cancel, BotAction.Complete })
            {
                await Say("安排另一个定时检查目标");
                (await engine.Next()).Choose(new(BotAction.Delegate, Objective: "读取合成值", Message: "计划读取",
                    Harness: "codex", Instruction: "读取"));
                (await harness.Next()).Return("合成值");
                var next = await engine.Next();
                var other = next.Context.Trigger.GoalId!.Value;
                next.Choose(new(BotAction.Wait, other, WakeAfterSeconds: 5));
                await Until(other, w => w.Input.Loop?.Wake is not null);
                await Say("处理当前目标");
                (await engine.Next()).Choose(new(action, other));
                await Until(other, w => w.Status == (action == BotAction.Cancel ? "cancelled" : "completed"));
                clock.Advance(5_000);
                check(await store.NextBotInput(goal: true) is null &&
                    (await store.ReadBotWork(other, owner.Id))!.Input.Loop!.Wake is null,
                    $"{action} invalidates the old timer even after its scheduled time");
            }
        }
        finally { await worker.StopAsync(default); worker.Dispose(); }
    }

    private sealed class TestClock : TimeProvider
    {
        private long now = 1_800_000_000_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref now));
        public void Advance(long milliseconds) => Interlocked.Add(ref now, milliseconds);
    }
}
