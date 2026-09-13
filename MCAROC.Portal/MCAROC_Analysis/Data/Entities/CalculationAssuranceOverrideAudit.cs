namespace MCAROC_Analysis.Data.Entities;

/// <summary>The "logged emergency override" requirement — written by the internal
/// POST /internal/calc-audit/override-mode action (built in PR4) so disabling enforcement is an
/// explicit, auditable act rather than an untracked config edit. Never deletes any
/// CalculationDiscrepancy/CalculationArtifactHold/CalculationLedgerEntry history — only the gate's
/// runtime behavior changes when Mode changes.</summary>
public class CalculationAssuranceOverrideAudit
{
    public long CalculationAssuranceOverrideAuditId { get; set; }

    public string PreviousMode { get; set; } = string.Empty;
    public string NewMode { get; set; } = string.Empty;
    public string ChangedByReviewerName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime ChangedUtc { get; set; }
}
