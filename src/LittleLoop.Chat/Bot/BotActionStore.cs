using System.Data.Common;
using System.Text.Json;
using LittleLoop.Core;

sealed partial class ChatStore
{
    // The network call took place outside this transaction. Commit only against the observed source and version.
    public async Task<BotDecisionError?> ApplyDecision(DecisionSnapshot snapshot, BotDecision decision, string robot, bool operatorAccess,
        int turnLimit, string grant, string? failure = null, BotDecisionSource? cognitionSource = null, bool retryCognition = false)
    {
        var input = snapshot.Input;
        var context = snapshot.Context;
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        string? type = await LockGoalRoom(db, tx, input.Room);
        bool hostFallback = failure is not null || retryCognition;
        if (hostFallback && (decision.Action is not (BotAction.Reply or BotAction.Wait) ||
            decision.ObligationEdits is not null || decision.ObligationAssessments is not null ||
            decision.WakeObligationId.HasValue || decision.ResolveSourceSequence.HasValue))
            return new("invalid-host-fallback", "field", "A host failure can only report or wait without changing requirements.");
        Guid? target = decision.GoalId ?? (requireObligations && input.Inbox.HasValue && decision.Action == BotAction.Reply
            ? context.ReplyGoalId : null);
        var (error, work) = await DecisionCommitGate(db, tx, snapshot, robot, operatorAccess, type, target);
        if (error is not null) return error;
        if (requireObligations && !hostFallback)
        {
            context = context with { RequireObligations = true };
            if (work?.Goal is { } current && context.Goals.FirstOrDefault(goal => goal.Id == work.Id) is { } observed)
            {
                var locked = observed with { Goal = current, SourceSequence = work.Sequence };
                context = context with
                {
                    Goals = context.Goals.Select(goal => goal.Id == locked.Id ? locked : goal).ToArray(),
                    DispatchEvidence = [await ReadDispatchEvidence(db, tx, input.Room, input.Owner, locked)],
                    ObligationSources = await ReadObligationSources(db, tx, input, locked),
                    UnavailableObligationEvidence = await ReadUnavailableObligationEvidence(db, tx, input.Room, input.Owner, locked)
                };
            }
            if (decision.Check(context) is { } invalid) return invalid;
            if (decision.Action == BotAction.Complete)
            {
                await using var inFlight = Command(db, tx, "SELECT 1 FROM chat_bot_inflight WHERE work_id=@work LIMIT 1",
                    ("work", target!.Value.ToString()));
                if (await inFlight.ExecuteScalarAsync() is not null)
                    return new("inflight-dispatch", "evidence-conflict", "Wait for outstanding execution evidence before completing.", "action");
            }
        }
        if (input.Inbox is long inbox && !await ConsumeInbox(db, tx, inbox))
            return new("input-consumed", "goal-version-conflict", "The user input was already consumed.");
        if (input.Inbox.HasValue)
        {
            await using var current = Command(db, tx, """
                SELECT 1 FROM chat_messages WHERE room_id=@room AND seq=@seq AND sender_id=@owner AND NOT deleted AND NOT edited
                """, ("room", input.Room), ("seq", input.Sequence), ("owner", input.Owner));
            if (await current.ExecuteScalarAsync() is null)
            {
                await tx.CommitAsync();
                return new("source-message-changed", "goal-version-conflict", "The source message was edited or deleted.");
            }
        }
        if (input.Trigger.Kind == BotEventKind.ToolResult && input.Trigger.GoalId is null)
        {
            if (!await ConsumeLocalResult(db, tx, input))
                return new("result-consumed", "goal-version-conflict", "The local result was already consumed.");
        }
        else if (input.Inbox is null)
        {
            if (input.Trigger.Kind == BotEventKind.Timer)
            {
                if (DueTimer(work, clock.GetUtcNow().ToUnixTimeMilliseconds()) != input.Trigger)
                    return new("timer-changed", "goal-version-conflict", "The scheduled decision trigger changed.");
            }
            else if (work?.Status is not ("deciding" or "needs-reconciliation") || work.Input.Loop?.Next != input.Trigger)
                return new("trigger-changed", "goal-version-conflict", "The pending goal observation changed.");
            await using var pending = Command(db, tx, "SELECT 1 FROM chat_goal_inbox WHERE room_id=@room AND owner_id=@owner AND NOT processed LIMIT 1",
                ("room", input.Room), ("owner", input.Owner));
            if (await pending.ExecuteScalarAsync() is not null)
                return new("pending-user-input", "goal-version-conflict", "User input superseded this pending decision.");
        }

        var goal = work?.Goal;
        var loop = work?.Input.Loop ?? new BotGoalLoop(0, turnLimit);
        string? message = decision.Message;
        string? output = null;
        GoalObligationCheck? scheduledCheck = null;
        switch (decision.Action)
        {
            case BotAction.Delegate:
                if (work is null)
                {
                    goal = ContinuousGoal.Create(decision.Objective!);
                    target = Guid.NewGuid();
                    work = new(target.Value, input.Room, input.Owner, input.Sequence, "task", "queued",
                        new(goal.Prompt, Anchor: context.QuotedText), Goal: goal);
                    await InsertJob(db, tx, work);
                }
                if (loop.Used >= loop.Limit) throw new InvalidDataException($"目标的 {loop.Limit} 次 Harness 调用预算已用尽。");
                if (loop.Used == 0 && message is null) throw new InvalidDataException("首次派发缺少给用户的行动说明。");
                if (work.Status is "running" or "paused" or "needs-reconciliation")
                    throw new InvalidDataException("当前目标需先调整要求、恢复或核对正在执行的步骤。");
                loop = loop with { Used = loop.Used + 1, Next = null,
                    Dispatch = new(Guid.NewGuid(), decision.Harness!, decision.Instruction!) };
                goal = goal! with { Version = goal.Version + (work.Input.Loop is null ? 0 : 1), Status = "queued" };
                break;
            case BotAction.Revise:
            case BotAction.Resume:
            case BotAction.Cancel:
            case BotAction.Pause:
                goal = goal!.Change(decision.Action.ToString().ToLowerInvariant(), decision.Instruction ?? "", goal.Version);
                if (goal.Status == "queued") goal = goal with { Status = "deciding" };
                loop = loop with { Dispatch = null, Next = goal.Status == "deciding"
                    ? new(BotEventKind.BotDecision, decision.Instruction ?? goal.Prompt, target, Status: decision.Action.ToString(), Sequence: input.Sequence)
                    : null };
                break;
            case BotAction.Wait when goal is not null:
            case BotAction.Complete:
                goal = goal! with { Version = goal.Version + 1,
                    Status = decision.Action == BotAction.Complete ? "completed" :
                        work!.Status == "needs-reconciliation" ? work.Status : "waiting-input" };
                loop = loop with { Dispatch = null, Next = null };
                if (decision.WakeAfterSeconds is int delay)
                {
                    long at = clock.GetUtcNow().AddSeconds(delay).ToUnixTimeMilliseconds();
                    if (!hostFallback && decision.WakeObligationId.HasValue)
                        scheduledCheck = new(Guid.NewGuid(), at, goal.Version);
                    loop = loop with
                    {
                        Wake = new(at, goal.Version, retryCognition, scheduledCheck?.Id,
                            scheduledCheck is null ? null : decision.WakeObligationId),
                        ScheduledWakeups = loop.ScheduledWakeups + 1
                    };
                }
                if (decision.Action == BotAction.Complete)
                {
                    output = message;
                    if (!context.AutoFinalReport) message = null;
                    else if (message is not null && type == "direct" && await CompletionIncludesGoal(db, tx, work!))
                    {
                        string objective = "任务目标：" + GoalMemoryExcerpt(goal.Objective);
                        message = decision.ObligationAssessments?.Any(a => a.CompletionNotice) == true
                            ? objective + "\n" + message : message + "\n" + objective;
                    }
                }
                break;
            case BotAction.SetFinalReport:
                await using (var set = Command(db, tx, """
                    INSERT INTO chat_bot_report_settings(room_id,owner_id,enabled) VALUES(@room,@owner,@enabled)
                    ON DUPLICATE KEY UPDATE enabled=@enabled
                    """, ("room", input.Room), ("owner", input.Owner), ("enabled", decision.Enabled!.Value)))
                    await set.ExecuteNonQueryAsync();
                break;
            case BotAction.Forget:
            case BotAction.Remember:
                if (type != "direct") { message = "长期偏好可以在你的 LittleLoop 私聊中保存。"; break; }
                if (decision.MemorySource is long source)
                {
                    await using var remove = Command(db, tx, "DELETE FROM chat_bot_memories WHERE room_id=@room AND owner_id=@owner AND source_seq=@source",
                        ("room", input.Room), ("owner", input.Owner), ("source", source));
                    if (await remove.ExecuteNonQueryAsync() != 1)
                        throw new InvalidDataException("没有找到对应的私聊偏好来源。");
                }
                if (decision.Action == BotAction.Forget) break;
                await using (var set = Command(db, tx, """
                    INSERT INTO chat_bot_memories(room_id,owner_id,source_seq,body) VALUES(@room,@owner,@seq,@body)
                    """, ("room", input.Room), ("owner", input.Owner), ("seq", input.Sequence), ("body", decision.Instruction)))
                    await set.ExecuteNonQueryAsync();
                break;
            case BotAction.ManageMemory:
                var memoryResult = type == "direct" && input.ReplyTo is null
                    ? await HandleConversationNote(db, tx, input.Room, input.Owner, input.Sequence, input.Trigger.Text) : null;
                // Project lessons and their source management operate on the original user message.
                var memoryGoals = new List<BotWork>();
                if (ConversationMemoryPolicy.Observe(input.Trigger.Text)?.Subject == MemorySubject.CompletionNotice)
                    foreach (var candidate in context.Goals.Where(g => input.ReplyTo.HasValue ? g.Id == context.ReplyGoalId :
                        g.Goal.Status is "deciding" or "queued" or "running" or "paused" or "waiting-input"))
                        if (await LockedWork(db, tx, candidate.Id, input.Room, input.Owner) is { } referenced)
                            memoryGoals.Add(referenced);
                memoryResult ??= await HandleGoalMemory(db, tx, input.Room, type, input.Owner, input.Sequence,
                    input.ReplyTo, input.Trigger.Text, memoryGoals, grant);
                message = memoryResult ?? "没有找到这条消息对应的记忆操作。";
                break;
        }
        if (work is not null && goal is not null)
        {
            if (decision.Action != BotAction.Reply || decision.ResolveSourceSequence.HasValue)
                goal = GoalObligationPolicy.Apply(goal, work.Id, decision.Action == BotAction.Revise ? input.Sequence : work.Sequence,
                    decision, input.Trigger, scheduledCheck);
            if (decision.Action == BotAction.Reply)
            {
                if (requireObligations && hostFallback && input.Inbox.HasValue && (ActiveForMemory(work) || work.Status == "needs-reconciliation"))
                    goal = goal with { PendingRequirementSources = [.. (goal.PendingRequirementSources ?? []), input.Sequence] };
                if (goal != work.Goal)
                    await SaveGoal(db, tx, work, goal, input.Sequence, "requirement-source", error: work.Error, output: work.Output);
                if (requireObligations && input.Inbox.HasValue)
                    message += "\n目标要求未变更。";
            }
            else
            {
                work = work with { Input = work.Input with { Loop = loop } };
                await SaveGoal(db, tx, work, goal, input.Sequence, decision.Action.ToString(),
                    error: failure is not null ? Cut(failure, 250) : goal.Status == "needs-reconciliation" ? work.Error : null, output: output);
            }
        }
        if (input.Inbox.HasValue)
        {
            await Record(db, tx, input.Room, input.Owner, input.Sequence, input.Trigger with { GoalId = target });
            if (target is not null)
            {
                await using var card = Command(db, tx, "UPDATE chat_messages SET card=@card WHERE room_id=@room AND seq=@seq",
                    ("card", ToJson(new MessageCard("task", TaskId: target))), ("room", input.Room), ("seq", input.Sequence));
                await card.ExecuteNonQueryAsync();
            }
        }
        else if (input.Trigger.Kind == BotEventKind.Timer)
            await Record(db, tx, input.Room, input.Owner, input.Sequence, input.Trigger);
        var decisionText = decision.Instruction is not null && decision.Message is not null
            ? $"{decision.Instruction}\n给用户的说明：{decision.Message}"
            : decision.Instruction ?? decision.Message ?? decision.Action.ToString();
        var detail = JsonSerializer.SerializeToNode(decision, BotJson)!.AsObject();
        detail["triggerKind"] = input.Trigger.Kind.ToString();
        if (input.Trigger.GoalVersion is int triggerVersion) detail["triggerGoalVersion"] = triggerVersion;
        if (input.Trigger.DispatchId is Guid triggerDispatchId) detail["triggerDispatchId"] = triggerDispatchId;
        if (cognitionSource is not null) detail["cognitionSource"] = JsonSerializer.SerializeToNode(cognitionSource, BotJson);
        await Record(db, tx, input.Room, input.Owner, input.Sequence, new(BotEventKind.BotDecision,
            decisionText, target, decision.Harness, decision.Action.ToString(), input.Sequence, goal?.Version,
            DispatchId: decision.Action == BotAction.Delegate ? loop.Dispatch!.Id : null), detail.ToJsonString());
        if (decision.Action == BotAction.ManageMemory)
        {
            var observation = new BotObservation(BotEventKind.ToolResult, message!, Status: "memory", Sequence: input.Sequence);
            await Record(db, tx, input.Room, input.Owner, input.Sequence, observation);
            await using var result = Command(db, tx, "INSERT INTO chat_bot_local_results(room_id,owner_id,seq,observation) VALUES(@room,@owner,@seq,@result)",
                ("room", input.Room), ("owner", input.Owner), ("seq", input.Sequence), ("result", ToJson(observation)));
            await result.ExecuteNonQueryAsync();
            message = null; // Cognition receives the committed tool result before replying.
        }
        if (!string.IsNullOrWhiteSpace(message)) await AddGoalNotice(db, tx, input.Room, input.Sequence, message, target);
        await tx.CommitAsync();
        return null;
    }

