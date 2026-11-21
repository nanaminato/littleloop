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
        string owner, BotGoalSnapshot target, BotGoalEvidence? current = null)
    {
        var assessed = (target.Goal.Obligations ?? []).Where(condition => condition.Status == GoalObligationStatus.Satisfied &&
            condition.Assessment?.CompletionNotice != true).ToArray();
        if (assessed.Length == 0) return [];

        // A long-running goal may have rolled its cited proof out of the conversational evidence window.
        var dispatchAssessments = assessed.Where(condition => condition.Assessment?.DispatchId is not null)
            .Select(condition => condition.Assessment!).ToArray();
        var dispatchEvidence = dispatchAssessments.Length == 0 ? null :
            await ReadCitedEvidence(db, tx, room, owner, target,
                new(BotAction.Complete, target.Id, ObligationAssessments: dispatchAssessments),
                current ?? await ReadDispatchEvidence(db, tx, room, owner, target));
        var checkIds = assessed.Select(condition => condition.Assessment?.CheckId).OfType<Guid>().Distinct()
            .Select((id, index) => (Name: $"check{index}", Value: (object?)id.ToString())).ToArray();
        var observed = new List<BotObservation>();
        if (checkIds.Length > 0)
        {
            string checks = string.Join(",", checkIds.Select(entry => "@" + entry.Name));
            await using var evidence = Command(db, tx, $"""
                SELECT e.observation FROM chat_bot_events e JOIN chat_messages m ON m.room_id=e.room_id AND m.seq=e.seq
                WHERE e.room_id=@room AND e.owner_id=@owner AND e.work_id=@work AND NOT m.deleted AND NOT m.edited
                    AND e.kind='Timer' AND JSON_UNQUOTE(JSON_EXTRACT(e.observation,'$.checkId')) IN ({checks})
                """, [("room", room), ("owner", owner), ("work", target.Id.ToString()), .. checkIds]);
            await using var rows = await evidence.ExecuteReaderAsync();
            while (await rows.ReadAsync()) observed.Add(FromJson<BotObservation>(rows.GetString(0)));
        }
        return assessed.Where(condition => !HasProof(condition)).Select(condition => condition.Id).ToArray();

        bool HasProof(GoalObligation condition)
        {
            var assessment = condition.Assessment;
            if (assessment is null || assessment.Id != condition.Id || condition.GoalId != target.Id) return false;
            if (assessment.EventId.HasValue)
                return dispatchEvidence?.Dispatches.FirstOrDefault(dispatch => dispatch.DispatchId == assessment.DispatchId) is { } dispatch &&
                    GoalObligationPolicy.SupportsRecordedEvidence(condition, assessment, dispatch, target.Goal.Version);
            return assessment.CheckId is Guid checkId && condition.Check is { Observed: true } check && check.Id == checkId &&
                observed.Any(fact => fact.Kind == BotEventKind.Timer && fact.Status == "due" && fact.GoalId == target.Id &&
                    fact.WakeObligationId == condition.Id && fact.CheckId == check.Id && fact.GoalVersion == check.GoalVersion &&
                    fact.WakeAtUnixMilliseconds == check.DueAtUnixMilliseconds);
        }
    }
}
