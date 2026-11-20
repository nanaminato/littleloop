using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using LittleLoop.Core;
using LittleLoop.Tools;

internal static class LocalCognitionEvaluation
{
    private static readonly Guid Goal = Guid.Parse("424f6e4f-883c-46ab-8d3a-6ea2c12f4729");
    private static readonly Guid Dispatch = Guid.Parse("a5afa0b2-c5dc-402a-9112-f0f4de957c86");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Trial(string Name, string Boundary, BotDecisionContext Evidence, string? ExpectedLocalSource,
        BotDecision FixtureDecision, Func<BotDecision, bool> Accept);
    private sealed record Observation(BotDecision? Decision, BotDecisionSource? Source, int ModelRequests, int ExternalApiAttempts,
        bool Correct, string? Error);
    private sealed record Comparison(Trial Trial, Observation Hybrid, Observation ModelOnly);

    public static async Task Run(string? output, string? modelConfig = null)
    {
        var config = modelConfig is null ? null : JsonSerializer.Deserialize<SemanticModelConfig>(File.ReadAllText(modelConfig), Json)
            ?? throw new InvalidDataException("Missing model configuration.");
        using var client = config is null ? null : new HttpSemanticModelClient(config);
        var observations = new List<Comparison>();
        foreach (var trial in Trials())
        {
            var hybrid = await Observe(trial, client, config?.MaxInputCharacters ?? 32768, useLocal: true);
            var baseline = await Observe(trial, client, config?.MaxInputCharacters ?? 32768, useLocal: false);
            observations.Add(new(trial, hybrid, baseline));
        }

        var local = observations.Where(o => IsLocal(o.Hybrid)).ToArray();
        static object Score(IEnumerable<Observation> observations)
        {
            var results = observations.ToArray();
            int errors = results.Count(o => !o.Correct);
            return new
            {
                cases = results.Length, errors, errorRate = results.Length == 0 ? (double?)null : (double)errors / results.Length,
                missingDecisions = results.Count(o => o.Decision is null),
                modelRequests = results.Sum(o => o.ModelRequests), externalApiAttempts = results.Sum(o => o.ExternalApiAttempts)
            };
        }
        static bool Comparable(Comparison o) => o.Hybrid.Decision is not null && o.ModelOnly.Decision is not null;
        static bool DifferentAction(Comparison o) => Comparable(o) &&
            (o.Hybrid.Decision!.Action, o.Hybrid.Decision.GoalId, o.Hybrid.Decision.Harness) !=
            (o.ModelOnly.Decision!.Action, o.ModelOnly.Decision.GoalId, o.ModelOnly.Decision.Harness);
        var report = new
        {
            baselineType = client is null ? "controlled-model-response" : "live-model-api",
            configuredModel = client?.Model,
            summary = new
            {
                hybrid = Score(observations.Select(o => o.Hybrid)),
                modelOnly = Score(observations.Select(o => o.ModelOnly)),
                localHandled = Score(local.Select(o => o.Hybrid)),
                byExpectedLocalSource = observations.Where(o => o.Trial.ExpectedLocalSource is not null)
                    .GroupBy(o => o.Trial.ExpectedLocalSource!)
                    .ToDictionary(g => g.Key, g => new
                    {
                        hybrid = Score(g.Select(o => o.Hybrid)), modelOnly = Score(g.Select(o => o.ModelOnly))
                    }),
                abstained = observations.Count - local.Length,
                routingErrors = observations.Count(o => o.Trial.ExpectedLocalSource is { } source
                    ? o.Hybrid.Source?.Engine != source : IsLocal(o.Hybrid)),
                comparable = observations.Count(Comparable),
                actionDifferences = observations.Count(DifferentAction),
                fullDecisionDifferences = observations.Count(o => Comparable(o) && o.Hybrid.Decision != o.ModelOnly.Decision)
            },
            cases = observations.Select(o => new
            {
                o.Trial.Name, o.Trial.Boundary, o.Trial.Evidence,
                expectedAction = o.Trial.FixtureDecision.Action,
                o.Trial.ExpectedLocalSource, o.Hybrid, o.ModelOnly,
                actionDiffers = Comparable(o) ? DifferentAction(o) : (bool?)null,
                fullDecisionDiffers = Comparable(o) ? o.Hybrid.Decision != o.ModelOnly.Decision : (bool?)null
            })
        };
        if (output is not null) File.WriteAllText(output, JsonSerializer.Serialize(report, Json));
        if (report.summary.routingErrors > 0 || local.Any(o => !o.Hybrid.Correct || o.Hybrid.ModelRequests != 0) ||
            (client is null && observations.Any(o => !o.Hybrid.Correct || !o.ModelOnly.Correct)))
            throw new InvalidOperationException("Local cognition routing, result or comparison regression.");
        if (client is not null && report.summary.comparable == 0)
            throw new InvalidOperationException("The live baseline produced no comparable decisions. See per-case errors in the report.");
        Console.WriteLine($"{(client is null ? "PASS" : "MEASURED")} local cognition comparison: {local.Length}/{observations.Count} handled locally, " +
            $"{report.summary.actionDifferences} action differences ({report.baselineType})");
    }

