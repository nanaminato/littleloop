using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace LittleLoop.Tools;

public interface IAgentSessionClient : IAsyncDisposable
{
    Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token);
    Task ReplyAsync(JsonElement id, object result, CancellationToken token);
    Task<JsonElement> NextAsync(CancellationToken token);
}

public sealed class CodexAppServerClient : IAgentSessionClient
{
    private readonly Process process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> calls = new();
    private readonly Channel<JsonElement> events = Channel.CreateBounded<JsonElement>(512);
    private readonly SemaphoreSlim send = new(1);
    private readonly CancellationTokenSource stopped = new();
    private readonly Task reader, errors;
    private readonly string requestsFile;
    private long sequence;
    public CodexAppServerClient(string executable, string cwd, string logDirectory)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new ArgumentException("Absolute Codex executable required.");
        ProjectFiles.CheckAncestors(executable);
        Directory.CreateDirectory(logDirectory);
        requestsFile = Path.Combine(logDirectory, "requests.jsonl");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        start.Environment.Remove("DEEPSEEK_API_KEY");
        process = Process.Start(start) ?? throw new IOException("Could not start Codex App Server.");
        reader = ReadAsync(Path.Combine(logDirectory, "events.jsonl"));
        errors = DrainErrors(Path.Combine(logDirectory, "stderr.log"));
    }
    public async Task InitializeAsync(CancellationToken token, bool experimental = false)
    {
        await CallAsync("initialize", new { clientInfo = new { name = "littleloop", title = "LittleLoop", version = "0.1.0" }, capabilities = new { experimentalApi = experimental } }, token);
        await WriteAsync(new { method = "initialized", @params = new { } }, token);
    }
    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        long id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
        }
        finally { calls.TryRemove(id, out _); }
    }
    public Task ReplyAsync(JsonElement id, object result, CancellationToken token) => WriteAsync(new { id, result }, token);
    public async Task<JsonElement> NextAsync(CancellationToken token) => await events.Reader.ReadAsync(token);
    private async Task WriteAsync(object message, CancellationToken token)
    {
        await send.WaitAsync(token);
        try
        {
            string line = JsonSerializer.Serialize(message);
            await File.AppendAllTextAsync(requestsFile, line + "\n", token);
            await process.StandardInput.WriteLineAsync(line.AsMemory(), token); await process.StandardInput.FlushAsync(token);
        }
        finally { send.Release(); }
    }
    private async Task ReadAsync(string logPath)
    {
        Exception? failure = null; long bytes = 0;
        try
        {
            using var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read));
            while (await process.StandardOutput.ReadLineAsync(stopped.Token) is { } line)
            {
                if (line.Length > 2_000_000 || (bytes += line.Length) > 40_000_000) throw new IOException("App Server event budget exceeded.");
                using var doc = JsonDocument.Parse(line); var item = doc.RootElement.Clone();
                await log.WriteLineAsync(line); await log.FlushAsync();
                if (!item.TryGetProperty("method", out _) && item.TryGetProperty("id", out var id) && id.TryGetInt64(out long number) && calls.TryRemove(number, out var completion))
                {
                    if (item.TryGetProperty("error", out var error)) completion.TrySetException(new IOException("App Server error: " + error.GetRawText()));
                    else completion.TrySetResult(item.GetProperty("result").Clone());
                }
                else if (item.TryGetProperty("method", out _)) await events.Writer.WriteAsync(item, stopped.Token);
            }
            failure = new IOException("App Server disconnected.");
        }
        catch (Exception e) { failure = e; }
        finally
        {
            foreach (var pending in calls.Values) pending.TrySetException(failure!);
            events.Writer.TryComplete(failure);
        }
    }
    private async Task DrainErrors(string path)
    {
        try
        {
            using var log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)); int total = 0;
            while (await process.StandardError.ReadLineAsync(stopped.Token) is { } line)
                if (total < 65536) { string part = line[..Math.Min(line.Length, 65536 - total)]; total += part.Length; await log.WriteLineAsync(part); await log.FlushAsync(); }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel();
        try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await Task.WhenAll(reader, errors).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        process.Dispose(); stopped.Dispose(); send.Dispose();
    }
}
