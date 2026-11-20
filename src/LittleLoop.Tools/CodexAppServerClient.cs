using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
    private readonly string? requestsFile;
    private long sequence;
    public CodexAppServerClient(string executable, string cwd, string logDirectory) : this(executable, cwd, logDirectory, false) { }
    public CodexAppServerClient(string executable, string cwd, string logDirectory, bool conversationOnly)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new ArgumentException("Absolute Codex executable required.");
        ProjectFiles.CheckAncestors(executable);
        Directory.CreateDirectory(logDirectory);
        if (!conversationOnly) requestsFile = Path.Combine(logDirectory, "requests.jsonl");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (Environment.GetEnvironmentVariable("LITTLELOOP_CODEX_HOME") is { Length: > 0 } serviceHome)
        {
            if (!Path.IsPathFullyQualified(serviceHome)) throw new ArgumentException("LITTLELOOP_CODEX_HOME must be absolute.");
            ProjectFiles.CheckAncestors(serviceHome);
            Directory.CreateDirectory(serviceHome);
            start.Environment["CODEX_HOME"] = serviceHome;
        }
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        if (conversationOnly)
        {
            foreach (string feature in new[] { "apps", "plugins", "hooks", "shell_tool", "view_image", "computer_use", "browser_use" })
            {
                start.ArgumentList.Add("--disable");
                start.ArgumentList.Add(feature);
            }
        }
        // Keep resumable runtime databases with this host session. The desktop's
        // personal CODEX_HOME may be read-only to a service or restricted process.
        string runtime = Path.GetFullPath(Path.Combine(logDirectory, "codex-runtime"));
        Directory.CreateDirectory(runtime);
        string temporary = Path.Combine(runtime, "tmp");
        Directory.CreateDirectory(temporary);
        // SQLite migrations also create temporary files. Service tokens may not
        // be able to write the interactive user's inherited TEMP directory.
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        start.Environment["TMPDIR"] = temporary;
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add("sqlite_home=" + JsonSerializer.Serialize(runtime));
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add("log_dir=" + JsonSerializer.Serialize(Path.Combine(runtime, "logs")));
        start.Environment.Remove("DEEPSEEK_API_KEY");
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("Workbench__", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LITTLELOOP_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ChatFcm__", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("GOOGLE_APPLICATION_CREDENTIALS", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Chat__", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ChatBot__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        process = Process.Start(start) ?? throw new IOException("Could not start Codex App Server.");
        reader = ReadAsync(conversationOnly ? null : Path.Combine(logDirectory, "events.jsonl"));
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
            if (requestsFile is not null) await File.AppendAllTextAsync(requestsFile, line + "\n", token);
            await process.StandardInput.WriteLineAsync(line.AsMemory(), token); await process.StandardInput.FlushAsync(token);
        }
        finally { send.Release(); }
    }
    private async Task ReadAsync(string? logPath)
    {
        Exception? failure = null; long bytes = 0;
        try
        {
            using var log = logPath is null ? null : new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read));
            while (await process.StandardOutput.ReadLineAsync(stopped.Token) is { } line)
            {
                if (line.Length > 2_000_000 || (bytes += line.Length) > 40_000_000) throw new IOException("App Server event budget exceeded.");
                if (log is not null) { await log.WriteLineAsync(line); await log.FlushAsync(); }
                using var doc = JsonDocument.Parse(line); var item = doc.RootElement.Clone();
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
