using System.Data.Common;
using System.Text.Json;
using LittleLoop.Core;

sealed partial class ChatStore
{
    private const int FactPageSize = 5;

    private async Task<BotFactQueryResponse> ReadGoalFacts(DecisionSnapshot snapshot, BotFactQuery query, string robot,
        bool operatorAccess, CancellationToken token)
    {
        var input = snapshot.Input;
        if (!snapshot.Context.Goals.Any(goal => goal.Id == query.GoalId) ||
            (input.Trigger.GoalId ?? snapshot.Context.ReplyGoalId) is Guid addressed && addressed != query.GoalId)
            return new(query)
            {
                Error = new("unknown-fact-query-goal", "fact-query", "Use the visible goal addressed by this decision.", "factQuery.goalId")
            };

        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync(token);
        var type = await LockGoalRoom(db, tx, input.Room);
        var (error, work) = await DecisionCommitGate(db, tx, snapshot, robot, operatorAccess, type, query.GoalId);
        if (error is not null) throw new BotDecisionSupersededException(error);
        var observed = snapshot.Context.Goals.First(goal => goal.Id == query.GoalId);
        var target = observed with { Goal = work!.Goal!, SourceSequence = work.Sequence };
        var result = query.Kind == BotFactQueryKind.Requirements
            ? await ReadRequirementFacts(db, tx, input, target, query)
            : await ReadEventFacts(db, tx, input, target, query, token);
        await tx.CommitAsync(token);
        return result;
    }

    private static async Task<BotFactQueryResponse> ReadRequirementFacts(DbConnection db, DbTransaction tx,
        DecisionInput input, BotGoalSnapshot target, BotFactQuery query)
    {
        var unavailable = await ReadUnavailableObligationEvidence(db, tx, input.Room, input.Owner, target);
        var conditions = (target.Goal.Obligations ?? []).Where(condition => condition.Active)
            .Select(condition => new BotRequirementFact(condition.Id, condition.Kind, Cut(condition.Description, 220),
                condition.SourceSequence, condition.CreatedVersion,
                unavailable.Contains(condition.Id) ? GoalObligationStatus.Unknown : condition.Status,
                condition.DelaySeconds, condition.Check, condition.Assessment?.DispatchId, condition.Assessment?.EventId))
            .OrderBy(condition => condition.Status == GoalObligationStatus.Satisfied).ToArray();
        int offset = Math.Clamp(query.Offset ?? 0, 0, conditions.Length);
        var page = conditions.Skip(offset).Take(FactPageSize).ToArray();
        var pending = target.Goal.PendingRequirementSources ?? [];
        var relevant = page.Select(condition => condition.SourceSequence).Concat(pending.Take(FactPageSize)).ToHashSet();
        var sources = await ReadObligationSources(db, tx, input, target);
        return new(query, target.Goal.Version)
        {
            Objective = Cut(target.Goal.Objective, 350),
            Conditions = page,
            Sources = sources.Where(source => relevant.Contains(source.Sequence)).Take(FactPageSize)
                .Select(source => source with { Text = Cut(source.Text, 300) }).ToArray(),
            PendingSourceSequences = pending,
            OpenConditionCount = conditions.Count(condition => condition.Status != GoalObligationStatus.Satisfied),
            NextOffset = offset + page.Length < conditions.Length ? offset + page.Length : null
        };
    }

