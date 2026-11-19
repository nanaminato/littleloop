using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using LittleLoop.Core;

sealed record GoalLessonHint(long SourceSequence, string Context, string Value, MemorySubject Subject = MemorySubject.DocumentWhitespace);

sealed partial class ChatStore
{
    private static async Task UpgradeConversationMemory(DbConnection db)
    {
        await using var create = Command(db, null, """
            CREATE TABLE IF NOT EXISTS chat_goal_notice_overrides (
                work_id CHAR(36) PRIMARY KEY, room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL,
                source_seq BIGINT NOT NULL, include_objective BOOLEAN NOT NULL, INDEX(room_id,owner_id,source_seq),
                FOREIGN KEY(work_id) REFERENCES chat_bot_work(id),
                FOREIGN KEY(room_id,source_seq) REFERENCES chat_messages(room_id,seq)
            );
            CREATE TABLE IF NOT EXISTS chat_goal_preference_reviews (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL, since_seq BIGINT NOT NULL,
                pending_seq BIGINT NULL, include_objective BOOLEAN NULL, PRIMARY KEY(room_id,owner_id)
            );
            CREATE TABLE IF NOT EXISTS chat_goal_lessons (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL, grant_hash CHAR(64) NOT NULL,
                subject VARCHAR(32) NOT NULL, context_key CHAR(64) NOT NULL, context_text TEXT CHARACTER SET utf8mb4 NOT NULL,
                lesson_value TEXT CHARACTER SET utf8mb4 NOT NULL, source_seq BIGINT NOT NULL,
                PRIMARY KEY(room_id,owner_id,grant_hash,subject,context_key), INDEX(room_id,owner_id,grant_hash,source_seq),
                FOREIGN KEY(room_id,source_seq) REFERENCES chat_messages(room_id,seq)
            );
            CREATE TABLE IF NOT EXISTS chat_goal_lesson_pauses (
                room_id CHAR(36) NOT NULL, owner_id CHAR(36) NOT NULL, PRIMARY KEY(room_id,owner_id)
            );
            """);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task<bool> LessonsPaused(DbConnection db, DbTransaction tx, string room, string owner)
    {
        await using var query = Command(db, tx, """
            SELECT 1 FROM chat_goal_lesson_pauses WHERE room_id=@room AND owner_id=@owner
            """, ("room", room), ("owner", owner));
        return await query.ExecuteScalarAsync() is not null;
    }

    private static async Task<string> ManageGoalKnowledge(DbConnection db, DbTransaction tx, string room, string owner,
        long? replyTo, string command, string? grantHash)
    {
        if (command is "停用经验" or "启用经验")
        {
            string sql = command == "停用经验"
                ? "INSERT IGNORE INTO chat_goal_lesson_pauses(room_id,owner_id) VALUES(@room,@owner)"
                : "DELETE FROM chat_goal_lesson_pauses WHERE room_id=@room AND owner_id=@owner";
            await using var update = Command(db, tx, sql, ("room", room), ("owner", owner));
            await update.ExecuteNonQueryAsync();
        }
        if (command == "撤回经验" && replyTo is null) return "请回复自己的经验来源消息或原任务卡发送“撤回经验”。";
        if (command == "撤回经验")
        {
            await using var remove = Command(db, tx, """
                DELETE FROM chat_goal_lessons WHERE room_id=@room AND owner_id=@owner AND source_seq=@seq
                """, ("room", room), ("owner", owner), ("seq", replyTo));
            if (await remove.ExecuteNonQueryAsync() != 0) return "已撤回这条消息形成的任务经验。";
        }
        string response = await HandleGoalExperience(db, tx, room, owner, replyTo, command, grantHash);
        if (command != "查看经验" || grantHash is null) return response;
        var lessons = await LoadGoalLessons(db, tx, room, owner, grantHash, 5);
        if (lessons.Length == 0) return response;
        string status = await LessonsPaused(db, tx, room, owner) ? "已暂停" : "可用于匹配任务";
        return response + "\n你在本项目给出的做法（" + status + "）\n" +
            string.Join("\n", lessons.Select(lesson => $"消息 #{lesson.SourceSequence} · {lesson.Context} · {LessonDescription(lesson)}"));
    }

    private static string LessonDescription(GoalLessonHint lesson) => lesson.Subject == MemorySubject.DocumentWhitespace
        ? (lesson.Value == "preserve" ? "保留行尾空格" : "清除行尾空格") : lesson.Value;

    private static async Task<string> SaveGoalLesson(DbConnection db, DbTransaction tx, string room, string owner,
        long sequence, string? grantHash, MemoryObservation observation)
    {
        if (string.IsNullOrEmpty(grantHash)) return "未取得当前项目授权范围，无法保存这条项目经验。";
        if (await LessonsPaused(db, tx, room, owner)) return "经验已停用。可以发送“启用经验”后重新说明这条做法。";
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(observation.Context.Normalize().ToLowerInvariant())));
        await using var save = Command(db, tx, """
            INSERT INTO chat_goal_lessons(room_id,owner_id,grant_hash,subject,context_key,context_text,lesson_value,source_seq)
            VALUES(@room,@owner,@grant,@subject,@key,@context,@value,@seq)
            ON DUPLICATE KEY UPDATE context_text=@context,lesson_value=@value,source_seq=@seq
            """, ("room", room), ("owner", owner), ("grant", grantHash), ("subject", observation.Subject.ToString()),
            ("key", key), ("context", observation.Context), ("value", observation.Value), ("seq", sequence));
        await save.ExecuteNonQueryAsync();
        var lesson = new GoalLessonHint(sequence, observation.Context, observation.Value, observation.Subject);
        return $"已记下本项目的做法：{lesson.Context} · {LessonDescription(lesson)}。回复你的原消息 #{sequence} 发送“撤回经验”可移除。";
    }

    private static async Task<GoalLessonHint[]> LoadGoalLessons(DbConnection db, DbTransaction tx, string room, string owner,
        string grantHash, int limit)
    {
        await using var select = Command(db, tx, """
            SELECT l.source_seq,l.context_text,l.lesson_value,l.subject FROM chat_goal_lessons l
            JOIN chat_messages m ON m.room_id=l.room_id AND m.seq=l.source_seq AND m.sender_id=l.owner_id
            WHERE l.room_id=@room AND l.owner_id=@owner AND l.grant_hash=@grant AND NOT m.deleted AND NOT m.edited
            ORDER BY l.source_seq DESC LIMIT @limit
            """, ("room", room), ("owner", owner), ("grant", grantHash), ("limit", limit));
        var lessons = new List<GoalLessonHint>();
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (Enum.TryParse<MemorySubject>(reader.GetString(3), out var subject))
                lessons.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), subject));
        return lessons.ToArray();
    }

    internal async Task<GoalLessonHint[]> ReadScopedLessonCandidates(BotWork work, string grantHash)
    {
        if (!SupportsGoalExperience(work)) return [];
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        if (await LockGoalRoom(db, tx, work.Room) != "direct" || !await IsMember(db, tx, work.Room, work.Owner) ||
            await LessonsPaused(db, tx, work.Room, work.Owner)) return [];
        await using var current = Command(db, tx, """
            SELECT w.goal FROM chat_bot_work w JOIN chat_bot_rooms b ON b.room_id=w.room_id AND b.enabled
            WHERE w.id=@id AND w.room_id=@room AND w.owner_id=@owner AND w.status='running'
            """, ("id", work.Id.ToString()), ("room", work.Room), ("owner", work.Owner));
        if (await current.ExecuteScalarAsync() is not string raw) return [];
        var goal = FromJson<ContinuousGoal>(raw);
        if (goal.Version != work.Goal!.Version || goal.Objective != work.Goal.Objective ||
            !goal.Requirements.SequenceEqual(work.Goal.Requirements, StringComparer.Ordinal)) return [];
        return await LoadGoalLessons(db, tx, work.Room, work.Owner, grantHash, 64);
    }

    public async Task<GoalLessonHint[]> ReadGoalLessons(BotWork work, string grantHash)
    {
        var lessons = await ReadScopedLessonCandidates(work, grantHash);
        return SelectGoalLessons(lessons, work.Goal);
    }

    internal static GoalLessonHint[] SelectGoalLessons(GoalLessonHint[] candidates, ContinuousGoal? goal)
    {
        if (goal is null) return [];
        var relevant = candidates.Where(lesson => ConversationMemoryPolicy.Applies(
            new(lesson.Subject, MemoryScope.Durable, lesson.Value, lesson.Context), goal)).ToArray();
        // If both the general document default and a format-specific exception match, use the specific one.
        var formatting = relevant.Where(lesson => lesson.Subject == MemorySubject.DocumentWhitespace)
            .OrderBy(lesson => lesson.Context == "document").FirstOrDefault();
        return relevant.Where(lesson => lesson.Subject != MemorySubject.DocumentWhitespace || lesson == formatting)
            .Take(3).ToArray();
    }
}
