using System.Threading.Channels;
using LittleLoop.Tools;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorkerChecks
{
    public static async Task Run(ChatStore store, Account owner, Account approver, Account robot, string room, Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("data", "continuous-worker-checks", Guid.NewGuid().ToString("N")));
        string project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "code.txt"), "fixture");
        File.WriteAllText(Path.Combine(project, "verify.txt"), "protected verifier fixture");
        string policy = Path.Combine(root, "policy.json");
        File.WriteAllText(policy, ChatStore.ToJson(new AgentHostPolicy("unused", project,
            Development: new(project, ["code.txt"], [new("verify", Environment.ProcessPath!, ["--verify-fixture"], true, ["verify.txt"])], [".txt"]))));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChatBot:Policy"] = policy, ["ChatBot:Data"] = Path.Combine(root, "worker"),
            ["ChatBot:Operators"] = owner.Name, ["ChatBot:Approvers"] = approver.Name
        }).Build();
        var executor = new ControlledAdapter();
        var independent = new ControlledAdapter(ChatStore.DeepSeekTestBotName, Path.Combine(root, "independent"));
        var independentRobot = await store.GetRobot(independent.Name);
        var otherRoom = await store.CreateRoom(owner, new("direct", "", [independentRobot.Name]));
        await store.InstallRobot(otherRoom.Id, owner.Id, [independentRobot.Id], true);
        ChatBot Create(ControlledAdapter? primary = null) =>
            new(store, new QuietHub(), config, NullLogger<ChatBot>.Instance, [primary ?? executor, independent]);
        async Task<SendResult> Send(string text, long? reply = null) =>
            await store.Send(owner.Id, owner.Name, room, new(Guid.NewGuid().ToString(), text, reply), goalRobot: robot.Id)
                ?? throw new Exception("Worker fixture message not sent");
        async Task Wait(Guid id, string status)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await store.ReadBotWork(id, owner.Id))?.Status != status) await Task.Delay(50, limit.Token);
        }
        var worker = Create(); await worker.StartAsync(CancellationToken.None);
        try
        {
            var message = await Send("任务：验证持续调度");
            var first = await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            check(first.Goal is { Version: 1, Status: "running" }, "background worker consumes ordinary chat message without manual queue action");

            Guid sameProjectId = Guid.NewGuid();
            var sameProject = await worker.Queue(room, owner.Id, owner.Name, sameProjectId, null, "task", new("Same workspace"));
            Guid independentId = Guid.NewGuid();
            await worker.Queue(otherRoom.Id, owner.Id, owner.Name, independentId, null, "task",
                new("Independent workspace", Provider: independent.Name));
            var parallel = await independent.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            check(parallel.Id == independentId && (await store.ReadBotWork(first.Id, owner.Id))?.Status == "running" &&
                (await store.ReadBotWork(sameProjectId, owner.Id))?.Status == "queued",
                "occupied workspace blocks its own queue but allows a later independent goal to run");
            Guid questionId = Guid.NewGuid();
            await worker.Queue(room, owner.Id, owner.Name, questionId, null, "question",
                new("Question during execution", Source: worker.Cite("code.txt", 1, 1)));
            await Wait(questionId, "completed");
            check((await store.ReadBotWork(independentId, owner.Id))?.Status == "running",
                "source question finishes while all development slots are occupied");
            await Send("进度");
            using (var overviewLimit = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                while (!(await store.History(owner.Id, room, null, null))!.Any(m =>
                    m.Sender == robot.Name && m.Text.Contains("当前有 2 项未结束任务") &&
                    m.Text.Contains("Same workspace") && m.Text.Contains("验证持续调度")))
                    await Task.Delay(50, overviewLimit.Token);
            check(true, "plain progress lists the user's in-room goals while execution continues");
            independent.Release.TrySetResult();
            await Wait(independentId, "completed");
            await Send("取消", sameProject!.Sequence);
            await Wait(sameProjectId, "cancelled");

            await Send("暂停", message.Message.Sequence); await Wait(first.Id, "paused");
            await executor.Yielded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await worker.StopAsync(CancellationToken.None); worker.Dispose();
            worker = Create(); await worker.StartAsync(CancellationToken.None);
            await Task.Delay(900);
            check(executor.Calls == 1 && (await store.ReadBotWork(first.Id, owner.Id))!.Status == "paused", "paused goal survives worker restart without automatic execution");
            check(independent.Calls == 1 && (await store.ReadBotWork(independentId, owner.Id))?.Status == "completed",
                "restart preserves independent goal completion without a second dispatch");
            await Send("补充：保留已有数据", message.Message.Sequence);
            await Send("恢复", message.Message.Sequence);
            var resumed = await executor.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            check(resumed.Id == first.Id && resumed.Goal!.Version == 4 && resumed.Input.Text.Contains("保留已有数据"),
                "worker resumes same goal with conversation requirements after restart");
            executor.Release.TrySetResult(); await Wait(first.Id, "completed");
            using var noticeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!(await store.History(owner.Id, room, null, null))!.Any(m => m.Sender == robot.Name && m.Text.Contains("实际修改与必需测试通过")))
                await Task.Delay(100, noticeLimit.Token);
            check(true, "background completion is delivered to durable chat timeline");
            Guid scheduledId = Guid.NewGuid();
            await worker.Queue(room, owner.Id, owner.Name, scheduledId, message.Message.Sequence, "task", new("Existing schedule action"));
            await Wait(scheduledId, "completed");
            check((await store.ReadBotWork(scheduledId, owner.Id))!.Goal is { Version: 1 },
                "existing schedule action uses the same persistent goal lifecycle");

            // More than one inbox batch, with all work retained in the same database and room.
            var backlog = new List<(Guid Id, long Sequence)>();
            for (int i = 0; i < 40; i++)
            {
                var sent = await Send("任务");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                ChatMessage? linked;
                while ((linked = await store.FindMessage(room, sent.Message.Sequence, owner.Id))?.Card?.TaskId is null)
                    await Task.Delay(20, timeout.Token);
                backlog.Add((linked.Card.TaskId.Value, sent.Message.Sequence));
            }
            foreach (var item in backlog) await Send("暂停", item.Sequence);
            foreach (var item in backlog) await Wait(item.Id, "paused");
            await worker.StopAsync(CancellationToken.None); worker.Dispose();
            // Accumulate input while the worker is down, then replay the durable inbox.
            foreach (var item in backlog)
            {
                await Send("补充：保留已有数据并完成测试", item.Sequence);
                await Send("恢复", item.Sequence);
            }
            worker = Create(); await worker.StartAsync(CancellationToken.None);
            foreach (var item in backlog) await Wait(item.Id, "completed");
            check(executor.Calls == 43, "40 queued goals after restart execute exactly once without starvation");
            await worker.StopAsync(CancellationToken.None); worker.Dispose();
            worker = Create(); await worker.StartAsync(CancellationToken.None);
            foreach (var item in backlog)
            {
                var restored = await store.ReadBotWork(item.Id, owner.Id);
                check(restored?.Goal is { Version: 4, Status: "completed", Objective: "保留已有数据并完成测试" },
                    "batch goal retains supplemented objective, version and terminal state after restart");
            }
            await worker.StopAsync(CancellationToken.None); worker.Dispose();
            var inFlight = new ControlledAdapter();
            worker = Create(inFlight);
            var friend = new Account(Guid.NewGuid().ToString(), "worker_friend_" + Guid.NewGuid().ToString("N")[..8], "!fixture");
            await store.Register(friend);
            await store.SetBotFriendAllowlist(robot.Id, friend.Name, true);
            var friendGrant = await store.RequestBotFriend(friend.Id, robot.Id);
            var friendRoom = friendGrant?.RoomId ?? throw new InvalidOperationException("Could not establish a room for the worker test.");
            await worker.StartAsync(CancellationToken.None);
            check(!worker.CanOperate(friend.Id) && await worker.CanOperateRoom(friend.Id, friendRoom),
                "friend access is scoped to its room, independently of static operators");
            await store.Send(friend.Id, friend.Name, friendRoom,
                new(Guid.NewGuid().ToString(), "任务：验证执行中撤销机器人访问", null), goalRobot: robot.Id, legacyBotOperator: false);
            var friendWork = await inFlight.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            await store.SetBotFriendAllowlist(robot.Id, friend.Name, false);
            await inFlight.Yielded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                while ((await store.ReadBotWork(friendWork.Id, friend.Id))?.Status != "cancelled")
                    await Task.Delay(50, deadline.Token);
            await store.SetBotFriendAllowlist(robot.Id, friend.Name, true);
            await store.RequestBotFriend(friend.Id, robot.Id);
            await Task.Delay(900);
            check(inFlight.Calls == 1 && (await store.ReadBotWork(friendWork.Id, friend.Id))?.Status == "cancelled",
                "revoked in-flight work yields and granting future access does not replay the old task");
        }
        finally { await worker.StopAsync(CancellationToken.None); worker.Dispose(); }
    }

    private sealed class ControlledAdapter(string name = ChatStore.BotName, string? projectRoot = null) : IChatRobotAdapter
    {
        public string Name => name;
        public string? ProjectRoot => projectRoot;
        public bool SupportsApproval => false;
        public Channel<BotWork> Started { get; } = Channel.CreateUnbounded<BotWork>();
        public TaskCompletionSource Yielded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<string> Answer(BotInput input, CancellationToken token) => Task.FromResult("fixture");
        public async Task<RobotOutcome> Run(BotWork work, string folder, Func<AgentApprovalRequest, CancellationToken, Task> approval,
            CancellationToken token, Func<CancellationToken, Task<bool>>? shouldYield = null)
        {
            Calls++; await Started.Writer.WriteAsync(work, token);
            while (!Release.Task.IsCompleted)
            {
                if (shouldYield is not null && await shouldYield(token))
                { Yielded.TrySetResult(); return new("suspended"); }
                await Task.Delay(20, token);
            }
            return new("completed", "Controlled executor receipt (not a real agent test).");
        }
    }
    private sealed class QuietHub : IHubContext<ChatHub>
    {
        public IHubClients Clients { get; } = new QuietClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private sealed class QuietClients : IHubClients, IClientProxy
    {
        public IClientProxy All => this;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Client(string connectionId) => this;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
        public IClientProxy Group(string groupName) => this;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
        public IClientProxy User(string userId) => this;
        public IClientProxy Users(IReadOnlyList<string> userIds) => this;
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
