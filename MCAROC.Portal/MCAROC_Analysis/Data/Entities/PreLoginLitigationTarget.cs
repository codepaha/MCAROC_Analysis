namespace MCAROC_Analysis.Data.Entities;

public sealed class PreLoginLitigationTarget
{
    public long PreLoginLitigationTargetId { get; set; }
    public long PreLoginReportJobId { get; set; }
    public PreLoginReportJob? PreLoginReportJob { get; set; }

    public PreLoginTargetKind TargetKind { get; set; }
    public int TargetIndex { get; set; }
    public string TargetName { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;
    public bool ExactMatch { get; set; }
    public string KeywordsJson { get; set; } = "[]";

    public string? VendorJobId { get; set; }
    public bool IsUserSuppliedVendorJobId { get; set; }
    public DateTime? RegistrationAttemptedUtc { get; set; }
    public DateTime? RegisteredUtc { get; set; }

    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }

    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public int AttemptVersion { get; set; } = 1;
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? PollingDeadlineUtc { get; set; }
    public int ProgressPercent { get; set; }
    public string? StatusMessage { get; set; }
    public string? FailureReason { get; set; }

    public string? ReportFormat { get; set; }
    public byte[]? RawReportBytes { get; set; }
    public string? RawResponseHash { get; set; }
    public long? RawReportByteLength { get; set; }
    public bool? NoLitigationFound { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public ICollection<PreLoginLitigationReconciliationEvent> ReconciliationEvents { get; set; } = [];
}

public enum PreLoginTargetKind { Borrower, Director }

