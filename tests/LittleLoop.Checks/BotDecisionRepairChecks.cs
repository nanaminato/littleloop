using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class BotDecisionRepairChecks
{
    private static readonly Guid Goal = Guid.Parse("17afaee0-c623-473e-b2ab-59cca3108fde");
    private static readonly Guid OtherGoal = Guid.Parse("b77c280a-af0d-4736-91cf-883fd0528092");
    private static readonly Guid Dispatch = Guid.Parse("4652bf2a-8e50-430c-a665-6b94ce5b65f7");
    private static readonly BotGoalSnapshot Target = new(Goal,
        ContinuousGoal.Create("Review the report, then schedule a follow-up.") with { Version = 5, Status = "deciding" }, 4);
    private static readonly BotDecisionContext Receipt = new(
        new(BotEventKind.HarnessReceipt, "Report received.", Goal, "reader", "returned", 20, 5, Dispatch),
        [Target, Target with { Id = OtherGoal }], [], [], ["reader", "verifier"]);
    private const string ContinueReview = """{"action":"delegate","harness":"verifier","instruction":"Check the report from a separate source."}""";
    private const string TimedWait = """{"action":"wait","wakeAfterSeconds":30}""";
    private const string Ask = """{"action":"wait","message":"Please confirm the status."}""";

    public static async Task Run()
    {
        // Historical logs contain error outcomes but no original drafts. These fixtures isolate their possible validator branches.
        await Correction(Receipt, """{"action":"delegate","harness":"verifier","instruction":"Review again","wakeAfterSeconds":30}""",
            "wake-requires-wait", "field", "action", TimedWait, BotAction.Wait);
        await Correction(Receipt, """{"action":"wait","wakeAfterSeconds":604801}""",
            "wake-out-of-range", "field", "wakeAfterSeconds", TimedWait, BotAction.Wait);
        await Correction(Receipt, $$"""{"action":"wait","goalId":"{{OtherGoal}}","wakeAfterSeconds":30}""",
            "wrong-trigger-goal", "field", "goalId", TimedWait, BotAction.Wait);
        await Correction(Receipt, $$"""{"action":"reply","goalId":"{{Goal}}","message":"Done"}""",
            "invalid-trigger-action", "field", "action", Ask, BotAction.Wait);
        await Correction(Receipt, """{"action":"delegate","harness":"reader-or-verifier","instruction":"Check the report"}""",
            "unknown-harness", "harness-reference", "harness", ContinueReview, BotAction.Delegate);
        await Correction(Receipt, """{"action":"delegate","instruction":"Check the report"}""",
            "missing-harness", "harness-reference", "harness", ContinueReview, BotAction.Delegate);
        await CheckSchemaFeedback();

        await Correction(Receipt with { Goals = [Target with { RemainingTurns = 0 }] }, ContinueReview,
            "turn-budget-exhausted", "field", "action", Ask, BotAction.Wait, delegateAllowed: false);
        await Correction(Receipt with { Goals = [Target with { RemainingWakeups = 0 }] }, TimedWait,
            "wake-not-available", "field", "wakeAfterSeconds", Ask, BotAction.Wait);
        await Correction(Receipt with
        {
            Trigger = Receipt.Trigger with { Status = "uncertain", Text = "Submission cannot be confirmed." },
            Goals = [Target with { Goal = Target.Goal with { Status = "needs-reconciliation" } }]
        }, """{"action":"complete","message":"Claim success"}""",
            "execution-unknown", "evidence-conflict", "action", Ask, BotAction.Wait, delegateAllowed: false);

        const string fenced = """
            ```json
            {"action":"delegate","harness":" VERIFIER ","instruction":"Check a separate source."}
            ```
            """;
        var (normalized, client, calls) = await Observe(Receipt, fenced);
        var call = calls.Single();
        Require(normalized is { Action: BotAction.Delegate, GoalId: not null, Harness: "verifier" } &&
            normalized.GoalId == Goal && client.Inputs.Count == 1 && call.Error is null,
            "a single Markdown envelope, unique ID spelling and trigger-bound GoalId normalize without a second request");
        var draft = call.Draft;
        Require(draft is not null && draft.Raw == fenced && draft.Characters == fenced.Length &&
            draft.Repairs.Any(r => r.Change == "removed-single-json-fence") &&
            draft.Repairs.Any(r => r.Field == "harness") && draft.Repairs.Any(r => r.Field == "goalId"),
            "normalized decision preserves the unchanged original and individual repair records");

        var (explicitChoice, _, ambiguous) = await Observe(Receipt with { Harnesses = ["verifier", "Verifier"] },
            """{"action":"delegate","harness":" VERIFIER ","instruction":"Check a separate source."}""", ContinueReview);
        Require(ambiguous[0] is { Error.Code: "unknown-harness", Draft: { } ambiguousDraft } &&
            !ambiguousDraft.Repairs.Any(r => r.Field == "harness") && explicitChoice.Harness == "verifier",
            "ambiguous registered spellings require an explicit model choice");

        var malformed = new Script(fenced + "\nAdditional prose is not part of the envelope.", Ask);
        var malformedCalls = new List<BotModelCall>();
        await new ModelBotDecisionEngine(malformed).DecideAsync(Receipt with { ObserveModelCall = malformedCalls.Add }, default);
        Require(malformedCalls.Count == 2 && malformedCalls[0].Outcome == "invalid-decision" &&
            malformedCalls[0].Error is { Code: "invalid-json", Category: "json" },
            "a response that cannot be safely unwrapped remains a structured JSON/format rejection");

        string large = new('x', 65537);
        var (_, _, oversized) = await Observe(Receipt, large, Ask);
        Require(oversized[0] is { Error.Code: "draft-too-large", Draft: { Raw: null } limit } &&
            limit.Characters == large.Length &&
            string.Equals(limit.Sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(large))),
                StringComparison.OrdinalIgnoreCase),
            "an over-limit draft records its size and digest without storing an unbounded response");

        var repeated = new Script("""{"action":"delegate","harness":"unknown","instruction":"Do it"}""",
            """{"action":"delegate","harness":"unknown","instruction":"Do it"}""", ContinueReview);
        var repeatedCalls = new List<BotModelCall>();
        await Reject(() => new ModelBotDecisionEngine(repeated, maxFormatAttempts: 2)
            .DecideAsync(Receipt with { ObserveModelCall = repeatedCalls.Add }, default));
        Require(repeated.Inputs.Count == 2 && repeatedCalls.Count == 2 &&
            repeatedCalls.All(c => c is { Error.Code: "unknown-harness", Draft.Raw: not null }) &&
            repeatedCalls[0].PriorSameError == 0 && repeatedCalls[1].PriorSameError == 1,
            "attempt budget keeps all rejected drafts and does not dispatch a third");

        var stale = new BotDecisionError("goal-version-changed", "goal-version-conflict", "The goal has been revised.", "goalId");
        var revised = new Script("""{"action":"wait","wakeAfterSeconds":0}""", TimedWait);
        var staleCalls = new List<BotModelCall>();
        var staleContext = Receipt with
        {
            ObserveModelCall = staleCalls.Add,
            CheckFreshness = _ => Task.FromResult<BotDecisionError?>(revised.Inputs.Count == 0 ? null : stale)
        };
        await Superseded(() => new ModelBotDecisionEngine(revised).DecideAsync(staleContext, default), stale);
        Require(revised.Inputs.Count == 1 && staleCalls.Count == 1 && staleCalls[0].Draft?.Raw is not null,
            "a revision between drafts aborts repair and keeps the first response without requesting a stale second draft");

        var denied = new BotDecisionError("room-access-changed", "permission-denied", "The caller no longer has access.");
        var restricted = new Script("""{"action":"delegate","harness":"unknown","instruction":"Do it"}""", ContinueReview);
        await Superseded(() => new ModelBotDecisionEngine(restricted).DecideAsync(
            Receipt with { CheckFreshness = _ => Task.FromResult<BotDecisionError?>(denied) }, default), denied);
        Require(restricted.Inputs.Count == 1, "permission refusal is terminal for the snapshot before attempting repair");
        Console.WriteLine("PASS deterministic Bot draft repair, evidence safety, stale snapshots and metered retry bounds");
    }

    private static async Task CheckSchemaFeedback()
    {
        const string unexpectedField = """{"action":"wait","obligationEdits":[{"action":"withdraw","oldId":"some-id"}]}""";
        var (_, client, audit) = await Observe(Receipt, unexpectedField, Ask);
        var unknown = audit[0].Error;
        var feedback = JsonDocument.Parse(client.Prompts[1][(client.Prompts[1].LastIndexOf('\n') + 1)..]);
        using (feedback)
        {
            var error = feedback.RootElement.GetProperty("error");
            Require(unknown is { Code: "unknown-field", Field: "obligationEdits[0].oldId", Actual: "\"some-id\"" } &&
                unknown.Expected?.Contains("id?: UUID string", StringComparison.Ordinal) == true &&
                error.GetProperty("actual").GetString() == unknown.Actual &&
                feedback.RootElement.GetProperty("goal").GetProperty("version").GetInt32() == 5,
                "nested unknown field reports its exact path, actual value, allowed shape and goal version");
        }

        var (_, _, wrongType) = await Observe(Receipt,
            """{"action":"wait","obligationAssessments":[{"id":123,"status":"unknown","reason":"no evidence"}]}""", Ask);
        Require(wrongType[0].Error is
            { Code: "invalid-field-type", Field: "obligationAssessments[0].id", Expected: "UUID string", Actual: "123" },
            "an incorrect field type reports the containing array index");

        var (_, _, missing) = await Observe(Receipt, $$"""
            {"action":"wait","obligationAssessments":[{"id":"{{Goal}}","status":"unknown"}]}
            """, Ask);
        Require(missing[0].Error is
            { Code: "missing-required-field", Field: "obligationAssessments[0].reason", Expected: "string", Actual: "<missing>" },
            "a missing required member reports its exact path");

        var (_, _, duplicate) = await Observe(Receipt, """{"action":"wait","message":"one","message":"two"}""", Ask);
        Require(duplicate[0].Error is
            { Code: "duplicate-field", Field: "message", Expected: "one string", Actual: "\"two\"" },
            "duplicate fields identify the conflicting field and second value");
    }

    private static async Task Correction(BotDecisionContext context, string original, string code, string category, string field,
        string corrected, BotAction expected, bool delegateAllowed = true)
    {
        var (action, client, audit) = await Observe(context, original, corrected);
        Require(action.Action == expected && action.GoalId == Goal && client.Inputs.Count == 2,
            $"{code}: model selects the accepted correction for the same goal");
        Require(audit[0].Error is { } error && (error.Code, error.Category, error.Field) == (code, category, field) &&
            audit[0].Draft?.Raw == original && audit[1].Error is null && audit[1].Draft?.Raw == corrected,
            $"{code}: structured error and both original drafts are retained");
        string correctionPrompt = client.Prompts[1];
        using var doc = JsonDocument.Parse(correctionPrompt[(correctionPrompt.LastIndexOf('\n') + 1)..]);
        var feedback = doc.RootElement;
        var actions = feedback.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()).ToArray();
        var wakeSeconds = feedback.GetProperty("wakeSeconds");
        var scope = context.Goals.Single(goal => goal.Id == Goal);
        bool maySchedule = scope.Goal.Status is "deciding" or "waiting-input" && scope.RemainingWakeups > 0;
        bool wakeCorrect = maySchedule
            ? wakeSeconds.ValueKind == JsonValueKind.Object && wakeSeconds.GetProperty("min").GetInt32() == BotDecision.MinWakeSeconds &&
                wakeSeconds.GetProperty("max").GetInt32() == BotDecision.MaxWakeSeconds
            : wakeSeconds.ValueKind == JsonValueKind.Null;
        Require(feedback.GetProperty("error").GetProperty("code").GetString() == code &&
            feedback.GetProperty("allowedGoalId").GetGuid() == Goal &&
            feedback.GetProperty("harnessIds").EnumerateArray().Select(id => id.GetString()).SequenceEqual(["reader", "verifier"]) &&
            actions.Contains("wait", StringComparer.OrdinalIgnoreCase) &&
            actions.Contains("delegate", StringComparer.OrdinalIgnoreCase) == delegateAllowed && wakeCorrect,
            $"{code}: retry receives the error and current legal goal, actions, Harness IDs and timer bounds");
    }

    private static async Task<(BotDecision Decision, Script Client, List<BotModelCall> Audit)> Observe(
        BotDecisionContext context, params string[] drafts)
    {
        var model = new Script(drafts);
        var audit = new List<BotModelCall>();
        var result = await new ModelBotDecisionEngine(model).DecideAsync(context with { ObserveModelCall = audit.Add }, default);
        return (result, model, audit);
    }

    private static async Task Reject(Func<Task<BotDecision>> decide)
    {
        try { await decide(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected the decision to be rejected.");
    }

    private static async Task Superseded(Func<Task<BotDecision>> decide, BotDecisionError error)
    {
        try { await decide(); }
        catch (BotDecisionSupersededException superseded) when (superseded.Error == error) { return; }
        throw new InvalidOperationException("Expected the original snapshot to be rejected.");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + check);
    }

    private sealed class Script(params string[] drafts) : ISemanticModelClient
    {
        public string Provider => "test";
        public string Model => "decision-repair-fixture";
        public List<string> Inputs { get; } = [];
        public List<string> Prompts { get; } = [];

        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = drafts[Inputs.Count];
            Inputs.Add(input);
            Prompts.Add(systemPrompt);
            return Task.FromResult(new SemanticModelReply(draft, Model));
        }
    }
}
