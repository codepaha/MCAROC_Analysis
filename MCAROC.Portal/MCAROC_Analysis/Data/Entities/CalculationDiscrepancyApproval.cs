namespace MCAROC_Analysis.Data.Entities;

/// <summary>One reviewer decision on a CalculationDiscrepancy, append-only — this is the full immutable
/// history (evidence, reviewer, timestamps) the issue requires; CalculationDiscrepancy.Status/Severity
/// are just the denormalized "current state" read model. ApprovalSequence is the dual-approval slot:
/// 1 is today's only required reviewer, 2 is a future second approver — turning that on is a pure
/// application-logic change (require a distinct-reviewer sequence-2 row before a transition sticks),
/// with zero schema change needed.</summary>
public class CalculationDiscrepancyApproval
{
    public long CalculationDiscrepancyApprovalId { get; set; }

    public long CalculationDiscrepancyId { get; set; }
    public CalculationDiscrepancy? Discrepancy { get; set; }

    public int ApprovalSequence { get; set; } = 1;

    public CalculationDiscrepancyDecisionAction DecisionAction { get; set; }

    /// <summary>From the internal reviewer login's display-name claim.</summary>
    public string ReviewerName { get; set; } = string.Empty;

    public string? ReviewerNotes { get; set; }

    public DateTime DecidedUtc { get; set; }

    /// <summary>A fresh, server-computed re-run of the originating check's logic, captured at Confirm
    /// time — the issue's explicit "deterministic reproduction result" requirement, enforced at the
    /// human-decision step and not only at ingestion.</summary>
    public string? DeterministicReproductionResultJson { get; set; }

    public string? ModelIdUsed { get; set; }
    public string? PromptVersionUsed { get; set; }
}
