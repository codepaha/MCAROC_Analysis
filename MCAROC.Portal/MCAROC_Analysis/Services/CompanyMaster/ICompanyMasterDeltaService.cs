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
    Task<CompanyMasterSyncJob> CreateJobAsync(CompanyMasterSyncTriggerType triggerType, string? proxyAlias, CancellationToken cancellationToken = default);
    Task UpdateJobStatusAsync(long jobId, CompanyMasterSyncJobStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task<long> IngestCsvFilesAsync(long syncRunId, long fencingToken, IReadOnlyList<string> csvFiles, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateStagingAsync(long syncRunId, long fencingToken, CancellationToken cancellationToken = default);
    Task<PromotionMetricsResult> PromoteStagedDeltaAsync(long syncRunId, long fencingToken, int batchSize = 4000, CancellationToken cancellationToken = default);
    Task CleanStagingAsync(long syncRunId, CancellationToken cancellationToken = default);
    Task<DateOnly?> ProbePortalSnapshotDateAsync(CancellationToken cancellationToken = default);
}
