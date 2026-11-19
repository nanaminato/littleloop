using System.Data.Common;
using System.Text;
using LittleLoop.Core;
using LittleLoop.Tools;

sealed record VerifiedGoalExperience(string GrantHash, string[] Paths)
{
    public static VerifiedGoalExperience? FromReceipts(ProjectFiles files, IEnumerable<LearningEpisode> episodes)
    {
        var paths = episodes.Reverse()
            .Select(episode => episode.Result)
            .Where(result => result is { Status: ExecutionStatus.Completed, ToolReceipt: { ToolId: "project.patch" } receipt } &&
                ProjectFiles.Same(receipt.WorkspaceRoot, files.Root))
            .SelectMany(result => result!.ToolReceipt!.Artifacts)
            .Where(artifact => artifact.Usage == "output")
            .Select(artifact => artifact.Path.Replace('\\', '/'))
            .Where(path => path.Length is > 0 and <= 180 &&
                path.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ' ') &&
                files.CanWrite(path))
            .Distinct(StringComparer.Ordinal).Take(3).ToArray();
        return paths.Length == 0 ? null : new(files.PolicyHash, paths);
    }
}

sealed record GoalExperienceHint(long SourceSequence, int SourceVersion, string[] Paths, string Match = "exact");

sealed partial class ChatStore
{
    internal static bool SupportsGoalExperience(BotWork work) => work is
        { Kind: "task", Goal: not null, Input: { Provider: null or BotName, Anchor: null, Source: null } };

    private static string GoalDigest(ContinuousGoal goal) => ProjectFiles.Hash(Encoding.UTF8.GetBytes(
        ToJson(new { goal.Objective, goal.Requirements })));

    public async Task<BotWork> PrepareGoalMemory(BotWork work, ProjectFiles files,
        ISemanticModelClient? semantics = null, CancellationToken token = default)
    {
        if (!SupportsGoalExperience(work)) return work;
        var grant = files.PolicyHash;
        var candidates = await ReadScopedLessonCandidates(work, grant);
        var lessons = SelectGoalLessons(candidates, work.Goal!);
        string? quote = null;
        if (lessons.Length == 0 && semantics is not null &&
            await ChatProcedureRecall.Select(semantics, work.Goal!, candidates, token) is { } match)
        {
            // No database transaction is held during inference. A correction, revocation or
            // version change while the provider was running invalidates the proposed source.
            candidates = await ReadScopedLessonCandidates(work, grant);
            lessons = SelectGoalLessons(candidates, work.Goal!);
            if (lessons.Length == 0 && candidates.Contains(match.Source))
            {
                lessons = [match.Source];
                quote = match.Quote;
            }
        }
        var hint = await ReadGoalExperience(work, grant);
        if (hint is not null)
        {
            var paths = hint.Paths.Where(path =>
            {
                // A verified path may since have moved or gained a linked ancestor.
                try { return files.CanWrite(path) && File.Exists(files.Resolve(path)); }
                catch (ArgumentException) { return false; }
            }).ToArray();
            hint = paths.Length == 0 ? null : hint with { Paths = paths };
        }
        return work with { ExperienceHint = hint, LessonHints = lessons, SemanticMatchQuote = quote };
    }

    private static async Task<bool> GoalExperienceEnabled(DbConnection db, DbTransaction tx, string room, string owner)
    {
        await using var query = Command(db, tx, """
            SELECT 1 FROM chat_goal_experience_settings WHERE room_id=@room AND owner_id=@owner
            """, ("room", room), ("owner", owner));
        return await query.ExecuteScalarAsync() is not null;
    }

    private static async Task SaveGoalExperience(DbConnection db, DbTransaction tx, BotWork work, VerifiedGoalExperience experience)
    {
        if (experience.Paths.Length == 0 || !await IsMember(db, tx, work.Room, work.Owner) ||
            !await GoalExperienceEnabled(db, tx, work.Room, work.Owner)) return;
        await using var insert = Command(db, tx, """
            INSERT INTO chat_goal_experiences(work_id,room_id,owner_id,source_version,grant_hash,goal_hash,paths)
            VALUES(@id,@room,@owner,@version,@grant,@goal,@paths)
            """, ("id", work.Id.ToString()), ("room", work.Room), ("owner", work.Owner),
            ("version", work.Goal!.Version), ("grant", experience.GrantHash), ("goal", GoalDigest(work.Goal)),
            ("paths", ToJson(experience.Paths)));
        await insert.ExecuteNonQueryAsync();
    }