    public async Task SaveHarnessReceipt(BotWork executed, HarnessReceipt receipt)
    {
        var dispatch = executed.Input.Loop!.Dispatch!;
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        await LockGoalRoom(db, tx, executed.Room);
        var current = await LockedWork(db, tx, executed.Id, executed.Room, executed.Owner);
        if (current is null) return;
        await using var settled = Command(db, tx, "DELETE FROM chat_bot_inflight WHERE dispatch_id=@dispatch AND work_id=@work",
            ("dispatch", dispatch.Id.ToString()), ("work", executed.Id.ToString()));
        if (await settled.ExecuteNonQueryAsync() != 1) return;
        var result = new BotObservation(BotEventKind.HarnessReceipt, Cut(receipt.Report, 16000), executed.Id,
            dispatch.Harness, receipt.Status, executed.Sequence, executed.Goal!.Version, dispatch.Id,
            ToolEvidence: receipt.SummarizeTools(), DispatchSubmitted: receipt.DispatchSubmitted);
        foreach (var tool in receipt.Tools)
            await Record(db, tx, executed.Room, executed.Owner, executed.Sequence, new(BotEventKind.ToolResult,
                Cut($"{tool.Kind} [{tool.Id}] {tool.Detail}", 8000), executed.Id, dispatch.Harness, tool.Status,
                executed.Sequence, executed.Goal!.Version, dispatch.Id, ToolId: tool.Id, ToolKind: tool.Kind), ToJson(tool));
        await Record(db, tx, executed.Room, executed.Owner, executed.Sequence, result,
            ToJson(new { dispatch.Id, receipt.SessionId, version = executed.Goal!.Version, receipt.Status }));
        if (current.Input.Loop?.Dispatch?.Id == dispatch.Id && current.Status == "running" &&
            current.Goal!.Version == executed.Goal.Version)
        {
            bool unknown = receipt.Status == "uncertain";
            var next = current.Goal with { Status = unknown ? "needs-reconciliation" : "deciding" };
            var loop = current.Input.Loop with { Dispatch = null, Next = result };
            await SaveGoal(db, tx, current with { Input = current.Input with { Loop = loop } }, next, executed.Sequence,
                "harness-receipt", unknown ? Cut(receipt.Report, 250) : null);
        }
        else if (receipt.Status == "uncertain" && current.Status is not ("cancelled" or "disabled" or "needs-reconciliation"))
        {
            var stopped = current.Goal! with { Version = current.Goal!.Version + 1, Status = "needs-reconciliation" };
            var loop = current.Input.Loop! with { Dispatch = null, Next = result };
            await SaveGoal(db, tx, current with { Input = current.Input with { Loop = loop } }, stopped,
                executed.Sequence, "uncertain", Cut(receipt.Report, 250));
        }
        await tx.CommitAsync();
    }

