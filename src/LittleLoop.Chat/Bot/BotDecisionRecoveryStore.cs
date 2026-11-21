using System.Data.Common;
using LittleLoop.Core;

sealed partial class ChatStore
{
    public async Task<BotDecisionError?> DeferFailedDecision(DecisionSnapshot snapshot, BotDecisionSource source,
        BotDecisionError error, string robot, bool operatorAccess, bool transient)
    {
        var input = snapshot.Input;
        var target = input.Trigger.GoalId ?? snapshot.Context.ReplyGoalId;
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        string? type = await LockGoalRoom(db, tx, input.Room);
        var (conflict, work) = await DecisionCommitGate(db, tx, snapshot, robot, operatorAccess, type, target);
        if (conflict is not null) return conflict;
        if (input.Trigger.GoalId.HasValue && await PendingGoalTriggerError(db, tx, input, work) is { } obsolete)
            return obsolete;

        var (scope, workId) = RecoveryKey(input);
        int failures = (input.Recovery?.Failures ?? 0) + 1;
        // Protocol recovery has a fresh model evaluation budget, with the prior failures available as evidence.
        // Transport failures can recover without either user input or a goal wakeup.
        int? delay = transient ? Math.Min(300, 30 << Math.Min(failures - 1, 4)) : failures == 1 ? 2 : null;
        long? at = delay is int seconds ? clock.GetUtcNow().AddSeconds(seconds).ToUnixTimeMilliseconds() : null;
        var errors = (input.Recovery?.Errors ?? []).Concat(source.ModelCalls?.Select(call => call.Error).OfType<BotDecisionError>() ?? [])
            .Append(error).Distinct().TakeLast(8).ToArray();
        using var save = Command(db, tx, """
            INSERT INTO chat_bot_decision_recovery(room_id,owner_id,seq,scope,work_id,goal_version,retry_at,recovery)
            VALUES(@room,@owner,@seq,@scope,@work,@version,@at,@recovery)
            ON DUPLICATE KEY UPDATE goal_version=@version,retry_at=@at,recovery=@recovery
            """, ("room", input.Room), ("owner", input.Owner), ("seq", input.Sequence), ("scope", scope), ("work", workId),
            ("version", input.Trigger.GoalId.HasValue ? work!.Goal!.Version : 0), ("at", at),
            ("recovery", ToJson(new BotDecisionRecovery(failures, errors))));
        await save.ExecuteNonQueryAsync();
        await WriteDecisionAudit(db, tx, snapshot, source with
        {
            Engine = at.HasValue ? "host/decision-deferred" : "host/decision-blocked"
        }, error);

        if (at is null)
        {
            const string message = "本次认知决策未能通过协议核对。已保留原始输入及诊断，任务未提交完成。";
            if (input.Inbox is long inbox)
            {
                if (!await ConsumeInbox(db, tx, inbox))
                    return new("input-consumed", "goal-version-conflict", "The user input was already consumed.");
                if (requireObligations && work?.Goal is { } goal &&
                    (ActiveForMemory(work) || work.Status == "needs-reconciliation"))
                {
                    var pending = (goal.PendingRequirementSources ?? []).Append(input.Sequence).Distinct().ToArray();
                    await SaveGoal(db, tx, work, goal with { PendingRequirementSources = pending }, input.Sequence,
                        "requirement-source", error: work.Error, output: work.Output);
                }
                await Record(db, tx, input.Room, input.Owner, input.Sequence, input.Trigger with { GoalId = target });
            }
            else if (input.Trigger.GoalId is null)
            {
                if (!await ConsumeLocalResult(db, tx, input))
                    return new("result-consumed", "goal-version-conflict", "The local result was already consumed.");
            }
            else
            {
                await using var update = Command(db, tx, "UPDATE chat_bot_work SET error=@error WHERE id=@work",
                    ("error", Cut(error.Message, 250)), ("work", workId));
                await update.ExecuteNonQueryAsync();
            }
            if (input.Trigger.GoalId is null) await ClearDecisionRecovery(db, tx, input);
            await AddGoalNotice(db, tx, input.Room, input.Sequence, message, target);
        }
        await tx.CommitAsync();
        return null;
    }

    private static (string Scope, string WorkId) RecoveryKey(DecisionInput input) =>
        input.Inbox.HasValue ? ("inbox", "") : input.Trigger.GoalId is Guid goal ? ("goal", goal.ToString()) : ("local", "");

    private static async Task ClearDecisionRecovery(DbConnection db, DbTransaction tx, DecisionInput input)
    {
        if (input.Recovery is null) return;
        var (scope, workId) = RecoveryKey(input);
        await using var clear = Command(db, tx, """
            DELETE FROM chat_bot_decision_recovery WHERE room_id=@room AND owner_id=@owner AND seq=@seq
                AND scope=@scope AND work_id=@work
            """, ("room", input.Room), ("owner", input.Owner), ("seq", input.Sequence), ("scope", scope), ("work", workId));
        await clear.ExecuteNonQueryAsync();
    }

    private async Task<BotDecisionError?> PendingGoalTriggerError(DbConnection db, DbTransaction tx, DecisionInput input, BotWork? work)
    {
        if (input.Trigger.Kind == BotEventKind.Timer)
        {
            if (DueTimer(work, clock.GetUtcNow().ToUnixTimeMilliseconds()) != input.Trigger)
                return new("timer-changed", "goal-version-conflict", "The scheduled decision trigger changed.");
        }
        else if (work?.Status is not ("deciding" or "needs-reconciliation") || work.Input.Loop?.Next != input.Trigger)
            return new("trigger-changed", "goal-version-conflict", "The pending goal observation changed.");
        await using var pending = Command(db, tx, """
            SELECT 1 FROM chat_goal_inbox WHERE room_id=@room AND owner_id=@owner AND NOT processed LIMIT 1
            """, ("room", input.Room), ("owner", input.Owner));
        return await pending.ExecuteScalarAsync() is not null
            ? new("pending-user-input", "goal-version-conflict", "User input superseded this pending decision.") : null;
    }
}
