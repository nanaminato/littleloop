using System.Data.Common;
using System.Text.Json;
using LittleLoop.Core;

sealed partial class ChatStore
{
    private const int EvidenceEventLimit = 240;
    internal const int EvidenceDispatchLimit = 20;

    private sealed record EvidenceRow(long Id, BotObservation Observation, string? Instruction = null,
        Guid? Verify = null, Guid? Review = null, string? ReviewReason = null);

    private static async Task<BotGoalEvidence> ReadDispatchEvidence(DbConnection db, DbTransaction tx, string room, string owner,
        BotGoalSnapshot goal)
    {
        // Prioritize workflow facts so a burst of tool events cannot crowd dispatches and their receipts out.
        await using var cmd = Command(db, tx, """
            SELECT e.id,e.observation,CASE WHEN e.kind='BotDecision' THEN e.detail END
            FROM chat_bot_events e JOIN chat_messages m ON m.room_id=e.room_id AND m.seq=e.seq
            WHERE e.room_id=@room AND e.owner_id=@owner AND e.work_id=@goal AND NOT m.deleted AND NOT m.edited
                AND e.kind IN ('BotDecision','HarnessReceipt','ToolResult')
            ORDER BY CASE WHEN e.kind='ToolResult' THEN 1 ELSE 0 END,e.id DESC
            LIMIT @limit
            """, ("room", room), ("owner", owner), ("goal", goal.Id.ToString()), ("limit", EvidenceEventLimit + 1));
        var rows = new List<EvidenceRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var observation = FromJson<BotObservation>(reader.GetString(1));
            if (observation.Kind != BotEventKind.BotDecision || reader.IsDBNull(2))
            {
                rows.Add(new(reader.GetInt64(0), observation));
                continue;
            }
            using var document = JsonDocument.Parse(reader.GetString(2));
            var detail = document.RootElement;
            rows.Add(new(reader.GetInt64(0), observation, ReadText(detail, "instruction"), ReadId(detail, "verifyDispatchId"),
                ReadId(detail, "reviewDispatchId"), ReadText(detail, "reviewReason")));
        }
        bool capped = rows.Count > EvidenceEventLimit;
        if (capped) rows.RemoveAt(EvidenceEventLimit);
        var relevant = rows.Where(row => row.Observation.GoalId == goal.Id &&
            (row.Observation.Kind != BotEventKind.BotDecision || row.Observation.Status == nameof(BotAction.Delegate))).ToArray();
        bool unattributed = relevant.Any(row => row.Observation.DispatchId is null);
        var grouped = relevant.Where(row => row.Observation.DispatchId.HasValue).GroupBy(row => row.Observation.DispatchId!.Value)
            .OrderByDescending(group => group.Max(row => row.Id)).ToArray();
        var dispatches = grouped.Take(EvidenceDispatchLimit).Select(group =>
        {
            var request = Latest(group, BotEventKind.BotDecision);
            var receipt = Latest(group, BotEventKind.HarnessReceipt);
            var identity = request?.Observation ?? receipt?.Observation ?? group.First().Observation;
            // Do not combine facts whose old records conflict about the dispatch's goal version or harness.
            if (receipt is not null && !SameDispatchVersion(receipt.Observation, identity)) receipt = null;
            var tools = group.Where(row => row.Observation.Kind == BotEventKind.ToolResult &&
                SameDispatchVersion(row.Observation, identity) && TerminalTool(row.Observation.Status)).OrderByDescending(row => row.Id).ToArray();
            var shown = tools.Where(row => row.Observation.Status == "completed").Take(1)
                .Concat(tools.Where(row => row.Observation.Status != "completed").Take(1)).OrderBy(row => row.Id).ToArray();
            bool evidence = identity.GoalVersion.HasValue && (tools.Length > 0 ||
                receipt?.Observation is { Status: "returned", DispatchSubmitted: not false } returned &&
                !string.IsNullOrWhiteSpace(returned.Text));
            var verified = evidence ? rows.Where(row => row.Verify == group.Key && row.Observation.GoalId == goal.Id)
                .MaxBy(row => row.Id) : null;
            string execution = receipt?.Observation.Status ?? "unknown";
            bool? submitted = receipt?.Observation.DispatchSubmitted;
            if (submitted == false && execution is not ("uncertain" or "returned")) execution = "not-submitted";
            var summary = receipt?.Observation.ToolEvidence;
            return new BotDispatchEvidence(group.Key, identity.GoalVersion, identity.Harness, execution, submitted,
                evidence ? "observed" : "unknown", Fact(request, 180), Fact(receipt, 240),
                shown.Select(row => Fact(row, 140)!).ToArray(),
                verified is null ? null : new(verified.Id, verified.Observation.GoalVersion),
                request?.Review, request?.ReviewReason, summary?.Recorded,
                summary is null ? null : summary.Truncated == true || summary.Recorded > shown.Length ? true : summary.Truncated)
            {
                OriginalInstruction = request?.Instruction,
                ReceiptToolsTruncated = summary?.Truncated,
                ReportedFailedTools = summary?.ReportedFailed
            };
        }).ToArray();
        int omitted = Math.Max(0, grouped.Length - EvidenceDispatchLimit);
        return new(goal.Id, goal.Goal.Version, goal.Goal.Status == "completed", dispatches, capped || unattributed || omitted > 0, omitted);
    }

    private static bool SameDispatchVersion(BotObservation candidate, BotObservation identity) =>
        candidate.GoalVersion == identity.GoalVersion && string.Equals(candidate.Harness, identity.Harness, StringComparison.Ordinal);

    private static EvidenceRow? Latest(IEnumerable<EvidenceRow> rows, BotEventKind kind) =>
        rows.Where(row => row.Observation.Kind == kind).MaxBy(row => row.Id);

    private static bool TerminalTool(string? status) => status is "completed" or "failed" or "error" or "denied" or "declined";

    private static BotEvidenceFact? Fact(EvidenceRow? row, int limit)
    {
        if (row is null) return null;
        var observation = row.Observation;
        string text = row.Instruction ?? observation.Text;
        if (text.Length > limit)
        {
            int head = limit / 2;
            int tail = text.Length - (limit - head - 1);
            text = Cut(text, head - (char.IsHighSurrogate(text[head - 1]) ? 1 : 0)) + "…" +
                text[(tail + (char.IsLowSurrogate(text[tail]) ? 1 : 0))..];
        }
        return new(row.Id, observation.Kind, observation.GoalVersion, observation.Status, text, observation.ToolId, observation.ToolKind);
    }

    private static string? ReadText(JsonElement detail, string field) =>
        detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty(field, out var part) && part.ValueKind == JsonValueKind.String
            ? part.GetString() : null;

    private static Guid? ReadId(JsonElement detail, string field) => Guid.TryParse(ReadText(detail, field), out var id) ? id : null;
}