    public async Task<GoalExperienceHint?> ReadGoalExperience(BotWork work, string grantHash)
    {
        if (!SupportsGoalExperience(work)) return null;
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        if (await LockGoalRoom(db, tx, work.Room) != "direct" || !await IsMember(db, tx, work.Room, work.Owner) ||
            !await GoalExperienceEnabled(db, tx, work.Room, work.Owner)) return null;
        var goal = work.Goal!;
        await using (var current = Command(db, tx, """
            SELECT w.goal FROM chat_bot_work w JOIN chat_bot_rooms b ON b.room_id=w.room_id AND b.enabled
            WHERE w.id=@id AND w.room_id=@room AND w.owner_id=@owner AND w.status='running'
            """, ("id", work.Id.ToString()), ("room", work.Room), ("owner", work.Owner)))
        {
            if (await current.ExecuteScalarAsync() is not string raw) return null;
            var persisted = FromJson<ContinuousGoal>(raw);
            if (persisted.Version != goal.Version || persisted.Objective != goal.Objective ||
                !persisted.Requirements.SequenceEqual(goal.Requirements))
                return null;
        }
        const string candidates = """
            SELECT w.seq,e.source_version,e.paths,w.goal FROM chat_goal_experiences e
            JOIN chat_bot_work w ON w.id=e.work_id AND w.status='completed'
            JOIN chat_messages m ON m.room_id=w.room_id AND m.seq=w.seq AND NOT m.deleted AND NOT m.edited
            WHERE e.room_id=@room AND e.owner_id=@owner AND e.grant_hash=@grant
            """;
        // Keep exact lookup independent of the recency window for approximate retrieval.
        await using (var exact = Command(db, tx, candidates + " AND e.goal_hash=@goal ORDER BY w.seq DESC LIMIT 1",
            ("room", work.Room), ("owner", work.Owner), ("grant", grantHash), ("goal", GoalDigest(goal))))
        await using (var match = await exact.ExecuteReaderAsync())
            if (await match.ReadAsync())
                return new(match.GetInt64(0), match.GetInt32(1), FromJson<string[]>(match.GetString(2)));

        await using var query = Command(db, tx, candidates + " ORDER BY w.seq DESC LIMIT 32",
            ("room", work.Room), ("owner", work.Owner), ("grant", grantHash));
        await using var reader = await query.ExecuteReaderAsync();
        GoalExperienceHint? best = null;
        int bestScore = 0;
        bool ambiguous = false;
        while (await reader.ReadAsync())
        {
            var prior = FromJson<ContinuousGoal>(reader.GetString(3));
            if (prior.Version != reader.GetInt32(1)) continue;
            int score = ConversationMemoryPolicy.ExperienceScore(prior, goal);
            if (score <= 0 || score < bestScore) continue;
            var paths = FromJson<string[]>(reader.GetString(2));
            if (score == bestScore)
            {
                if (!paths.ToHashSet(StringComparer.Ordinal).SetEquals(best!.Paths)) ambiguous = true;
                continue;
            }
            bestScore = score;
            ambiguous = false;
            best = new(reader.GetInt64(0), prior.Version, paths, "related");
        }
        return ambiguous ? null : best;
    }

    private static async Task<string> HandleGoalExperience(DbConnection db, DbTransaction tx, string room, string owner,
        long? replyTo, string command, string? grantHash)
    {
        if (command == "启用经验")
        {
            await using var enable = Command(db, tx, """
                INSERT IGNORE INTO chat_goal_experience_settings(room_id,owner_id) VALUES(@room,@owner)
                """, ("room", room), ("owner", owner));
            await enable.ExecuteNonQueryAsync();
            return "已启用经验提炼与复用。";
        }
        if (command == "停用经验")
        {
            await using var disable = Command(db, tx, "DELETE FROM chat_goal_experience_settings WHERE room_id=@room AND owner_id=@owner",
                ("room", room), ("owner", owner));
            await disable.ExecuteNonQueryAsync();
            return "已停用后续经验提炼与复用。";
        }
        if (command == "撤回经验")
        {
            await using var card = Command(db, tx, "SELECT card FROM chat_messages WHERE room_id=@room AND seq=@seq AND NOT deleted",
                ("room", room), ("seq", replyTo));
            if (await card.ExecuteScalarAsync() is not string raw || FromJson<MessageCard>(raw).TaskId is not { } source)
                return "请回复源任务卡发送“撤回经验”。";
            await using var revoke = Command(db, tx, "DELETE FROM chat_goal_experiences WHERE room_id=@room AND owner_id=@owner AND work_id=@id",
                ("room", room), ("owner", owner), ("id", source.ToString()));
            return await revoke.ExecuteNonQueryAsync() == 1 ? "已撤回该任务的经验。" : "这张任务卡没有可撤回的经验。";
        }

        bool enabled = await GoalExperienceEnabled(db, tx, room, owner);
        await using var select = Command(db, tx, """
            SELECT w.seq,e.source_version,e.paths FROM chat_goal_experiences e
            JOIN chat_bot_work w ON w.id=e.work_id AND w.status='completed'
            JOIN chat_messages m ON m.room_id=w.room_id AND m.seq=w.seq AND NOT m.deleted AND NOT m.edited
            WHERE e.room_id=@room AND e.owner_id=@owner AND e.grant_hash=@grant ORDER BY w.seq DESC LIMIT 5
            """, ("room", room), ("owner", owner), ("grant", grantHash));
        await using var reader = await select.ExecuteReaderAsync();
        var entries = new List<string>();
        while (await reader.ReadAsync())
            entries.Add($"任务消息 #{reader.GetInt64(0)} v{reader.GetInt32(1)} · {string.Join("、", FromJson<string[]>(reader.GetString(2)))}");
        return (enabled ? "自动补丁经验已启用。" : "自动补丁经验已停用。") +
            (entries.Count == 0 ? "暂无可查看的经验。" : "\n" + string.Join("\n", entries));
    }
}