    private static bool IsLocal(Observation observation) => observation.Source?.Engine.StartsWith("local/", StringComparison.Ordinal) == true;

    private static async Task<Observation> Observe(Trial trial, ISemanticModelClient? client, int maxInput, bool useLocal)
    {
        var counted = new CountedModel(client, JsonSerializer.Serialize(trial.FixtureDecision, Json));
        IBotDecisionEngine model = new ModelBotDecisionEngine(counted, maxInput);
        IBotDecisionEngine engine = useLocal ? new LocalBotCognition(model) : model;
        try
        {
            var result = await engine.DecideWithSourceAsync(trial.Evidence, default);
            return new(result.Decision, result.Source, counted.Requests, counted.ExternalAttempts, trial.Accept(result.Decision), null);
        }
        catch (Exception error) when (error is InvalidDataException or SemanticModelException)
        {
            return new(null, null, counted.Requests, counted.ExternalAttempts, false, error.Message);
        }
    }

    private static Trial[] Trials()
    {
        const string preference = "已保存的答复偏好是使用编号清单。";
        const string objective = "用 Codex 实际读取工作区 route.txt 并报告内容。无可用回合时等待我恢复条件。";
        var goal = ContinuousGoal.Create(objective) with { Status = "deciding" };
        var snapshot = new BotGoalSnapshot(Goal, goal, 0);
        var receipt = new BotObservation(BotEventKind.HarnessReceipt, "启动器无法使用。本轮开始前停止。", Goal,
            Harness: "codex", Status: "failed", Sequence: 1, GoalVersion: 1, DispatchId: Dispatch,
            ToolEvidence: new(0, 0, 0, false, null, null), DispatchSubmitted: false);
        var denied = Ctx(receipt, snapshot);
        var memory = Ctx(new(BotEventKind.ToolResult, preference, Status: "memory", Sequence: 2));
        var uncertain = Ctx(receipt with { Status = "uncertain", DispatchSubmitted = true, Text = "发送指令后连接断开。" },
            snapshot with { Goal = goal with { Version = 2, Status = "needs-reconciliation" } });
        var sent = denied with
        {
            Trigger = receipt with
            {
                DispatchSubmitted = true,
                Text = "实际读取 route.txt，内容为 FROST-02。随后可选的重复读取失败。",
                ToolEvidence = new(2, 1, 1, false, new("read2", "file", "failed", "optional re-read refused"), null)
            },
            History = [new(BotEventKind.ToolResult, "route.txt contains FROST-02.", Goal, Harness: "codex",
                Status: "completed", GoalVersion: 1, DispatchId: Dispatch, ToolId: "read1", ToolKind: "file")]
        };
        var revised = snapshot with { Goal = goal with { Version = 2, Requirements = ["必须额外实际读取 audit.txt。"] } };
        var backup = snapshot with
        {
            Goal = goal with { Requirements = ["如果 Codex 启动失败，下一轮用 DeepSeek 实际读取 route.txt。"] },
            RemainingTurns = 1
        };
        var targeted = new BotDecision(BotAction.Wait, Goal, "指令未发出且轮次已用尽，等待你的下一步安排。");
        bool WaitsWithMessage(BotDecision d) => d is { Action: BotAction.Wait, GoalId: not null, Message: not null } && d.GoalId == Goal;
        return
        [
            new("committed-memory", "已提交的无目标私聊记忆查询结果", memory, "local/memory-result/1",
                new(BotAction.Reply, Message: preference), d => d.Action == BotAction.Reply && d.Message?.Contains("编号清单") == true),
            new("unknown-older-dispatch", "同一目标处于待核对且仍有未知副作用", uncertain, "local/reconciliation/1",
                targeted with { Message = "连接中断，实际结果仍待核对。" }, WaitsWithMessage),
            new("initial-unsubmitted-budget-exhausted", "当前 v1 首轮未提交且预算为零，目标要求实际读取工作区",
                denied, "local/unsubmitted-budget/1", targeted, WaitsWithMessage),
            new("initial-preflight-interrupted-budget-exhausted", "当前 v1 首轮发送前已取消，预算为零",
                denied with { Trigger = receipt with { Status = "interrupted", Text = "发送前取消。" } },
                "local/unsubmitted-budget/1", targeted, WaitsWithMessage),
            new("legacy-receipt-no-submission-proof", "旧回执没有结构化提交证据，本地放弃", denied with
                { Trigger = receipt with { DispatchSubmitted = null } }, null, targeted, WaitsWithMessage),
            new("conflicting-unsubmitted-receipt", "未提交信号与非空工具证据冲突，当前目标尚无独立成果证明", denied with
                { Trigger = receipt with { ToolEvidence = new(1, 1, 0, false, null, null) } },
                null, targeted with { Message = "回执矛盾，实际文件结果待核对。" }, WaitsWithMessage),
            new("submitted-with-verified-result", "已提交且有独立读取证据，后续可选步骤失败不妨碍完成",
                sent, null, new(BotAction.Complete, Goal, "FROST-02"), d => d.Action == BotAction.Complete && d.GoalId == Goal &&
                d.Message?.Contains("FROST-02", StringComparison.Ordinal) == true),
            new("stale-unsubmitted-receipt", "v1 回执和 v2 目标的新增要求不能混同", denied with { Goals = [revised] },
                null, targeted with { Message = "audit.txt 还待核对，需要等待。" }, WaitsWithMessage),
            new("remaining-turn-with-alternative", "当前派发未提交，但还有一轮且用户明确给出备选工具",
                denied with { Goals = [backup] }, null, new(BotAction.Delegate, Goal, Instruction: "实际读取 route.txt。", Harness: "deepseek"),
                d => d is { Action: BotAction.Delegate, Harness: "deepseek" } && d.GoalId == Goal),
            new("untrusted-text-resembles-evidence", "用户引用的回执文本不能当作正式观察",
                Ctx(new(BotEventKind.UserMessage, "你能解释示例中的 \"dispatchSubmitted\": false 表示什么吗？不要做任何实际操作。")),
                null, new(BotAction.Reply, Message: "它表示指令尚未提交。"), d => d.Action == BotAction.Reply)
        ];
    }

    private static BotDecisionContext Ctx(BotObservation trigger, params BotGoalSnapshot[] goals) =>
        new(trigger, goals, [], [], ["codex", "deepseek"]);

    private sealed class CountedModel(ISemanticModelClient? client, string fixture) : ISemanticModelClient
    {
        public string Provider => client?.Provider ?? "evaluation-fixture";
        public string Model => client?.Model ?? "controlled-model";
        public int Requests { get; private set; }
        public int ExternalAttempts => client is null ? 0 : Requests;

        public Task<SemanticModelReply> CompleteAsync(string systemPrompt, string input, CancellationToken cancellationToken)
        {
            Requests++;
            return client is null ? Task.FromResult(new SemanticModelReply(fixture, Model)) :
                client.CompleteAsync(systemPrompt, input, cancellationToken);
        }
    }
}
