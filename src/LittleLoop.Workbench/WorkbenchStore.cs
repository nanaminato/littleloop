using System.Text.Json;
using MySqlConnector;

record Citation(string Path, int Start, int End, string Sha256, string Text);
record Message(Guid Id, string Role, string Text, Citation? Source, Guid? ReplyTo = null);
record Work(Guid Id, Guid MessageId, string Goal, string Status, string? Error = null);
record Conversation(Message[] Messages, Work[] Tasks);
record Ask(string Text, string Path, int Start, int End);
record Schedule(Guid MessageId, string Goal);
record Decision(string Nonce, string Value);

sealed class WorkbenchStore : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1);
    private readonly string file;
    private readonly string? connection;
    private MySqlConnection? databaseLease;

    public WorkbenchStore(string directory, string? connection)
    {
        file = Path.Combine(directory, "conversation.json");
        this.connection = connection;
    }

    public async Task Initialize()
    {
        if (connection is not null)
        {
            var settings = new MySqlConnectionStringBuilder(connection) { Pooling = false };
            if (string.IsNullOrWhiteSpace(settings.Database)) throw new InvalidOperationException("A dedicated MySQL database is required.");
            databaseLease = new MySqlConnection(settings.ConnectionString);
            await databaseLease.OpenAsync();
            await using var acquire = new MySqlCommand("SELECT GET_LOCK(SHA2(CONCAT('littleloop_workbench:', DATABASE()), 256), 0)", databaseLease);
            if (Convert.ToInt32(await acquire.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("Another workbench is using this MySQL database.");
            await using var ddl = new MySqlCommand("""
                CREATE TABLE IF NOT EXISTS littleloop_workbench (
                    id INT PRIMARY KEY, payload JSON NOT NULL
                )
                """, databaseLease);
            await ddl.ExecuteNonQueryAsync();
        }
        await Update(s => s with
        {
            Tasks = s.Tasks.Select(t => t.Status == "running"
                ? t with { Status = "needs-reconciliation", Error = "后台进程曾中断，请检查已执行的步骤。" } : t).ToArray()
        });
    }

    public Task<Conversation> Read() => Access(null);
    public Task<Conversation> Update(Func<Conversation, Conversation> change) => Access(change);

    private async Task<Conversation> Access(Func<Conversation, Conversation>? change)
    {
        await gate.WaitAsync();
        try
        {
            if (connection is null)
            {
                var state = Decode(File.Exists(file) ? await File.ReadAllTextAsync(file) : null);
                if (change is null) return state;
                state = change(state);
                await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(state, Json));
                File.Move(file + ".tmp", file, true);
                return state;
            }

            var db = databaseLease ?? throw new InvalidOperationException("MySQL store is not initialized.");
            await using var select = new MySqlCommand("SELECT payload FROM littleloop_workbench WHERE id=1", db);
            var current = Decode(await select.ExecuteScalarAsync() as string);
            if (change is null) return current;
            current = change(current);
            await using var write = new MySqlCommand("""
                INSERT INTO littleloop_workbench (id, payload) VALUES (1, @payload)
                ON DUPLICATE KEY UPDATE payload = @payload
                """, db);
            write.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(current, Json));
            await write.ExecuteNonQueryAsync();
            return current;
        }
        finally { gate.Release(); }
    }

    private static Conversation Decode(string? value) => value is null
        ? new([], []) : JsonSerializer.Deserialize<Conversation>(value, Json) ?? throw new IOException("Missing conversation data.");

    public async ValueTask DisposeAsync()
    {
        if (databaseLease is not null) await databaseLease.DisposeAsync();
        gate.Dispose();
    }
}
