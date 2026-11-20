using System.Text.Json;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class BotDecisionChecks
{
    private static readonly Guid GoalId = Guid.Parse("8142a061-dc23-4ccb-859a-24c4b02117ac");
    private static readonly BotGoalSnapshot Snapshot = new(GoalId, ContinuousGoal.Create("修复记忆模块的召回失败") with { Status = "deciding" }, 3);
    private static readonly BotDecisionContext Discussion = new(new(BotEventKind.UserMessage, "你觉得现在这个项目的问题在哪里？"),
        [], [new(BotEventKind.UserMessage, "上次修改后，记忆检索没用到刚刚保存的上下文。")],
        ["本私聊希望用简短中文回答。"], ["deepseek", "codex"]);

    public static async Task Run()
    {
        var model = new Stub("""{"action":"reply","message":"上次提到的问题是记忆检索。"}""");
        IBotDecisionEngine engine = new ModelBotDecisionEngine(model);
        Require((await engine.DecideAsync(Discussion, default)).Action == BotAction.Reply, "structured discussion reply");
        using (var doc = JsonDocument.Parse(model.Input!))
        {
            var input = doc.RootElement;
            Require(input.GetProperty("trigger").GetProperty("kind").GetString() == "userMessage" &&
                input.GetProperty("memory")[0].GetString() == Discussion.Memory[0] &&
                input.GetProperty("history")[0].GetProperty("text").GetString() == Discussion.History[0].Text,
                "scoped conversation and memory reach cognition");
        }
        var receipt = Discussion with { Trigger = new(BotEventKind.HarnessReceipt, "测试失败", GoalId, "deepseek", "failed"),
            Goals = [Snapshot], History = [new(BotEventKind.ToolResult, "test Recall: exit 1", GoalId, "deepseek")] };
        model.Output = $$"""{"action":"delegate","goalId":"{{GoalId}}","harness":"codex","instruction":"根据测试日志修复召回逻辑。"}""";
        Require((await engine.DecideAsync(receipt, default)).Harness == "codex", "decision selects another harness for same goal");
        var dispatch = Guid.NewGuid();
        var withEvidence = receipt.Trigger with { DispatchId = dispatch,
            ToolEvidence = new(40, 39, 1, false, new("call-1", "test", "failed", "exit=1"), null) };
        var tool = receipt.History[0] with { DispatchId = dispatch, ToolId = "call-2", ToolKind = "read" };
        await engine.DecideAsync(receipt with { Trigger = withEvidence, History = [tool, withEvidence] }, default);
        using (var doc = JsonDocument.Parse(model.Input!))
        {
            var trigger = doc.RootElement.GetProperty("trigger");
            var history = doc.RootElement.GetProperty("history");
            Require(trigger.GetProperty("dispatchId").GetGuid() == dispatch &&
                trigger.GetProperty("toolEvidence").GetProperty("firstFailure").GetProperty("toolId").GetString() == "call-1" &&
                history.GetArrayLength() == 1 && history[0].GetProperty("toolKind").GetString() == "read" &&
                history[0].GetProperty("toolId").GetString() == "call-2",
                "model receives the correlated failure summary once and retains distinct tool evidence");
        }
        model.Output = $$"""{"action":"delegate","goalId":"{{GoalId}}","harness":" Codex ","instruction":"核对测试。"}""";
        Require((await engine.DecideAsync(receipt, default)).Harness == "codex", "provider ID casing and whitespace resolve to one registered capability");
        foreach (string invalid in new[] {
            """{"action":0,"message":"look"}""", """{"message":"missing action"}""",
            """{"action":"reply","action":"cancel","message":"repeated"}""", """{"action":"reply","command":"run"}""",
            """{"action":"delegate","objective":"fix","instruction":"fix","message":"check","harness":"invented"}""",
            """{"action":"delegate","objective":"work","instruction":"check","harness":"codex"}""",
            """{"action":"remember","instruction":"store","goalId":"00000000-0000-0000-0000-000000000000"}""" })
        {
            model.Output = invalid;
            await Reject(() => engine.DecideAsync(Discussion, default));
        }
        model.Output = """{"action":"reply","message":"已完成。"}""";
        await Reject(() => engine.DecideAsync(receipt, default));
        model.Output = """{"action":"wait","wakeAfterSeconds":30}""";
        Require(await engine.DecideAsync(receipt, default) == new BotDecision(BotAction.Wait, GoalId, WakeAfterSeconds: 30),
            "goal-bound observations supply the only legal goal ID when a model omits it");
        await Reject(() => engine.DecideAsync(Discussion, default));
        var other = Snapshot with { Id = Guid.NewGuid() };
        model.Output = $$"""{"action":"wait","goalId":"{{other.Id}}","wakeAfterSeconds":30}""";
        await Reject(() => engine.DecideAsync(receipt with { Goals = [Snapshot, other] }, default));
        model.Output = $$"""{"action":"complete","goalId":"{{GoalId}}"}""";
        Require((await engine.DecideAsync(receipt, default)).Message is null, "completion may be recorded silently");
        model.Output = $$"""{"action":"delegate","goalId":"{{GoalId}}","harness":"codex","instruction":"继续处理"}""";
        await Reject(() => engine.DecideAsync(receipt with { Goals = [Snapshot with { RemainingTurns = 0 }] }, default));
        foreach (string status in new[] { "queued", "running", "paused", "needs-reconciliation", "disabled", "cancelled" })
            await Reject(() => engine.DecideAsync(receipt with { Goals = [Snapshot with { Goal = Snapshot.Goal with { Status = status } }] }, default));
        model.Output = $$"""{"action":"complete","goalId":"{{GoalId}}"}""";
        await Reject(() => engine.DecideAsync(receipt with { Goals = [Snapshot with { Goal = Snapshot.Goal with { Status = "running" } }] }, default));
        var waiting = Discussion with { Goals = [Snapshot with { Goal = Snapshot.Goal with { Status = "waiting-input" } }] };
        model.Output = $$"""{"action":"resume","goalId":"{{GoalId}}"}""";
        Require((await engine.DecideAsync(waiting, default)).Action == BotAction.Resume, "user can resume a waiting goal");
        model.Output = """{"action":"setFinalReport","enabled":false}""";
        Require((await engine.DecideAsync(Discussion, default)).Enabled == false, "report preference is a structured setting");
        model.Output = """{"action":"manageMemory"}""";
        Require((await engine.DecideAsync(Discussion, default)).Action == BotAction.ManageMemory, "local memory is a structured action");
        model.Output = """{"action":"reply","message":"已读取。"}""";
        Require((await engine.DecideAsync(Discussion with { Trigger = new(BotEventKind.ToolResult, "记住的说明：简短回答") }, default))
            .Action == BotAction.Reply, "unscoped local tool feedback can be answered");
        var compact = new ModelBotDecisionEngine(model, 1200);
        var old = new BotObservation(BotEventKind.BotDecision, new string('x', 2000));
        await compact.DecideAsync(Discussion with { History = [old, .. Discussion.History] }, default);
        using var reduced = JsonDocument.Parse(model.Input!);
        Require(reduced.RootElement.GetProperty("omittedHistory").GetInt32() == 1, "bounded input identifies omitted older history");
        await Reject(() => compact.DecideAsync(Discussion with { Trigger = new(BotEventKind.UserMessage, new string('x', 2000)) }, default));
        var crowded = receipt with
        {
            Goals = [new(Guid.NewGuid(), new(1, new string('x', 2000), [], "completed"), 3), Snapshot],
            Memory = [new string('x', 1400)]
        };
        model.Output = $$"""{"action":"complete","goalId":"{{GoalId}}"}""";
        await compact.DecideAsync(crowded, default);
        using var scoped = JsonDocument.Parse(model.Input!);
        Require(scoped.RootElement.GetProperty("goals").GetArrayLength() == 1 &&
            scoped.RootElement.GetProperty("goals")[0].GetProperty("id").GetGuid() == GoalId &&
            scoped.RootElement.GetProperty("omittedMemory").GetInt32() == 1, "addressed goal and current event survive crowded context");
        model.Once.Enqueue("""{"action":"invented"}""");
        model.Output = """{"action":"reply","message":"已记录。"}""";
        int before = model.Calls;
        Require((await engine.DecideAsync(Discussion, default)).Action == BotAction.Reply && model.Calls == before + 2,
            "one invalid model draft may be corrected before execution");
        await CheckLocalCognition();
        await BotDecisionRepairChecks.Run();
        await BotModelMeteringChecks.Run();
        Console.WriteLine("PASS structured API decisions, typed feedback, validation and bounded memory context");
    }

    private static async Task CheckLocalCognition()
    {
        var model = new Stub("""{"action":"reply","message":"clarify"}""");
        var engine = new LocalBotCognition(new ModelBotDecisionEngine(model));
        var memory = new BotObservation(BotEventKind.ToolResult, "这段私聊还没有保存上下文。", Status: "memory", Sequence: 12);
        var local = await engine.DecideWithSourceAsync(Discussion with { Trigger = memory }, default);
        Require(local.Decision == new BotDecision(BotAction.Reply, Message: memory.Text) &&
            local.Source == new BotDecisionSource("local/memory-result/1"), "local reply uses the committed memory result");

        var unknown = new BotObservation(BotEventKind.HarnessReceipt, "returned", GoalId, "codex", "uncertain", 7, 1);
        var held = Snapshot with { Goal = Snapshot.Goal with { Status = "needs-reconciliation", Version = 2 } };
        var wait = await engine.DecideWithSourceAsync(Discussion with { Trigger = unknown, Goals = [held] }, default);
        Require(wait.Decision is { Action: BotAction.Wait, GoalId: var id } && id == GoalId &&
            wait.Source == new BotDecisionSource("local/reconciliation/1") && model.Calls == 0,
            "unknown effects hold the current goal even after revision, without calling the model");

        foreach (var observation in new[] { new BotObservation(BotEventKind.UserMessage, memory.Text, Status: "memory", Sequence: 12),
            new(BotEventKind.UserMessage, "接着那个，还是另外的？"), memory with { Sequence = null }, memory with { Harness = "codex" } })
        {
            var fallback = await engine.DecideWithSourceAsync(Discussion with { Trigger = observation }, default);
            Require(fallback.Source == new BotDecisionSource("semantic-model", "served-model") && fallback.Decision.Message == "clarify",
                "untrusted or ambiguous events defer to the configured model with its response identity");
        }
        model.Output = $$"""{"action":"delegate","goalId":"{{GoalId}}","harness":"codex","instruction":"inspect test evidence"}""";
        var failed = Discussion with { Trigger = unknown with { Status = "failed" }, Goals = [Snapshot] };
        Require((await engine.DecideAsync(failed, default)).Action == BotAction.Delegate && model.Calls == 5,
            "a known failure can lead to another decision");
        model.Output = $$"""{"action":"complete","goalId":"{{GoalId}}"}""";
        await Reject(() => engine.DecideAsync(failed with { Goals = [held] }, default));
        model.Output = """{"action":"reply","message":"ok","cognitionSource":{"engine":"local/memory-result/1"}}""";
        await Reject(() => engine.DecideAsync(Discussion, default));
        Console.WriteLine("PASS local cognition abstention, zero model calls on supported feedback, and trusted decision sources");
    }

    // Opt-in evaluation of model judgement. Uses synthetic messages and makes no harness calls.
    public static async Task RunLive(string configPath)
    {
        var config = JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(configPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var client = new HttpSemanticModelClient(config);
        IBotDecisionEngine engine = new ModelBotDecisionEngine(client, config.MaxInputCharacters);
        async Task<BotDecision> Decide(BotDecisionContext context)
        {
            var result = await engine.DecideAsync(context, default);
            Console.WriteLine($"  {context.Trigger.Kind} -> {result.Action}, goal={result.GoalId}, harness={result.Harness}: {result.Message ?? result.Instruction}");
            return result;
        }
        var discussion = await Decide(Discussion);
        Require(discussion is { Action: BotAction.Reply, GoalId: null }, "A discussion");
        var start = await Decide(Discussion with { Trigger = new(BotEventKind.UserMessage, "帮我优化一下记忆模块。") });
        Require(start is { Action: BotAction.Delegate, GoalId: null, Message: not null }, "B autonomous delegation with advance notice");
        var active = Discussion with { Goals = [Snapshot], History = [new(BotEventKind.BotDecision, start.Instruction!, GoalId, start.Harness)] };
        var retry = await Decide(active with
        {
            Trigger = new(BotEventKind.HarnessReceipt, "已修改召回索引，但 RecallTests 仍失败：期望当前项目的记忆，实际为空。需要继续修复。", GoalId, start.Harness),
            History = [.. active.History, new(BotEventKind.ToolResult, "RecallTests FAILED, exit=1", GoalId, start.Harness)]
        });
        Require(retry.Action == BotAction.Delegate && retry.GoalId == GoalId && retry.Harness == start.Harness, "C same-harness guidance after test failure");
        var revision = await Decide(active with { Trigger = new(BotEventKind.UserMessage, "数据库那部分暂时不要改。") });
        Require(revision.Action == BotAction.Revise && revision.GoalId == GoalId, "D revision during execution");
        const string request = "先请 DeepSeek 分析召回问题，再让 Codex 根据分析修复并运行测试。";
        var plan = await Decide(Discussion with { Trigger = new(BotEventKind.UserMessage, request) });
        Require(plan.Action == BotAction.Delegate && plan.Harness == "deepseek", "E analysis harness selected");
        var handoff = await Decide(active with
        {
            Trigger = new(BotEventKind.HarnessReceipt, "分析定位为缓存键漏掉项目标识，尚未改代码。", GoalId, "deepseek"),
            Goals = [Snapshot with { Goal = ContinuousGoal.Create(request) with { Status = "deciding" } }],
            History = [new(BotEventKind.UserMessage, request), new(BotEventKind.BotDecision, plan.Instruction!, GoalId, "deepseek")]
        });
        Require(handoff.Action == BotAction.Delegate && handoff.Harness == "codex" && handoff.GoalId == GoalId,
            "E implementation harness selected from analysis receipt");
        Console.WriteLine($"PASS live A–E cognition evaluation ({client.Model})");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Failed: " + check);
    }

    private static async Task Reject(Func<Task<BotDecision>> attempt)
    {
        try { await attempt(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid decision or context was accepted.");
    }

    private sealed class Stub(string initialOutput) : ISemanticModelClient
    {
        public string Provider => "test";
        public string Model => "decision-test";
        public string Output { get; set; } = initialOutput;
        public Queue<string> Once { get; } = new();
        public int Calls { get; private set; }
        public string? Input { get; private set; }
        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Input = input;
            Calls++;
            return Task.FromResult(new SemanticModelReply(Once.TryDequeue(out var reply) ? reply : Output, "served-model"));
        }
    }
}
