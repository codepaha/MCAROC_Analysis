namespace MCAROC_Analysis.Data.Entities;

public enum PreLoginAttributionAction
{
    Suggested = 1,
    Accepted = 2,
    Rejected = 3,
    AutomaticallyApplied = 4,
    Undone = 5,
    AnalystMiss = 6
}

/// <summary>
/// Audit trail for pre-login case attribution suggestions, automatic exclusions, and analyst decisions (#446).
/// Backs the attribution trust report across assignments and reason kinds.
/// </summary>
public class PreLoginAttributionDecision
{
    public long PreLoginAttributionDecisionId { get; set; }
    public long ScopeCaseId { get; set; }
    public long PreLoginReportJobId { get; set; }
    public string CaseRowId { get; set; } = string.Empty;
    public long TargetId { get; set; }
    public PreLoginTargetKind TargetKind { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public string ReasonKind { get; set; } = string.Empty;
    public PreLoginAttributionAction Action { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime DecidedUtc { get; set; }
}