    public async Task MigrateBotQueue(int limit)
    {
        await using var db = await Open();
        var jobs = new List<BotWork>();
        await using (var cmd = Command(db, null, """
            SELECT id,room_id,owner_id,seq,kind,status,input,error,output,goal FROM chat_bot_work
            WHERE status='queued' AND kind IN ('task','question','chat') AND JSON_EXTRACT(input,'$.loop') IS NULL
            """))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) { var w = ReadWork(r); if (w.Input.Provider != ReviewBotName) jobs.Add(w); }
        foreach (var job in jobs)
        {
            await using var tx = await db.BeginTransactionAsync();
            await LockGoalRoom(db, tx, job.Room);
            if (await LockedWork(db, tx, job.Id, job.Room, job.Owner) is not { Status: "queued" }) continue;
            if (job.Input.Provider is not (null or BotName))
            {
                const string reason = "此项目机器人已停止派发。请在 LittleLoop 会话中继续，并指定要使用的工作区。";
                if (job.Goal is { } historical)
                    await SaveGoal(db, tx, job, historical with { Status = "disabled" }, job.Sequence, "retired-provider", reason);
                else
                {
                    await using var update = Command(db, tx, "UPDATE chat_bot_work SET status='disabled',error=@reason WHERE id=@id",
                        ("id", job.Id.ToString()), ("reason", reason));
                    await update.ExecuteNonQueryAsync();
                }
                await AddGoalNotice(db, tx, job.Room, job.Sequence, reason, job.Id);
            }
            else if (job.Goal is { } goal)
            {
                var next = new BotObservation(BotEventKind.BotDecision, goal.Prompt, job.Id,
                    Status: "resume", Sequence: job.Sequence);
                await SaveGoal(db, tx, job with { Input = job.Input with { Loop = new(0, limit, Next: next) } },
                    goal with { Status = "deciding" }, job.Sequence, "migrate");
            }
            else
            {
                await using var cmd = Command(db, tx, """
                    INSERT INTO chat_goal_inbox(room_id,seq,owner_id,body,reply_to)
                    SELECT room_id,seq,sender_id,body,reply_to FROM chat_messages WHERE room_id=@room AND seq=@seq
                    ON DUPLICATE KEY UPDATE processed=FALSE;
                    UPDATE chat_bot_work SET status='superseded' WHERE id=@id
                    """, ("room", job.Room), ("seq", job.Sequence), ("id", job.Id.ToString()));
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
    }

    public async Task<BotWork[]> InterruptedDispatches()
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, "SELECT claimed_work FROM chat_bot_inflight");
        var works = new List<BotWork>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) works.Add(FromJson<BotWork>(reader.GetString(0)));
        return works.ToArray();
    }

    private static string Cut(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
