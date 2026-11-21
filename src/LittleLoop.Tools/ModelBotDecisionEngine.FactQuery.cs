using System.Text.Json;
using LittleLoop.Core;

namespace LittleLoop.Tools;

public sealed partial class ModelBotDecisionEngine
{
    private const int MaxFactQueries = 2;

    private static BotFactQuery? ReadFactQuery(string content)
    {
        if (content.Length > MaxDraftCharacters) return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = document.RootElement.EnumerateObject();
            if (!fields.MoveNext() || fields.Current.Name != "factQuery") return null;
            var query = fields.Current.Value.Deserialize<BotFactQuery>(Json);
            return fields.MoveNext() ? null : query;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<BotFactQueryResponse> QueryFacts(BotDecisionContext context, BotFactQuery query,
        CancellationToken token)
    {
        if (!context.Goals.Any(goal => goal.Id == query.GoalId) ||
            (context.Trigger.GoalId ?? context.ReplyGoalId) is Guid addressed && addressed != query.GoalId)
            return new(query)
            {
                Error = new("unknown-fact-query-goal", "fact-query", "Use the visible goal addressed by this decision.", "factQuery.goalId")
            };
        if (query.Kind == BotFactQueryKind.Requirements && (query.DispatchId.HasValue || query.BeforeEventId.HasValue || query.Offset < 0) ||
            query.Kind == BotFactQueryKind.Events && (query.Offset.HasValue || query.BeforeEventId <= 0))
            return new(query)
            {
                Error = new("invalid-fact-query-filter", "fact-query",
                    "Use offset for requirements or dispatchId and beforeEventId for events.", "factQuery")
            };
        return await context.ReadFacts!(query, token);
    }
}
