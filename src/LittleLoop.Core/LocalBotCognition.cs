namespace LittleLoop.Core;

// Provenance belongs to the decision provider and is separate from the action proposed by a model.
public sealed record BotDecisionSource(string Engine, string? Model = null);
public sealed record BotDecisionResult(BotDecision Decision, BotDecisionSource Source);

public sealed class LocalBotCognition(IBotDecisionEngine fallback) : IBotDecisionEngine
{
    public async Task<BotDecision> DecideAsync(BotDecisionContext context, CancellationToken token) =>
        (await DecideWithSourceAsync(context, token)).Decision;

    public async Task<BotDecisionResult> DecideWithSourceAsync(BotDecisionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var trigger = context.Trigger;
        if (trigger is { Kind: BotEventKind.ToolResult, Status: "memory", Sequence: > 0, GoalId: null, Harness: null } &&
            !string.IsNullOrWhiteSpace(trigger.Text) && trigger.Text.Length <= 16000)
            return new(new BotDecision(BotAction.Reply, Message: trigger.Text).Validate(context), new("local/memory-result/1"));

        // An uncertain receipt can be for an older revision. The host already placed the current goal on hold.
        if (trigger is { Kind: BotEventKind.HarnessReceipt, Status: "uncertain", GoalId: Guid id, GoalVersion: > 0 } &&
            !string.IsNullOrWhiteSpace(trigger.Harness) &&
            context.Goals.Any(g => g.Id == id && g.Goal.Status == "needs-reconciliation" && g.Goal.Version >= trigger.GoalVersion))
            return new(new BotDecision(BotAction.Wait, id,
                $"请核对 {trigger.Harness} 本轮操作的实际结果。执行记录不足以确认已经发生的更改，目标已等候核对。").Validate(context),
                new("local/reconciliation/1"));

        return await fallback.DecideWithSourceAsync(context, token);
    }
}
