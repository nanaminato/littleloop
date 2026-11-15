using System.Security.Claims;

sealed record RoomMember(string Name, string DisplayName, bool Robot);

sealed partial class ChatStore
{
    public async Task<RoomMember[]?> Members(string room, string user)
    {
        await using var db = await Open();
        if (!await IsMember(db, null, room, user)) return null;
        await using var command = Command(db, null, """
            SELECT a.name,COALESCE(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.displayName')),''),a.name),a.password=@robot
            FROM chat_rooms r JOIN chat_members m ON r.id=m.room_id JOIN chat_accounts a ON a.id=m.user_id
            LEFT JOIN chat_user_settings s ON s.user_id=a.id AND s.section='profile'
            WHERE r.id=@room AND (r.type<>'channel' OR a.id=r.owner_id) ORDER BY a.name
            """, ("room", room), ("robot", RobotCredential));
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<RoomMember>();
        while (await reader.ReadAsync())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        return result.ToArray();
    }
}
