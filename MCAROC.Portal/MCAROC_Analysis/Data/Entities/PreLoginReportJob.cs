namespace MCAROC_Analysis.Data.Entities;

public enum PreLoginReportJobStatus { Queued, Fetching, AwaitingReview, Generating, Completed, Failed }

/// <summary>Durable, auditable report request. API payload and final DOCX stay on the server.</summary>
public sealed class PreLoginReportJob
{
    public long PreLoginReportJobId { get; set; }
    public Guid BatchId { get; set; }
    public string Cin { get; set; } = string.Empty;
    public string? SubmittedCompanyName { get; set; }
    public string Format { get; set; } = "Sbi";
    public PreLoginReportJobStatus Status { get; set; } = PreLoginReportJobStatus.Queued;
    public int AttemptCount { get; set; }
    public int ProgressPercent { get; set; }
    public string? FailureReason { get; set; }
    public string? DataJson { get; set; }
    public string? ReportStoragePath { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
}
