using System.Data.Common;
using LittleLoop.Core;

sealed partial class ChatStore
{
    private static async Task<GoalObligationSource[]> ReadObligationSources(DbConnection db, DbTransaction tx,
        DecisionInput input, BotGoalSnapshot? target)
    {
        var sequences = new HashSet<long>();
        if (target is not null)
        {
            if (target.SourceSequence is long original) sequences.Add(original);
            foreach (var condition in target.Goal.Obligations ?? []) sequences.Add(condition.SourceSequence);
            foreach (long pending in target.Goal.PendingRequirementSources ?? []) sequences.Add(pending);
            if (target.Goal.Obligations is null)
            {
                // Read the provenance of pre-upgrade requirements from the existing versioned ledger.
                await using var revisions = Command(db, tx, """
                    SELECT DISTINCT seq FROM chat_goal_events WHERE room_id=@room AND work_id=@work
                        AND LOWER(action)='revise' ORDER BY seq
                    """, ("room", input.Room), ("work", target.Id.ToString()));
                await using var rows = await revisions.ExecuteReaderAsync();
                while (await rows.ReadAsync()) sequences.Add(rows.GetInt64(0));
            }
        }

        var sources = new List<GoalObligationSource>();
        if (sequences.Count > 0)
        {
            var keys = sequences.Order().Select((sequence, index) => (Name: $"source{index}", Value: (object?)sequence)).ToArray();
            var names = string.Join(",", keys.Select(key => "@" + key.Name));
            await using var original = Command(db, tx, $"""
                SELECT seq,body FROM chat_messages WHERE room_id=@room AND sender_id=@owner
                    AND seq IN ({names}) AND NOT edited AND NOT deleted ORDER BY seq
                """, [("room", input.Room), ("owner", input.Owner), .. keys]);
            await using var rows = await original.ExecuteReaderAsync();
            while (await rows.ReadAsync()) sources.Add(new(rows.GetInt64(0), rows.GetString(1), target!.Id));
        }
        if (input.Trigger.Kind == BotEventKind.UserMessage && !sequences.Contains(input.Sequence))
            sources.Add(new(input.Sequence, input.Trigger.Text));
        return sources.ToArray();
    }

    private static async Task<Guid[]> ReadUnavailableObligationEvidence(DbConnection db, DbTransaction tx, string room,
        string owner, BotGoalSnapshot target)
    {
        var assessed = (target.Goal.Obligations ?? []).Where(condition => condition.Status == GoalObligationStatus.Satisfied &&
            condition.Assessment?.CompletionNotice != true).ToArray();
        if (assessed.Length == 0) return [];

        // A long-running goal may have rolled this proof out of the conversational evidence window.
        var eventIds = assessed.Select(condition => condition.Assessment?.EventId).OfType<long>().Distinct()
            .Select((id, index) => (Name: $"event{index}", Value: (object?)id)).ToArray();
        var checkIds = assessed.Select(condition => condition.Assessment?.CheckId).OfType<Guid>().Distinct()
            .Select((id, index) => (Name: $"check{index}", Value: (object?)id.ToString())).ToArray();
        string receipts = eventIds.Length == 0 ? "1=0" : $"e.id IN ({Parameters(eventIds)})";
        string checks = checkIds.Length == 0 ? "1=0" :
            $"e.kind='Timer' AND JSON_UNQUOTE(JSON_EXTRACT(e.observation,'$.checkId')) IN ({Parameters(checkIds)})";
        await using var evidence = Command(db, tx, $"""
            SELECT e.id,e.observation FROM chat_bot_events e JOIN chat_messages m ON m.room_id=e.room_id AND m.seq=e.seq
            WHERE e.room_id=@room AND e.owner_id=@owner AND e.work_id=@work AND NOT m.deleted AND NOT m.edited
                AND (({receipts}) OR ({checks}))
            """, [("room", room), ("owner", owner), ("work", target.Id.ToString()), .. eventIds, .. checkIds]);

        var observed = new Dictionary<long, BotObservation>();
        await using var rows = await evidence.ExecuteReaderAsync();
        while (await rows.ReadAsync()) observed.Add(rows.GetInt64(0), FromJson<BotObservation>(rows.GetString(1)));
        return assessed.Where(condition => !HasProof(condition)).Select(condition => condition.Id).ToArray();

        bool HasProof(GoalObligation condition)
        {
            var assessment = condition.Assessment;
            if (assessment is null || assessment.Id != condition.Id || condition.GoalId != target.Id) return false;
            if (assessment.EventId is long eventId &&
                (!observed.TryGetValue(eventId, out var fact) || fact.GoalId != target.Id ||
                 fact.DispatchId != assessment.DispatchId || fact.GoalVersion is not int version ||
                 version < condition.CreatedVersion || version > target.Goal.Version ||
                 !MatchesExpectedOutcome(fact, condition, assessment))) return false;
            if (assessment.CheckId is Guid checkId && (condition.Check is not { Observed: true } check || check.Id != checkId ||
                !observed.Values.Any(fact => fact.Kind == BotEventKind.Timer && fact.Status == "due" && fact.GoalId == target.Id &&
                    fact.WakeObligationId == condition.Id && fact.CheckId == check.Id && fact.GoalVersion == check.GoalVersion &&
                    fact.WakeAtUnixMilliseconds == check.DueAtUnixMilliseconds))) return false;
            return assessment.EventId.HasValue || assessment.CheckId.HasValue;
        }

        static bool MatchesExpectedOutcome(BotObservation fact, GoalObligation condition, GoalObligationAssessment assessment) =>
            assessment.ExpectedToolStatus is null
                ? fact is { Kind: BotEventKind.HarnessReceipt, Status: "returned" } or
                    { Kind: BotEventKind.ToolResult, Status: "completed" }
                : assessment.ExpectedToolStatus == "failed" &&
                    condition.Kind is GoalObligationKind.Behavior or GoalObligationKind.Constraint &&
                    fact is { Kind: BotEventKind.ToolResult, Status: "failed" };

        static string Parameters((string Name, object? Value)[] entries) => string.Join(",", entries.Select(entry => "@" + entry.Name));
    }
}
