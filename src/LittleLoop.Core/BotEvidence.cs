using System.Text.Json.Serialization;

namespace LittleLoop.Core;

public sealed record BotHarnessOption(string Id, string Description, string[] Capabilities, string WorkspaceScope,
    string Permission, string Availability, string? AvailabilityReason = null)
{
    [JsonIgnore]
    public bool CanQueue => Availability == "available-to-queue";
}

public sealed record BotEvidenceFact(long EventId, BotEventKind Source, int? GoalVersion, string? Status, string Text,
    string? ToolId = null, string? ToolKind = null);

/// <summary>A Bot's assessment of evidence at a particular goal version. It is separate from execution and goal completion.</summary>
public sealed record BotEvidenceVerification(long DecisionEventId, int? GoalVersion);

public sealed record BotDispatchEvidence(Guid DispatchId, int? GoalVersion, string? Harness, string Execution,
    bool? SubmissionStarted, string EvidenceState, BotEvidenceFact? Request, BotEvidenceFact? Receipt, BotEvidenceFact[] ToolEvidence,
    BotEvidenceVerification? Verification, Guid? ReviewDispatchId = null, string? ReviewReason = null, int? RecordedToolCount = null,
    bool? ToolCoverageIncomplete = null)
{
    [JsonIgnore]
    public string? OriginalInstruction { get; init; }
}

public sealed record BotGoalEvidence(Guid GoalId, int GoalVersion, bool GoalCompleted,
    BotDispatchEvidence[] Dispatches, bool CoverageIncomplete, int EarlierDispatchesOmitted);

public sealed partial record BotDecision
{
    internal BotDecisionError? CheckEvidence(BotDecisionContext context)
    {
        var dispatches = context.DispatchEvidence.FirstOrDefault(goal => goal.GoalId == GoalId)?.Dispatches ?? [];
        if (VerifyDispatchId is Guid verified)
        {
            if (Action is not (BotAction.Delegate or BotAction.Wait or BotAction.Complete))
                return EvidenceConflict("verification-action", "Only delegate, wait or complete may carry an evidence assessment.", "verifyDispatchId");
            if (!dispatches.Any(dispatch => dispatch.DispatchId == verified && dispatch.EvidenceState == "observed"))
                return EvidenceConflict("verification-evidence-required",
                    "Use a dispatch ID shown in this goal's evidence with an observed result.", "verifyDispatchId");
        }
        if (ReviewDispatchId.HasValue || ReviewReason is not null)
        {
            if (Action != BotAction.Delegate || ReviewDispatchId is not Guid review ||
                !dispatches.Any(dispatch => dispatch.DispatchId == review))
                return EvidenceConflict("review-dispatch-required",
                    "A review must be a delegation referencing an existing dispatch of the same goal.", "reviewDispatchId");
            if (!HasText(ReviewReason, 500))
                return EvidenceConflict("review-reason-required",
                    "Explain what new requirement, conflict or check justifies the review (maximum 500 characters).", "reviewReason");
        }

        // Exact equality is checkable. The host does not infer that differently worded instructions mean the same thing.
        if (Action == BotAction.Delegate && Instruction is not null)
        {
            string instruction = Instruction.Trim();
            var repeats = dispatches.Where(dispatch => (dispatch.Verification is not null || dispatch.DispatchId == VerifyDispatchId) &&
                dispatch.Harness == Harness &&
                string.Equals(dispatch.OriginalInstruction?.Trim(), instruction, StringComparison.Ordinal)).ToArray();
            if (repeats.Length > 0 && !repeats.Any(dispatch => dispatch.DispatchId == ReviewDispatchId))
                return EvidenceConflict("verified-delegation-repeated",
                    "This instruction duplicates a checked dispatch. Use its evidence or identify the dispatch and give a review reason.", "reviewDispatchId");
        }
        return null;
    }

    private static BotDecisionError EvidenceConflict(string code, string message, string field) =>
        new(code, "evidence-conflict", message, field);
}
