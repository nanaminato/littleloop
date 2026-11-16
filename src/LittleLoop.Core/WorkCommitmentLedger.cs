namespace LittleLoop.Core;

public sealed record WorkCommitment(string Id, int Version, string Goal, string Status, string[] MessageIds,
    string? Replaces = null, string? ExecutionId = null, int? ExecutionVersion = null);
public sealed record WorkCommitmentReceipt(string TaskId, int Version, string ExecutionId, string Outcome, bool Late);
public sealed record WorkCommitmentEvent(string MessageId, WorkInterpretation Interpretation, string? TaskId, string Detail);
public sealed record WorkCommitmentState(WorkCommitment[] Tasks, WorkCommitmentEvent[] Events, WorkCommitmentReceipt[] Receipts)
{
    public static WorkCommitmentState Empty => new([], [], []);
}
public sealed record WorkCommitmentTransition(WorkCommitmentState State, string Detail, string? TaskId, bool Duplicate = false);

/// <summary>Pure immutable transitions. Host serializes transitions and persistence under one lock per ledger.
/// It must never dispatch before persisting and never claim cancellation means side effects were rolled back.</summary>
public static class WorkCommitmentLedger
{
    public const int MaximumEvents = 1000;
    public static WorkReference[] References(WorkCommitmentState state) => state.Tasks
        .Select(t => new WorkReference(t.Id, t.Version, t.Goal, t.Status, t.MessageIds)).ToArray();

    public static WorkCommitmentTransition Apply(WorkCommitmentState state, WorkUtterance message, string newTaskId)
    {
        if (state.Events.FirstOrDefault(e => e.MessageId == message.MessageId) is { } old)
            return new(state, old.Detail, old.TaskId, true);
        var interpreted = WorkConversationPolicy.Interpret(message, References(state));
        return Apply(state, message.MessageId, interpreted, newTaskId);
    }

    public static WorkCommitmentTransition Apply(WorkCommitmentState state, string messageId, WorkInterpretation interpretation, string newTaskId)
    {
        if (state.Events.FirstOrDefault(e => e.MessageId == messageId) is { } old)
            return new(state, old.Detail, old.TaskId, true);
        if (state.Events.Length >= MaximumEvents) throw new InvalidOperationException("Commitment journal full; archive explicitly before accepting new work.");
        var tasks = state.Tasks.ToList();
        string? id = interpretation.TaskId;
        var detail = interpretation.Reason;
        var index = tasks.FindIndex(t => t.Id == id);
        var target = index >= 0 ? tasks[index] : null;
        var intent = interpretation.Intent;
        if (intent is not (WorkIntent.Accept or WorkIntent.None or WorkIntent.Clarify) &&
            (target is null || target.Version != interpretation.ExpectedVersion))
        {
            intent = WorkIntent.Clarify;
            detail = "Task changed before apply; reread the current task card.";
        }
        if (intent == WorkIntent.Accept || intent == WorkIntent.Replace)
        {
            if (tasks.Count >= WorkConversationPolicy.MaximumReferences) throw new InvalidOperationException("Task capacity reached; archive explicitly before accepting work.");
            if (string.IsNullOrWhiteSpace(newTaskId) || tasks.Any(t => t.Id == newTaskId) || string.IsNullOrWhiteSpace(interpretation.Goal))
                throw new ArgumentException("A unique task ID and nonempty goal are required.");
        }
        switch (intent)
        {
            case WorkIntent.Accept:
                if (tasks.Any(t => t.Status is "queued" or "running" or "paused" or "stopping" || t.ExecutionId is not null))
                { detail = "Finish or explicitly replace the current commitment first."; break; }
                id = newTaskId;
                tasks.Add(new(id, 1, interpretation.Goal!, "queued", [messageId]));
                detail = "Accepted; queued, not yet executed.";
                break;
            case WorkIntent.Pause:
            case WorkIntent.Cancel:
            case WorkIntent.Replace:
                if (target!.Status is "completed" or "cancelled" or "replaced") { detail = "Historical work cannot be stopped again."; break; }
                var destination = intent == WorkIntent.Pause ? "paused" : intent == WorkIntent.Cancel ? "cancelled" : "replaced";
                tasks[index] = target with { Version = target.Version + 1, Status = destination, MessageIds = target.MessageIds.Append(messageId).ToArray() };
                if (intent == WorkIntent.Replace)
                {
                    id = newTaskId;
                    tasks.Add(new(id, 1, interpretation.Goal!, "queued", [messageId], target.Id));
                }
                detail = target.ExecutionId is null ? "Commitment updated before dispatch." : "Stop requested; old execution still outstanding. No further work may dispatch until its receipt.";
                break;
            case WorkIntent.Revise:
                if (target!.Status is "completed" or "cancelled" or "replaced") { detail = "Historical work cannot be revised."; break; }
                if (string.IsNullOrWhiteSpace(interpretation.Goal)) throw new ArgumentException("Goal required.");
                tasks[index] = target with { Version = target.Version + 1, Goal = interpretation.Goal!, Status = "queued", MessageIds = target.MessageIds.Append(messageId).ToArray() };
                detail = target.ExecutionId is null ? "Goal revised; authorization is not expanded." : "Goal revised; waiting for old execution to stop before dispatch.";
                break;
            case WorkIntent.Resume:
                if (target!.Status != "paused") { detail = "Only a paused commitment can resume."; break; }
                tasks[index] = target with { Version = target.Version + 1, Status = "queued", MessageIds = target.MessageIds.Append(messageId).ToArray() };
                detail = "Resumed in queue; prior approvals are stale.";
                break;
        }
        return new(state with { Tasks = tasks.ToArray(), Events = state.Events.Append(new(messageId, interpretation, id, detail)).ToArray() }, detail, id);
    }

