using LittleLoop.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class WorkerChecks
{
    private static async Task CheckHarnessRecovery(IConfiguration config, string project, Account owner, Action<bool, string> check)
    {
        using var fixture = new TemporaryChatDatabase();
        ChatStore Open() => new(fixture.Connection, false, "Sqlite");
        var store = Open();
        await store.Initialize();
        await store.InitializeChannelAdministration();
        await store.InitializeGroupModeration();
        await store.InitializeChannelDiscussions();
        await store.Register(owner);
        var robot = await store.GetRobot(ChatStore.BotName);
        var room = await store.CreateRoom(owner, new("direct", null, [robot.Name]));
        await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);

        string[] names = ["remote", "never-sent", "unreachable", "removed", "deepseek", "swapped", "codex", "native-missing", "native-opening", "send-throws"];
        var claimed = new Dictionary<string, BotWork>();
        await using (var lease = await store.AcquireBotLease())
        {
            foreach (var name in names)
            {
                await store.Send(owner.Id, owner.Name, room.Id, new(Guid.NewGuid().ToString(), name, null), goalRobot: robot.Id);
                var input = await store.NextBotInput(goal: false) ?? throw new Exception("Missing input");
                var snapshot = await store.DecisionContext(input, robot.Id, true, "fixture", names, 2, true, null)
                    ?? throw new Exception("Missing snapshot");
                var plan = new BotDecision(BotAction.Delegate, Objective: name, Message: "检查项目", Instruction: name, Harness: name);
                await store.ApplyDecision(snapshot, plan.Validate(snapshot.Context), robot.Id, true, 2, "fixture");
                while (await store.NextGoalNotice() is { } notice) await store.CompleteGoalNotice(notice.Id);
                claimed[name] = await store.ClaimBotWork(lease, recoveryKey: id => id switch
                {
                    "codex" or "deepseek" => null,
                    "native-missing" or "native-opening" => NativeBotHarness.JournalKey,
                    _ => "remote-ledger/" + id
                })
                    ?? throw new Exception("Missing dispatch");
            }
        }

        // The completed remote turn lives in the provider's records. Nothing was written to its local session folder.
        Guid Dispatch(string name) => claimed[name].Input.Loop!.Dispatch!.Id;
        var completed = new Dictionary<Guid, HarnessReceipt>
        {
            [Dispatch("remote")] = new("returned", "remote-session", "external ledger", [new("read", "file", "completed", "alpha")])
        };
        var remote = new RecoveryHarness("remote", project, id => HarnessRecovery.FromReceipt(completed[id]));
        var neverSent = new RecoveryHarness("never-sent", project, _ => HarnessRecovery.NotSubmitted);
        var unreachable = new RecoveryHarness("unreachable", project, _ => throw new IOException("service unavailable"));
        var lostReply = new RecoveryHarness("send-throws", project, _ => HarnessRecovery.NotSubmitted, throwsAfterSend: true);
        var swapped = new ReplacementNativeHarness("swapped", project);
        var legacyWithoutJournal = new ReplacementNativeHarness("codex", project);
        var missing = new ReplacementNativeHarness("native-missing", project);
        var opening = new ReplacementNativeHarness("native-opening", project);

        store = Open();
        var engine = new ControlledEngine();
        using var worker = new ChatBot(store, new QuietHub(), config, NullLogger<ChatBot>.Instance, engine,
            [remote, neverSent, unreachable, swapped, legacyWithoutJournal, missing, opening, lostReply]);
        string legacy = Directory.CreateDirectory(Path.Combine(worker.Folder(claimed["deepseek"]), "harnesses", "deepseek")).FullName;
        new HarnessTurn(legacy, Dispatch("deepseek"), null).Finish("returned", "native journal");
        string opened = Directory.CreateDirectory(Path.Combine(worker.Folder(claimed["native-opening"]), "harnesses", "native-opening")).FullName;
        new HarnessTurn(opened, Dispatch("native-opening"), null).Opening();
        bool IsUncertain(string name) => name is "unreachable" or "removed" or "swapped" or "codex" or "native-missing" or "send-throws";
        await worker.StartAsync(default);
        try
        {
            var reviewed = new HashSet<Guid>();
            while (reviewed.Count < names.Length)
            {
                var request = await engine.Next();
                var trigger = request.Context.Trigger;
                if (trigger is not { Kind: BotEventKind.HarnessReceipt, GoalId: Guid id, Harness: { } name })
                    throw new Exception("Recovered execution must return to cognition as a receipt");
                var unknown = IsUncertain(name);
                check(claimed[name].Id == id && trigger.Status == (unknown ? "uncertain" : "returned"),
                    "startup feeds the recovered " + name + " dispatch to its original goal");
                if (name == "remote")
                    check(trigger.Text == "external ledger" && request.Context.History.Any(e =>
                        e.GoalId == id && e.Kind == BotEventKind.ToolResult && e.Text.Contains("alpha")),
                        "a non-file recovery adapter returns its own receipt and tool evidence");
                if (name == "deepseek") check(trigger.Text == "native journal", "a removed native adapter retains readable terminal evidence");
                request.Choose(new(unknown ? BotAction.Wait : BotAction.Complete, id));
                reviewed.Add(id);
            }
            using var deadline = new CancellationTokenSource(Timeout);
            foreach (var (name, work) in claimed)
            {
                while ((await store.ReadBotWork(work.Id, owner.Id))?.Input.Loop?.Next is not null)
                    await Task.Delay(25, deadline.Token);
                string status = IsUncertain(name) ? "needs-reconciliation" : "completed";
                check((await store.ReadBotWork(work.Id, owner.Id)) is { Input.Loop.Used: 1 } saved && saved.Status == status &&
                    (await store.GoalObservations(work.Id)).Count(e => e.Kind == BotEventKind.HarnessReceipt) == 1,
                    "settlement preserves one budget charge, one receipt and the reviewed state for " + name);
            }
            check(remote.Recoveries.SequenceEqual([Dispatch("remote")]) && remote.Sends.Count == 0 &&
                unreachable.Recoveries.SequenceEqual([Dispatch("unreachable")]) && unreachable.Sends.Count == 0 &&
                neverSent.Recoveries.SequenceEqual([Dispatch("never-sent")]) && neverSent.Sends.SequenceEqual([Dispatch("never-sent")]) &&
                (await store.InterruptedDispatches()).Length == 0, "only a proven unsubmitted dispatch is sent, once with its original identity");
            check(swapped.Sends == 0 && legacyWithoutJournal.Sends == 0,
                "a same-name native replacement cannot treat its empty journal as proof another adapter never sent a dispatch");
            check(missing.Sends == 0 && opening.Sends == 1,
                "claimed native dispatches require a pre-submission journal entry to retry after restart");
            check(lostReply.Sends.SequenceEqual([Dispatch("send-throws")]),
                "an exception after entering a custom SendAsync preserves unknown effects without a second send");
        }
        finally { await worker.StopAsync(default); }
    }

    private sealed class RecoveryHarness(string name, string project, Func<Guid, HarnessRecovery> recover, bool throwsAfterSend = false) : IBotHarness
    {
        public string Name => name;
        public string RecoveryKey => "remote-ledger/" + name;
        public string ProjectRoot => project;
        public List<Guid> Recoveries { get; } = [];
        public List<Guid> Sends { get; } = [];
        public Task<HarnessRecovery> RecoverAsync(string sessionDirectory, Guid dispatchId, CancellationToken token)
        {
            Recoveries.Add(dispatchId);
            return Task.FromResult(recover(dispatchId));
        }
        public Task<HarnessReceipt> SendAsync(string sessionDirectory, Guid dispatchId, string instruction,
            Func<CancellationToken, Task<bool>>? shouldInterrupt, CancellationToken token, string? workspace = null)
        {
            Sends.Add(dispatchId);
            if (throwsAfterSend) throw new IOException("lost connection after submission");
            return Task.FromResult(new HarnessReceipt("returned", "fresh-session", "executed", []));
        }
        public HarnessRunState GetStatus(string sessionDirectory, Guid dispatchId) => throw new NotSupportedException();
        public void Interrupt(string sessionDirectory, Guid dispatchId) => throw new NotSupportedException();
    }

    private sealed class ReplacementNativeHarness(string name, string project) : NativeBotHarness
    {
        public override string Name => name;
        public override string ProjectRoot => project;
        public int Sends { get; private set; }
        protected override Task<HarnessReceipt> Run(string directory, string workspace, bool isolated,
            string instruction, HarnessTurn turn, CancellationToken token)
        {
            Sends++;
            return Task.FromResult(turn.Finish("returned", "replacement ran"));
        }
    }
}
