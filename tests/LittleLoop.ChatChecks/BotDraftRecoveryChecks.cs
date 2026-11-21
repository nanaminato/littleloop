using System.Text.Json;
using System.Threading.Channels;
using LittleLoop.Core;
using LittleLoop.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class WorkerChecks
{
    private static async Task CheckBotDraftRecovery(IConfiguration config, string project, Account owner, Action<bool, string> check)
    {
        using var fixture = new TemporaryChatDatabase();
        var clock = new TestClock();
        var store = new ChatStore(fixture.Connection, false, "Sqlite", clock);
        await store.Initialize();
        await store.InitializeChannelAdministration();
        await store.InitializeGroupModeration();
        await store.InitializeChannelDiscussions();
        await store.Register(owner);
        var robot = await store.GetRobot(ChatStore.BotName);
        var room = await store.CreateRoom(owner, new("direct", null, [robot.Name]));
        await store.InstallRobot(room.Id, owner.Id, [robot.Id], true);
        var model = new DraftModel();
        var harness = new ControlledHarness("codex", project);
        using var worker = new ChatBot(store, new QuietHub(), config, NullLogger<ChatBot>.Instance,
            new ModelBotDecisionEngine(model), [harness]);
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder(fixture.Connection) { Pooling = false }.ConnectionString);
        await db.OpenAsync();

        async Task Say(string text) => await store.Send(owner.Id, owner.Name, room.Id,
            new(Guid.NewGuid().ToString(), text, null), goalRobot: robot.Id);

        async Task<(Guid Id, DraftRequest Receipt)> StartGoal(string request)
        {
            await Say(request);
            (await model.Next()).Return("""
                {"action":"delegate","message":"I will inspect the report.","objective":"Inspect the report",
                 "harness":"codex","instruction":"Inspect and report the existing status."}
                """);
            (await harness.Next()).Return("Report found.");
            var receipt = await model.Next();
            using var body = JsonDocument.Parse(receipt.Input);
            return (body.RootElement.GetProperty("trigger").GetProperty("goalId").GetGuid(), receipt);
        }

        async Task<BotWork> UntilWork(Guid id, Func<BotWork, bool> matches)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            while (true)
            {
                if (await store.ReadBotWork(id, owner.Id) is { } work && matches(work)) return work;
                await Task.Delay(25, deadline.Token);
            }
        }

        async Task<bool> HasSupersededDraft(Guid id, string raw)
        {
            await using var query = db.CreateCommand();
            query.CommandText = """
                SELECT COUNT(*) FROM chat_bot_events WHERE work_id=$id AND kind='DecisionAudit'
                    AND json_extract(detail, '$.error.code')='goal-version-changed'
                    AND json_array_length(detail, '$.cognitionSource.modelCalls')=1
                    AND json_extract(detail, '$.cognitionSource.modelCalls[0].draft.raw')=$draft
                    AND json_extract(detail, '$.cognitionSource.modelCalls[0].error.code')='invalid-json'
                """;
            query.Parameters.AddWithValue("$id", id.ToString());
            query.Parameters.AddWithValue("$draft", raw);
            return Convert.ToInt64(await query.ExecuteScalarAsync()) == 1;
        }

        async Task<long> RetryAt(Guid id, int failures)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            while (true)
            {
                await using var query = db.CreateCommand();
                query.CommandText = """
                    SELECT retry_at FROM chat_bot_decision_recovery WHERE work_id=$id
                      AND json_extract(recovery, '$.failures')=$failures
                    """;
                query.Parameters.AddWithValue("$id", id.ToString());
                query.Parameters.AddWithValue("$failures", failures);
                if (await query.ExecuteScalarAsync() is long at) return at;
                await Task.Delay(25, deadline.Token);
            }
        }

        await worker.StartAsync(default);
        try
        {
            var (timed, first) = await StartGoal("Inspect the first report.");
            first.Timeout();
            long firstDue = await RetryAt(timed, 1);
            var waiting = await UntilWork(timed, work => work.Status == "deciding");
            clock.Advance(29_999);
            check(firstDue == clock.GetUtcNow().ToUnixTimeMilliseconds() + 1 &&
                waiting.Input.Loop is { ScheduledWakeups: 0, Wake: null } &&
                await store.NextBotInput(goal: true) is null && harness.Calls.Count == 1,
                "a transient cognition timeout delays the same trigger without consuming a goal wakeup or repeating execution");

            clock.Advance(1);
            var retry = await model.Next();
            using (var body = JsonDocument.Parse(retry.Input))
                check(body.RootElement.GetProperty("trigger").GetProperty("kind").GetString() == "harnessReceipt" &&
                    body.RootElement.GetProperty("decisionRecovery").GetProperty("failures").GetInt32() == 1,
                    "technical recovery re-presents the original Harness evidence with durable failure diagnostics");
            retry.Timeout();
            long secondDue = await RetryAt(timed, 2);
            clock.Advance(59_999);
            var reopened = new ChatStore(fixture.Connection, false, "Sqlite", clock);
            check(secondDue == clock.GetUtcNow().ToUnixTimeMilliseconds() + 1 &&
                await reopened.NextBotInput(goal: true) is null && harness.Calls.Count == 1,
                "a repeated transient failure persists exponential backoff without a user wait or another Harness call");
            clock.Advance(1);
            (await model.Next()).Return("""{"action":"wait","wakeAfterSeconds":5}""");
            var scheduled = await UntilWork(timed, work => work.Input.Loop?.Wake is not null);
            clock.Advance(5_000);
            (await model.Next()).Timeout();
            await RetryAt(timed, 1);
            check((await UntilWork(timed, work => work.Status == "waiting-input")).Input.Loop?.Wake == scheduled.Input.Loop!.Wake &&
                (await store.GoalObservations(timed)).All(observation => observation.Kind != BotEventKind.Timer),
                "a cognition outage at expiry preserves the original appointment and leaves it unconsumed");
            clock.Advance(30_000);
            var timerRetry = await model.Next();
            using (var body = JsonDocument.Parse(timerRetry.Input))
            {
                var trigger = body.RootElement.GetProperty("trigger");
                check(trigger.GetProperty("kind").GetString() == "timer" && trigger.GetProperty("wakeAtUnixMilliseconds").GetInt64() ==
                    scheduled.Input.Loop!.Wake!.AtUnixMilliseconds, "timer recovery carries the same business wake, timestamp and version");
            }
            timerRetry.Return("""{"action":"wait","message":"Which version of the report should I use?"}""");
            await UntilWork(timed, work => work.Status == "waiting-input" && work.Input.Loop?.Wake is null);
            check((await store.GoalObservations(timed)).Count(observation => observation.Kind == BotEventKind.Timer) == 1,
                "the recovered Bot decision consumes and records the business timer once");

            var (revised, stale) = await StartGoal("Inspect the second report.");
            await Say("Only compare the archived copy.");
            var input = await store.NextBotInput(goal: false) ?? throw new InvalidOperationException("Missing revision input.");
            var updated = await store.DecisionContext(input, robot.Id, true, "fixture", ["codex"], 3, true, null)
                ?? throw new InvalidOperationException("Missing revision snapshot.");
            var userRevision = new BotDecision(BotAction.Revise, revised, Instruction: "Only compare the archived copy.");
            check(await store.ApplyDecision(updated, userRevision.Validate(updated.Context), robot.Id, true, 3, "fixture") is null,
                "a new requirement commits while the old model request is in flight");

            const string invalid = """{"action":""";
            stale.Return(invalid);
            var current = await model.Next();
            using (var body = JsonDocument.Parse(current.Input))
            {
                var trigger = body.RootElement.GetProperty("trigger");
                check(trigger.GetProperty("kind").GetString() == "botDecision" &&
                    trigger.GetProperty("text").GetString() == userRevision.Instruction && await HasSupersededDraft(revised, invalid),
                    "the old malformed draft is durably audited and the next model request uses the revised goal");
            }
            current.Return("""{"action":"wait","message":"I have recorded the new requirement."}""");
            await UntilWork(revised, work => work.Status == "waiting-input");
            check(harness.Calls.Count == 2 && (await store.GoalObservations(revised)).All(e => e.Kind != BotEventKind.DecisionAudit),
                "superseded drafts neither dispatch nor leak into the ordinary goal observation stream");
        }
        finally { await worker.StopAsync(default); }
    }

    private sealed record DraftRequest(string Input, TaskCompletionSource<SemanticModelReply> Result)
    {
        public void Return(string draft) => Result.SetResult(new(draft, "fixture-model"));
        public void Timeout() => Result.SetException(new SemanticModelException("timeout"));
    }

    private sealed class DraftModel : ISemanticModelClient
    {
        private readonly Channel<DraftRequest> requests = Channel.CreateUnbounded<DraftRequest>();
        public string Provider => "fixture";
        public string Model => "fixture-model";
        public Task<DraftRequest> Next() => requests.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

        public async Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<SemanticModelReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            await requests.Writer.WriteAsync(new(input, completion), cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
    }
}
