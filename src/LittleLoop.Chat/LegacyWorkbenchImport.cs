using LittleLoop.Tools;
using MySqlConnector;

static class LegacyWorkbenchImport
{
    private sealed record LegacyMessage(Guid Id, string Role, string Text, SourceCitation? Source, Guid? ReplyTo);
    private sealed record LegacyTask(Guid Id, Guid MessageId, string Goal, string Status, string? Error);
    private sealed record LegacyConversation(LegacyMessage[] Messages, LegacyTask[] Tasks);

    public static async Task Import(ChatStore store, Account robot, IConfiguration config)
    {
        string? legacy = config.GetConnectionString("LegacyWorkbench");
        string? snapshot = config["ChatBot:LegacySnapshot"];
        if (legacy is null && snapshot is null) return;
        if (legacy is not null && snapshot is not null) throw new InvalidOperationException("Choose one legacy source.");
        var ownerName = config["ChatBot:LegacyOwner"] ?? throw new InvalidOperationException("Set ChatBot__LegacyOwner.");
        var owner = await store.ResolveConfiguredAccount(ownerName.Trim().ToLowerInvariant()) ?? throw new InvalidOperationException("Legacy owner account does not exist.");
        if (owner.Id == robot.Id) throw new InvalidOperationException("Legacy owner must be a user.");
        string payload;
        if (snapshot is not null)
        {
            if (new FileInfo(snapshot).Length > 16 * 1024 * 1024) throw new IOException("Legacy snapshot is too large.");
            payload = await File.ReadAllTextAsync(snapshot);
        }
        else
        {
            await using var source = new MySqlConnection(legacy);
            await source.OpenAsync();
            await using var cmd = new MySqlCommand("SELECT IF(OCTET_LENGTH(payload)<=16777216,payload,NULL) FROM littleloop_workbench WHERE id=1", source);
            payload = await cmd.ExecuteScalarAsync() as string ?? throw new IOException("No bounded legacy workbench payload.");
        }
        var history = ChatStore.FromJson<LegacyConversation>(payload) ?? throw new IOException("Invalid workbench payload.");
        var room = await store.CreateRoom(owner, new("legacy", "历史工作台", []));
        await store.AddLegacyRobot(room.Id, owner.Id, robot.Id);
        var sequence = new Dictionary<Guid, long>();
        foreach (var message in history.Messages)
        {
            var sender = message.Role == "assistant" ? robot : owner;
            long? reply = message.ReplyTo is { } parent ? sequence.GetValueOrDefault(parent) : null;
            var saved = await store.Send(sender.Id, sender.Name, room.Id, new(message.Id.ToString(), message.Text, reply is 0 ? null : reply),
                card: message.Source is null ? null : new("citation", message.Source));
            sequence.Add(message.Id, saved?.Message.Sequence ?? throw new IOException("Failed importing legacy message."));
        }

        foreach (var item in history.Tasks)
        {
            string status = item.Status is "running" or "queued" ? "needs-reconciliation" : item.Status;
            var work = new BotWork(item.Id, room.Id, owner.Id, 0, "legacy", status, new(item.Goal), item.Error);
            long parent = sequence.GetValueOrDefault(item.MessageId);
            if (await store.Send(robot.Id, robot.Name, room.Id, new(item.Id.ToString(), item.Goal, parent == 0 ? null : parent),
                card: new("task", TaskId: item.Id), job: work) is null)
                throw new IOException("Failed importing legacy task.");
        }
    }
}