    public static WorkCommitmentState Dispatch(WorkCommitmentState state, string taskId, int expectedVersion, string executionId)
    {
        if (string.IsNullOrWhiteSpace(executionId) || state.Tasks.Any(t => t.ExecutionId is not null) || state.Receipts.Any(r => r.ExecutionId == executionId))
            throw new InvalidOperationException("An execution is outstanding or its identifier was already used.");
        var task = state.Tasks.Single(t => t.Id == taskId);
        if (task.Version != expectedVersion || task.Status != "queued") throw new InvalidOperationException("Stale dispatch.");
        return state with { Tasks = state.Tasks.Select(t => t.Id == taskId ? t with { Status = "running", ExecutionId = executionId, ExecutionVersion = expectedVersion } : t).ToArray() };
    }

    public static bool CanApprove(WorkCommitmentState state, string taskId, int version, string executionId) =>
        state.Tasks.Any(t => t.Id == taskId && t.Version == version && t.ExecutionVersion == version && t.ExecutionId == executionId && t.Status == "running");

    public static WorkCommitmentState Receive(WorkCommitmentState state, string taskId, int version, string executionId, string outcome)
    {
        if (state.Receipts.Any(r => r.ExecutionId == executionId)) return state;
        var task = state.Tasks.Single(t => t.Id == taskId);
        if (task.ExecutionId != executionId || task.ExecutionVersion != version) throw new InvalidOperationException("Receipt does not match dispatched execution.");
        if (outcome is not ("completed" or "stopped" or "failed" or "unknown")) throw new ArgumentException("Unknown outcome label.");
        var late = task.Version != version || task.Status is not ("running" or "needs-reconciliation");
        // Unknown remains an outstanding execution and blocks replacement until explicit reconciliation.
        if (outcome == "unknown") return state with { Tasks = state.Tasks.Select(t => t.Id == taskId ? t with { Status = late ? t.Status : "needs-reconciliation" } : t).ToArray() };
        var updated = task with { ExecutionId = null, ExecutionVersion = null, Status = late ? task.Status : outcome == "completed" ? "completed" : outcome == "stopped" ? "paused" : "failed" };
        return state with { Tasks = state.Tasks.Select(t => t.Id == taskId ? updated : t).ToArray(),
            Receipts = state.Receipts.Append(new(taskId, version, executionId, outcome, late)).ToArray() };
    }
}
