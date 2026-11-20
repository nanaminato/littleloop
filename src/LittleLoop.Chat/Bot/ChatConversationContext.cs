using System.Data.Common;
using System.Text;

sealed partial class ChatStore
{
    private enum NoteCommand { None, Save, Show, Clear }

    private static (NoteCommand Command, string? Value) ParseConversationNote(string body)
    {
        const string prefix = "记住上下文";
        string text = body.Trim();
        if (text.StartsWith(prefix + "：", StringComparison.Ordinal) || text.StartsWith(prefix + ":", StringComparison.Ordinal))
        {
            string value = text[(prefix.Length + 1)..].Trim();
            return (NoteCommand.Save, value.Length > 0 && value.EnumerateRunes().Count() <= 512 ? value : null);
        }
        return text.TrimEnd('?', '？', '。').TrimEnd() switch
        {
            "查看上下文" => (NoteCommand.Show, null),
            "清除上下文" => (NoteCommand.Clear, null),
            _ => (NoteCommand.None, null)
        };
    }

    private const string ConversationNoteSource = """
        SELECT m.body FROM chat_messages m
        JOIN chat_rooms r ON r.id=m.room_id AND r.type='direct'
        JOIN chat_members cm ON cm.room_id=m.room_id AND cm.user_id=m.sender_id
        JOIN chat_accounts a ON a.id=m.sender_id AND a.password<>@robotPassword
        WHERE m.room_id=@room AND m.sender_id=@owner AND NOT m.deleted
        AND m.reply_to IS NULL AND m.forwarded_from IS NULL AND m.card IS NULL AND m.attachment_id IS NULL
        """;

    public async Task<string?> ReadConversationNote(BotWork work)
    {
        if (work.Kind != "chat") return null;
        await using var db = await Open();
        return await ReadConversationNote(db, null, work.Room, work.Owner);
    }

    private static async Task<string?> ReadConversationNote(DbConnection db, DbTransaction? tx, string room, string owner)
    {
        await using var source = Command(db, tx, ConversationNoteSource +
            " AND m.seq=(SELECT source_seq FROM chat_conversation_notes WHERE room_id=@room AND owner_id=@owner)",
            ("room", room), ("owner", owner), ("robotPassword", RobotCredential));
        return await source.ExecuteScalarAsync() is string body ? ParseConversationNote(body).Value : null;
    }

    private static async Task<string?> HandleConversationNote(DbConnection db, DbTransaction tx, string room,
        string owner, long seq, string sentText)
    {
        var requested = ParseConversationNote(sentText).Command;
        if (requested == NoteCommand.None) return null;
        // The inbox is a snapshot. Validate the live message under the caller's room lock.
        await using var source = Command(db, tx, ConversationNoteSource + " AND m.seq=@seq",
            ("room", room), ("owner", owner), ("seq", seq), ("robotPassword", RobotCredential));
        var current = await source.ExecuteScalarAsync() is string body ? ParseConversationNote(body) : default;
        if (current.Command != requested) return "请单独发送一条文字消息来管理这段私聊的上下文。";
        switch (current.Command)
        {
            case NoteCommand.Show:
                return await ReadConversationNote(db, tx, room, owner) is { } value
                    ? "这段私聊记住的上下文是：" + value : "这段私聊还没有保存上下文。";
            case NoteCommand.Clear:
                await using (var remove = Command(db, tx, "DELETE FROM chat_conversation_notes WHERE room_id=@room AND owner_id=@owner",
                    ("room", room), ("owner", owner)))
                    if (await remove.ExecuteNonQueryAsync() != 0) await AdvanceRevision();
                return "已清除这段私聊保存的说明，近期聊天记录仍可用于追问。";
            default:
                if (current.Value is null) return "请在“记住上下文：”后写下 1 到 512 个字符。";
                await using (var save = Command(db, tx, """
                    INSERT INTO chat_conversation_notes(room_id,owner_id,source_seq) VALUES(@room,@owner,@seq)
                    ON DUPLICATE KEY UPDATE source_seq=@seq
                    """, ("room", room), ("owner", owner), ("seq", seq))) await save.ExecuteNonQueryAsync();
                await AdvanceRevision();
                return "已记住，这段私聊之后会用到这条上下文。";
        }

        async Task AdvanceRevision()
        {
            await using var update = Command(db, tx, "UPDATE chat_rooms SET revision=revision+1 WHERE id=@room", ("room", room));
            await update.ExecuteNonQueryAsync();
        }
    }
}
