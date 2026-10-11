namespace MCAROC_Analysis.Data.Entities;

public sealed class PreLoginLitigationReconciliationEvent
{
    public long PreLoginLitigationReconciliationEventId { get; set; }
    public long PreLoginLitigationTargetId { get; set; }
    public PreLoginLitigationTarget? Target { get; set; }

    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }

    public string PreviousStatus { get; set; } = string.Empty;
    public DateTime? PreviousRegistrationAttemptedUtc { get; set; }
    public string? PreviousVendorJobId { get; set; }
    public DateTime? PreviousPollingDeadlineUtc { get; set; }
    public string? SuppliedVendorJobId { get; set; }
    public string? EvidenceNote { get; set; }
    public int AttemptVersion { get; set; }
}

