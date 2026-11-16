sealed partial class ChatStore
{
    // Authorship is checked against immutable account IDs, never display names or client envelopes.
    public async Task<bool> IsCommitmentAuthor(string room, long sequence, string owner)
    {
        await using var db = await Open();
        await using var cmd = Command(db, null, """
            SELECT COUNT(*) FROM chat_messages msg JOIN chat_rooms r ON r.id=msg.room_id
            WHERE msg.room_id=@room AND msg.seq=@seq AND msg.sender_id=@owner AND r.type='direct'
            AND NOT msg.deleted AND NOT msg.edited AND msg.forwarded_from IS NULL
            AND EXISTS(SELECT 1 FROM chat_members m WHERE m.room_id=msg.room_id AND m.user_id=@owner)
            """, ("room", room), ("seq", sequence), ("owner", owner));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 1;
    }
}
