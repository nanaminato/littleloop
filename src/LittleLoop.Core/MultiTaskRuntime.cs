using System.Text.Json;
using System.Threading.Channels;

namespace LittleLoop.Core;

public interface IAsyncActionExecutor
{
    Task<ActionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>One serial cognitive driver, independent asynchronous execution workers.</summary>
public sealed class MultiTaskRuntime(LearningSession session, IAsyncActionExecutor executor,
    PluginHost? plugins = null, TimeProvider? clock = null)
{
    private readonly Channel<Message> inbox = Channel.CreateUnbounded<Message>(new() { SingleReader = true });
    private int started;
    private sealed record Message(Func<object> Execute, TaskCompletionSource<object> Completion);
    private sealed record Completion(ActionResult? Result, Exception? Error);

    public Task<WorkItem> EnqueueAsync(LearningTask task, int priority = 0, DateTimeOffset? dueAt = null)
    {
        var snapshot = Copy(task);
        return Post<WorkItem>(() => session.Enqueue(snapshot, priority, dueAt));
    }
    public Task<WorkItem> ReviseAsync(LearningTask task, long expectedVersion, int priority = 0, DateTimeOffset? dueAt = null)
    {
        var snapshot = Copy(task);
        return Post<WorkItem>(() => session.ReviseTask(snapshot, expectedVersion, priority, dueAt));
    }
    public Task<WorkItem> CancelAsync(string taskId, long expectedVersion) => Post<WorkItem>(() => session.CancelTask(taskId, expectedVersion));
    public Task<LearningState> ReadAsync() => Post<LearningState>(() => session.Read());
    public Task<LearningState> AddBudgetAsync(int amount, long expectedRevision) =>
        Post<LearningState>(() => session.AddRuntimeBudget(amount, expectedRevision));
    public Task<LearningEpisode> ReconcileAsync(ActionResult result)
    {
        var snapshot = Copy(result);
        return Post<LearningEpisode>(() =>
        {
            var attempt = session.Read().Runtime!.Attempts.SingleOrDefault(a => a.DecisionId == snapshot.DecisionId);
            if (attempt?.Status != AttemptStatus.Uncertain)
                throw new ArgumentException("Reconciliation requires a recorded uncertain execution.");
            return session.Complete(snapshot);
        });
    }
    public Task<LearningEpisode> FeedbackAsync(ResultFeedback feedback)
    {
        var snapshot = Copy(feedback);
        return Post<LearningEpisode>(() => session.Feedback(snapshot));
    }

    public async Task<LearningState> RunAsync(bool stopWhenIdle = true, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("A driver instance can only run once.");
        var active = new Dictionary<string, Task<Completion>>(StringComparer.Ordinal);
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FileStream? lease = null;
        try
        {
            lease = session.AcquireRunnerLease();
            var initial = session.Read();
            if (initial.Runtime is null) throw new ArgumentException("Enable scheduling first.");
            // The old process may have executed a tool. Never dispatch its unknown attempt again.
            foreach (var attempt in initial.Runtime.Attempts.Where(a => a.Status == AttemptStatus.Dispatched))
                session.MarkExecutionUncertain(attempt.DecisionId, "Previous driver stopped before recording the result; reconcile externally.");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < 32 && inbox.Reader.TryRead(out var message); i++)
                {
                    try { message.Completion.SetResult(message.Execute()); }
                    catch (Exception error) { message.Completion.SetException(error); }
                }
                foreach (var id in active.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToArray())
                {
                    var completion = await active[id];
                    if (completion.Error is { } error) session.MarkExecutionUncertain(id, $"{error.GetType().Name}: {error.Message}");
                    else
                    {
                        try
                        {
                            if (completion.Result is null || completion.Result.DecisionId != id)
                                throw new ArgumentException("Executor result belongs to another decision.");
                            session.Complete(completion.Result);
                        }
                        catch (Exception validationError) when (validationError is ArgumentException or InvalidDataException)
                        { session.MarkExecutionUncertain(id, $"Rejected executor result: {validationError.Message}"); }
                    }
                    active.Remove(id);
                }

                bool progressed = false;
                var request = session.DispatchNext();
                if (request is not null)
                {
                    // Task.Run also isolates synchronous setup before a plugin executor's first await.
                    active.Add(request.Trace.Input.Id, Task.Run(async () =>
                    {
                        try { return new Completion(await executor.ExecuteAsync(Copy(request), workers.Token), null); }
                        catch (Exception error) { return new Completion(null, error); }
                    }));
                    progressed = true;
                }
                if (session.ThinkNext((clock ?? TimeProvider.System).GetUtcNow(), plugins) is not null) progressed = true;
                if (progressed) continue;
                if (active.Count == 0 && stopWhenIdle && !inbox.Reader.TryPeek(out _)) break;

                // Cancel the unused inbox wait so each loop has only one live waiter.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var messageReady = inbox.Reader.WaitToReadAsync(wait.Token).AsTask();
                await Task.WhenAny(active.Values.Cast<Task>().Append(messageReady)).WaitAsync(cancellationToken);
                await wait.CancelAsync();
                try { await messageReady; } catch (OperationCanceledException) { }
            }
            return session.Read();
        }
        finally
        {
            inbox.Writer.TryComplete();
            while (inbox.Reader.TryRead(out var message))
                message.Completion.TrySetException(new InvalidOperationException("Driver stopped before processing this command."));
            try
            {
                try { await workers.CancelAsync(); }
                finally
                {
                    foreach (var id in active.Keys)
                        session.MarkExecutionUncertain(id, "Driver interrupted; execution outcome must be reconciled before reusing its slot.");
                }
            }
            finally { lease?.Dispose(); }
        }
    }

    private async Task<T> Post<T>(Func<object> action)
    {
        if (Volatile.Read(ref started) == 0) throw new InvalidOperationException("Start the driver before posting commands.");
        var result = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!inbox.Writer.TryWrite(new(action, result))) throw new InvalidOperationException("Driver is stopped.");
        return (T)await result.Task;
    }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
