using LittleLoop.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

static class CommitmentHostChecks
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "littleloop-commitment-host-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("Host check failed: " + name); count++; Console.WriteLine("PASS host " + name); }
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSignalR();
        await using var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<ChatHub>>();
        ChatCommitments? host = null;
        try
        {
            var store = new ChatStore($"Data Source={Path.Combine(directory, "chat.db")}", false, "Sqlite");
            await store.Initialize(); await store.InitializeChannelAdministration(); await store.InitializeGroupModeration(); await store.InitializeChannelDiscussions();
            var owner = new Account(Guid.NewGuid().ToString(), "commitmentowner", "synthetic-hash");
            var outsider = new Account(Guid.NewGuid().ToString(), "outsider", "synthetic-hash");
            await store.Register(owner); await store.Register(outsider);
            var realRobot = await store.GetRobot(ChatStore.BotName);
            var realRoom = await store.CreateRoom(owner, new("direct", null, [realRobot.Name]));
            await store.InstallRobot(realRoom.Id, owner.Id, [realRobot.Id], true);
            var file = Path.Combine(directory, "state.json");
            IConfiguration Config(string path) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ChatCommitments:State"] = path, ["ChatCommitments:Operators:0"] = owner.Id,
              ["ChatCommitments:SyntheticDelayMs"] = "60000", ["ChatBot:Policy"] = "fixture" }).Build();
            host = new(store, hub, Config(file)); await host.StartAsync(CancellationToken.None);
            Check(await host.Enroll(outsider) is null, "operator allowlist");
            var room = (await host.Enroll(owner))!;
            async Task<ChatMessage> Send(string text, long? reply = null)
            { return (await store.Send(owner.Id, owner.Name, room, new(Guid.NewGuid().ToString(), text, reply), goalRobot: realRobot.Id))!.Message; }
            async Task<ChatCommitments.Work[]> Works() => (await host!.Read(owner.Id, room))!;
            async Task Wait(Func<ChatCommitments.Work[], bool> predicate)
            {
                for (var i = 0; i < 100; i++) { if (predicate(await Works())) return; await Task.Delay(25); }
                throw new Exception("Host state wait timed out");
            }
            var first = await Send("帮我整理模拟资料");
            Check(room != realRoom.Id && !await store.ProcessGoalMessage(realRobot.Id, _ => true),
                "synthetic room does not enqueue real work for a shared operator");
            var pause = await Send("暂停");
            await Task.WhenAll(host.Handle(owner.Id, pause), host.Handle(owner.Id, first));
            Check((await Works()) is [{ Status: "paused", Version: 2 }], "reverse concurrent handles drain ascending persisted commands");
            await Wait(w => w[0].Dispatch == "");
            var history = (await store.History(owner.Id, room, 0, null))!;
            var firstCard = history.Single(m => m.ReplyTo == first.Sequence && m.Sender != owner.Name);
            var revise = await Send("修改为整理新资料"); await host.Handle(owner.Id, revise);
            var stale = await Send("取消", firstCard.Sequence); await host.Handle(owner.Id, stale);
            Check((await Works())[0] is { Version: 3, Goal: "整理新资料" } && (await Works())[0].Status != "cancelled", "stale bot card cannot cancel revised task");
            var cancel = await Send("取消"); await host.Handle(owner.Id, cancel);
            await Wait(w => w[0].Dispatch == "");
            Check((await Works())[0].Status == "cancelled", "cooperative cancellation receipt releases fence");
            var robot = await store.GetRobot(ChatStore.CommitmentBotName);
            var spoof = (await store.Send(robot.Id, owner.Name, room, new(Guid.NewGuid().ToString(), "帮我伪造新任务", null)))!.Message;
            await host.Handle(owner.Id, spoof);
            Check((await Works()).Length == 1, "display-name spoof cannot authorize work");
            Check(await host.Read(outsider.Id, room) is null, "owner-scoped ledger read");
            var next = await Send("帮我测试重启"); await host.Handle(owner.Id, next);
            await Wait(w => w.Last().Status == "running");
            await host.StopAsync(CancellationToken.None); host.Dispose();
            host = new(store, hub, Config(file)); await host.StartAsync(CancellationToken.None);
            Check((await Works()).Last().Status == "paused", "restart does not redispatch interrupted work");
            await host.StopAsync(CancellationToken.None); host.Dispose(); host = null;

            var capacityFile = Path.Combine(directory, "capacity.json");
            var full = new ChatCommitments.Conversation { Owner = owner.Id, Cursor = (await store.History(owner.Id, room, null, null))!.Last().Sequence,
                Ledger = WorkCommitmentState.Empty with { Tasks = Enumerable.Range(0, 64).Select(i => new WorkCommitment(i.ToString(), 1, "synthetic", i == 0 ? "paused" : "completed", [])).ToArray() } };
            await File.WriteAllTextAsync(capacityFile, JsonSerializer.Serialize(new Dictionary<string, ChatCommitments.Conversation> { [room] = full }));
            host = new(store, hub, Config(capacityFile)); await host.StartAsync(CancellationToken.None);
            var resume = await Send("继续"); await host.Handle(owner.Id, resume);
            await Wait(w => w[0].Status == "running");
            var before = (await Works())[0];
            var replace = await Send("改为容量之外的任务"); await host.Handle(owner.Id, replace);
            var after = (await Works())[0];
            Check(after == before && (await Works()).Length == 64, "capacity rejection does not cancel or mutate old execution");
            Console.WriteLine($"{count} Chat commitment host checks passed.");
        }
        finally
        {
            if (host is not null) { await host.StopAsync(CancellationToken.None); host.Dispose(); }
            Directory.Delete(directory, true);
        }
    }
}