    private static async Task<BotFactQueryResponse> ReadEventFacts(DbConnection db, DbTransaction tx,
        DecisionInput input, BotGoalSnapshot target, BotFactQuery query, CancellationToken token)
    {
        await using var cmd = Command(db, tx, """
            SELECT e.id,e.observation,CASE WHEN e.kind='BotDecision' THEN e.detail END
            FROM chat_bot_events e JOIN chat_messages m ON m.room_id=e.room_id AND m.seq=e.seq
            WHERE e.room_id=@room AND e.owner_id=@owner AND e.work_id=@goal AND NOT m.deleted AND NOT m.edited
              AND e.kind IN ('BotDecision','HarnessReceipt','ToolResult','Timer')
              AND (@before IS NULL OR e.id<@before)
              AND (@dispatch IS NULL OR JSON_UNQUOTE(JSON_EXTRACT(e.observation,'$.dispatchId'))=@dispatch)
            ORDER BY e.id DESC LIMIT @limit
            """, ("room", input.Room), ("owner", input.Owner), ("goal", target.Id.ToString()),
            ("before", query.BeforeEventId), ("dispatch", query.DispatchId?.ToString()), ("limit", FactPageSize + 1));
        var rows = new List<BotScopedFact>();
        await using (var reader = await cmd.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var observation = FromJson<BotObservation>(reader.GetString(1));
                if (observation.GoalId != target.Id) continue;
                string? instruction = null;
                Guid? review = null;
                if (!reader.IsDBNull(2))
                {
                    using var json = JsonDocument.Parse(reader.GetString(2));
                    instruction = ReadText(json.RootElement, "instruction");
                    review = ReadId(json.RootElement, "reviewDispatchId");
                }
                var fact = Fact(new(reader.GetInt64(0), observation, instruction), 250)!;
                rows.Add(new(fact, observation.DispatchId, observation.Harness, observation.Sequence,
                    observation.CheckId, observation.WakeObligationId, observation.WakeAtUnixMilliseconds, review));
            }

        var page = rows.Take(FactPageSize).ToArray();
        var dispatches = new List<BotDispatchEvidence>();
        foreach (var group in page.Where(row => row.DispatchId.HasValue).GroupBy(row => row.DispatchId!.Value))
        {
            var evidence = await ReadDispatchEvidence(db, tx, input.Room, input.Owner, target, group.Key,
                group.Select(row => row.Fact.EventId).ToHashSet());
            dispatches.AddRange(evidence.Dispatches);
        }
        return new(query, target.Goal.Version)
        {
            Events = page,
            NextBeforeEventId = rows.Count > page.Length ? page[^1].Fact.EventId : null,
            QueriedEvidence = new(target.Id, target.Goal.Version, target.Goal.Status == "completed", dispatches.ToArray(), true, 0)
        };
    }

    private static async Task<BotGoalEvidence> ReadCitedEvidence(DbConnection db, DbTransaction tx, string room,
        string owner, BotGoalSnapshot goal, BotDecision decision, BotGoalEvidence current)
    {
        (Guid? DispatchId, long? EventId)[] dispatchOnly = [(decision.VerifyDispatchId, null), (decision.ReviewDispatchId, null)];
        var citations = (decision.ObligationAssessments ?? []).OfType<GoalObligationAssessment>()
            .Where(assessment => assessment.DispatchId.HasValue)
            .Select(assessment => (assessment.DispatchId, assessment.EventId))
            .Concat(dispatchOnly)
            .Where(citation => citation.DispatchId.HasValue).GroupBy(citation => citation.DispatchId!.Value);
        foreach (var group in citations)
        {
            var events = group.Select(citation => citation.EventId).OfType<long>().ToHashSet();
            var existing = current.Dispatches.FirstOrDefault(dispatch => dispatch.DispatchId == group.Key);
            if (existing is not null && events.All(id => existing.Receipt?.EventId == id ||
                existing.ToolEvidence.Any(fact => fact.EventId == id))) continue;
            var extra = await ReadDispatchEvidence(db, tx, room, owner, goal, group.Key, events);
            current = BotFactContext.MergeEvidence(current, extra);
        }
        return current;
    }

    public async Task<DecisionSnapshot> WithDecisionEvidence(DecisionSnapshot snapshot, BotDecision decision)
    {
        if (decision.GoalId is not Guid goalId || decision.Check(snapshot.Context) is null ||
            snapshot.Context.Goals.FirstOrDefault(goal => goal.Id == goalId) is not { } target) return snapshot;
        var input = snapshot.Input;
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var current = snapshot.Context.DispatchEvidence.FirstOrDefault(group => group.GoalId == target.Id)
            ?? new BotGoalEvidence(target.Id, target.Goal.Version, target.Goal.Status == "completed", [], true, 0);
        var evidence = await ReadCitedEvidence(db, tx, input.Room, input.Owner, target, decision, current);
        await tx.CommitAsync();
        return snapshot with { Context = snapshot.Context with
        {
            DispatchEvidence = [.. snapshot.Context.DispatchEvidence.Where(group => group.GoalId != goalId), evidence]
        } };
    }
}
