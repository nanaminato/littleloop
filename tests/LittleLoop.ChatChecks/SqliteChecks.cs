using Microsoft.Data.Sqlite;

static class SqliteChecks
{
    public static async Task Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "littleloop-chat-checks-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "chat.db"), Pooling = false }.ConnectionString;
            var store = new ChatStore(connection, true, "Sqlite");
            await Initialize(store);
            await Initialize(store); // schema replay
            var alice = new Account(Guid.NewGuid().ToString(), "alice", "hash-a");
            var bob = new Account(Guid.NewGuid().ToString(), "bob", "hash-b");
            Check(await store.Register(alice), "register");
            Check(await store.Register(bob), "register peer");
            Check(!await store.Register(alice), "duplicate registration");
            Check((await store.Find("alice"))?.Id == alice.Id, "find");
            var room = await store.CreateRoom(alice, new OpenRoom("direct", null, [bob.Name]));
            Check((await store.CreateRoom(alice, new OpenRoom("direct", null, [bob.Name]))).Id == room.Id, "direct room idempotency");
            var input = new PostMessage(Guid.NewGuid().ToString(), "Hello sqlite!", null);
            var sent = await store.Send(alice.Id, alice.Name, room.Id, input);
            Check(sent is { IsNew: true }, "send");
            Check(await store.Send(alice.Id, alice.Name, room.Id, input) is { IsNew: false }, "send idempotency");
            Check((await store.History(bob.Id, room.Id, null, null)) is [{ Text: "Hello sqlite!" }], "history");
            Check(await store.History(Guid.NewGuid().ToString(), room.Id, null, null) is null, "membership isolation");
            Check((await store.Rooms(bob.Id)).Single().Unread == 1, "room preview/unread");
            Check(await store.MarkRead(bob.Id, room.Id, 100), "mark read");
            Check((await store.Rooms(bob.Id)).Single().Unread == 0, "read clamp");
            await store.PutSetting(alice.Id, "test", new { value = "persisted" });
            await store.PutSetting(alice.Id, "test", new { value = "updated" });
            var device = await store.AddDevice(alice, "integration-check");
            Check(device is not null && await store.ActiveDevice(alice.Id, device), "device expiry");
            Check(await store.ReplacePassword(alice, "new-hash"), "password compare");
            Check(!await store.ReplacePassword(alice, "other-hash"), "stale password comparison");
            var lease = await store.AcquireBotLease();
            try { await store.AcquireBotLease(); throw new Exception("second bot lease accepted"); }
            catch (IOException) { }
            await lease.DisposeAsync();
            await using var nextLease = await store.AcquireBotLease();
            await store.RecoverBotWork(nextLease);
            Check(await store.ClaimBotWork(nextLease) is null, "empty bot queue");
            var reopened = new ChatStore(connection, true, "Sqlite");
            Check((await reopened.History(bob.Id, room.Id, null, null))?.Length == 1, "restart persistence");
            var bobDevice = await reopened.AddDevice(bob, "notification-test");
            Check(bobDevice is not null, "peer device");
            Check(await reopened.SavePush(bobDevice!, new DevicePush("https://example.invalid/test", new PushKeys("synthetic-key", "synthetic-auth"), "en")), "push registration");
            Check(await reopened.SaveFcm(bobDevice!, "synthetic-token", false), "FCM registration");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
                reopened.Send(alice.Id, alice.Name, room.Id, new PostMessage(Guid.NewGuid().ToString(), "parallel " + i, null)))));
            Check(concurrent.All(x => x is { IsNew: true }) && concurrent.Select(x => x!.Message.Sequence).Distinct().Count() == 12, "concurrent sequence allocation");
            Check((await reopened.History(bob.Id, room.Id, null, null))?.Length == 13, "concurrent persistence");
            var push = await reopened.ClaimPush();
            Check(push is not null && await reopened.ReadPush(push.Value) is not null, "push queue read/lease");
            await reopened.FinishPush(push!.Value);
            var fcm = await reopened.ClaimFcm();
            Check(fcm is not null && await reopened.ReadFcm(fcm.Value) is not null, "FCM queue read/lease");
            await reopened.CompleteFcm(fcm!.Value, true);
            await reopened.PutSetting(bob.Id, "notifications", new { direct = false });
            Check(await reopened.ReadPush(push.Value + 1) is null, "disabled push respects JSON false");
            Check(await reopened.ReadFcm(fcm.Value + 1) is null, "disabled FCM respects JSON false");
            await reopened.PutSetting(bob.Id, "notifications", new { direct = true });
            Check(await reopened.ReadPush(push.Value + 1) is not null, "enabled push respects JSON true");
            await using var db = new SqliteConnection(connection);
            await db.OpenAsync();
            await using var integrity = db.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            Check((string?)await integrity.ExecuteScalarAsync() == "ok", "integrity");
            integrity.CommandText = "PRAGMA foreign_key_check";
            Check(await integrity.ExecuteScalarAsync() is null, "foreign keys");
            integrity.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name LIKE 'ix_chat_%'";
            Check(Convert.ToInt32(await integrity.ExecuteScalarAsync()) >= 8, "secondary indexes retained");
            Guid legacyId = Guid.NewGuid();
            await reopened.Send(alice.Id, alice.Name, room.Id, new(legacyId.ToString(), "Legacy task", null),
                job: new(legacyId, room.Id, alice.Id, 0, "task", "completed", new("Legacy task")));
            integrity.CommandText = """
                ALTER TABLE chat_bot_work DROP COLUMN goal;
                DROP TABLE chat_goal_inbox;
                DROP TABLE chat_goal_events;
                DROP TABLE chat_goal_outbox;
                """;
            await integrity.ExecuteNonQueryAsync();
            await Initialize(reopened);
            var legacy = await reopened.ReadBotWork(legacyId, alice.Id);
            Check(legacy is { Status: "completed", Goal: null, Input.Text: "Legacy task" }, "upgrade preserves legacy work without a continuous goal");
            integrity.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('chat_goal_inbox','chat_goal_events','chat_goal_outbox')";
            Check(Convert.ToInt32(await integrity.ExecuteScalarAsync()) == 3, "upgrade adds the continuous goal tables");
            Console.WriteLine("SQLite Chat checks passed: schema replay, accounts, rooms, messages, authorization, idempotency, settings, devices, password CAS, leases, restart, concurrency, notification queues, integrity/indexes.");
        }
        finally { Directory.Delete(dir, true); }
    }
    static async Task Initialize(ChatStore store)
    {
        await store.Initialize();
        await store.InitializeChannelAdministration();
        await store.InitializeGroupModeration();
        await store.InitializeChannelDiscussions();
    }
    static void Check(bool condition, string name) { if (!condition) throw new Exception("Check failed: " + name); }
}
