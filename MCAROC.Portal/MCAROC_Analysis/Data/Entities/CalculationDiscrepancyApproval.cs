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

    /// <summary>Reviewer-supplied name (the login gate is deferred — see AGENT_CHANNEL.md — so this is a
    /// plain reviewer-entered field today, not a claim; trivially swappable to a login claim later with no
    /// schema change).</summary>
    public string ReviewerName { get; set; } = string.Empty;

    public string? ReviewerNotes { get; set; }

    public DateTime DecidedUtc { get; set; }

    /// <summary>A fresh, server-computed re-run of the originating check's logic, captured at Confirm
    /// time — the issue's explicit "deterministic reproduction result" requirement, enforced at the
    /// human-decision step and not only at ingestion.</summary>
    public string? DeterministicReproductionResultJson { get; set; }

    public string? ModelIdUsed { get; set; }
    public string? PromptVersionUsed { get; set; }

    /// <summary>Set only for DecisionAction == Confirm — the severity this specific reviewer assigned.
    /// CalculationDiscrepancy.Severity is only the current-state read model; this is what makes a future
    /// dual-approval Confirm's consensus check (do all approving reviewers agree on severity?) verifiable
    /// against the durable audit trail rather than trusting whichever caller happened to complete the
    /// transition last.</summary>
    public CalculationDiscrepancySeverity? ProposedSeverity { get; set; }
}
