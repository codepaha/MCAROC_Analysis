using System;
using System.Collections.Generic;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// Lifecycle and telemetry record for a scheduled or manual Company Master snapshot sync run.
/// Tracks distributed fencing tokens, lease heartbeats, portal dates, checksums, and execution metrics.
/// </summary>
public sealed class CompanyMasterSyncJob
{
    public long JobId { get; set; }

    /// <summary>
    /// Monotonically increasing fencing token. Every worker write and batch promotion atomically verifies
    /// this token against the active job record to prevent split-brain writes upon lease recovery.
    /// </summary>
    public long FencingToken { get; set; } = 1;

    public CompanyMasterSyncTriggerType TriggerType { get; set; } = CompanyMasterSyncTriggerType.Scheduled;

    public CompanyMasterSyncJobStatus Status { get; set; } = CompanyMasterSyncJobStatus.Pending;

    /// <summary>
    /// Normalized published date parsed from MCA CDM portal ("* Company Master Details As on &lt;Date&gt;").
    /// Stored as DateOnly for indexed date comparisons.
    /// </summary>
    public DateOnly? PublishedDate { get; set; }

    /// <summary>
    /// Raw unparsed portal date text as displayed on mcacdm.nic.in for auditability.
    /// </summary>
    public string? PublishedDateRaw { get; set; }

    /// <summary>
    /// Midnight UTC representation of the published date.
    /// </summary>
    public DateTime? PublishedDateUtc { get; set; }

    /// <summary>
    /// SHA256 aggregate checksum computed over source archive or combined CSV streams.
    /// Used for idempotent skip if data has already been promoted.
    /// </summary>
    public string? AggregateChecksum { get; set; }

    /// <summary>
    /// Machine/process identifier of the worker currently holding the lease.
    /// </summary>
    public string? LeaseOwnerInstanceId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Updated continuously during download, staging, and batched promotion.
    /// Used by replacement workers to detect orphaned/dead jobs.
    /// </summary>
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LeaseExpiresUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Sanitized alias/endpoint of the proxy utilized for portal communication (credentials stripped).
    /// </summary>
    public string? SanitizedProxyAlias { get; set; }

    public int CaptchaAttempts { get; set; }

    public string? ErrorMessage { get; set; }

    public ICollection<CompanyMasterSyncMetric> Metrics { get; set; } = new List<CompanyMasterSyncMetric>();
}
