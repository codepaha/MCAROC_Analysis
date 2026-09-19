using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CompanyMaster;

public record ValidationResult(bool IsValid, int TotalRows, int DuplicateCINs, string? ErrorMessage, bool IsCollision = false);

public record PromotionMetricsResult(int TotalUpdated, int TotalInserted, int TotalUnchanged, Dictionary<CompanyMasterRecordType, (int Added, int Updated, int Unchanged)> MetricsByRecordType);

public interface ICompanyMasterDeltaService
{
    /// <param name="publishedDate">Portal snapshot date probed before job creation; persisted for idempotent skip-if-same-date checks.</param>
    /// <param name="publishedDateRaw">Raw portal date text for auditability.</param>
    Task<CompanyMasterSyncJob> CreateJobAsync(
        CompanyMasterSyncTriggerType triggerType,
        string? proxyAlias,
        DateOnly? publishedDate = null,
        string? publishedDateRaw = null,
        CancellationToken cancellationToken = default);
    Task UpdateJobStatusAsync(long jobId, CompanyMasterSyncJobStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task<bool> RenewLeaseHeartbeatAsync(long jobId, long fencingToken, CancellationToken cancellationToken = default);
    /// <returns>Total staging rows loaded, and the hex-encoded SHA256 aggregate checksum of all CSV content.</returns>
    Task<(long TotalRows, string AggregateChecksum)> IngestCsvFilesAsync(long syncRunId, long fencingToken, IReadOnlyList<string> csvFiles, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateStagingAsync(long syncRunId, long fencingToken, CancellationToken cancellationToken = default);
    Task<PromotionMetricsResult> PromoteStagedDeltaAsync(long syncRunId, long fencingToken, int batchSize = 4000, CancellationToken cancellationToken = default);
    Task CleanStagingAsync(long syncRunId, CancellationToken cancellationToken = default);
    Task<DateOnly?> ProbePortalSnapshotDateAsync(CancellationToken cancellationToken = default);
    /// <param name="publishedDate">Portal snapshot date already probed by the caller; persisted to the job record.</param>
    /// <param name="publishedDateRaw">Raw portal date text for auditability.</param>
    Task<PromotionMetricsResult?> ExecuteAutomatedSyncAsync(
        long jobId,
        long fencingToken,
        DateOnly? publishedDate = null,
        string? publishedDateRaw = null,
        System.IO.Stream? archiveStream = null,
        CancellationToken cancellationToken = default);
}
