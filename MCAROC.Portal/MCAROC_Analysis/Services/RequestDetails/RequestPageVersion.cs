using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.RequestDetails;

/// <summary>Optimistic publication fence for services that independently select current data.
/// No long-lived transaction, locks, or per-user cache. A concurrent publication asks the user to refresh.</summary>
public sealed record RequestPageVersion(long? Ingestion, long? Analysis, AnalysisRunStatus? AnalysisStatus,
    long? FilingBatch, long? LitigationSnapshot, string? SearchHash, long? LitigationAnalysis,
    LitigationAiAnalysisRunStatus? LitigationAnalysisStatus, long LiveData = 0)
{
    public string Key => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        { Ingestion, Analysis, AnalysisStatus, FilingBatch, LitigationSnapshot, SearchHash, LitigationAnalysis, LitigationAnalysisStatus, LiveData }))));

    public RequestPageVersion WithoutLiveData() => this with { LiveData = 0 };

    public static Task<RequestPageVersion?> ReadAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var snapshots = LitigationSnapshotSelection.ForRequest(db, id);
        return db.Requests.AsNoTracking().TagWith("Request page publication version").Where(r => r.RequestId == id).Select(r => new RequestPageVersion(
            r.LatestCompletedIngestionRunId,
            db.AnalysisRuns.Where(a => a.RequestId == id && a.IngestionRunId == r.LatestCompletedIngestionRunId)
                .OrderByDescending(a => a.RunNumber).Select(a => (long?)a.AnalysisRunId).FirstOrDefault(),
            db.AnalysisRuns.Where(a => a.RequestId == id && a.IngestionRunId == r.LatestCompletedIngestionRunId)
                .OrderByDescending(a => a.RunNumber).Select(a => (AnalysisRunStatus?)a.Status).FirstOrDefault(),
            db.McaFilingBatches.Where(b => b.RequestId == id)
                .OrderByDescending(b => b.Status == FilingBatchStatus.Completed || b.Status == FilingBatchStatus.CompletedWithErrors)
                .ThenByDescending(b => b.Status == FilingBatchStatus.Completed || b.Status == FilingBatchStatus.CompletedWithErrors ? b.CompletedDate ?? b.StartedDate : b.StartedDate)
                .Select(b => (long?)b.BatchId).FirstOrDefault(),
            snapshots.Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefault(),
            db.LitigationSearchJobs.Where(j => j.RequestId == id).Select(j => j.RawResponseHash).FirstOrDefault(),
            db.LitigationAiAnalysisRuns.Where(a => a.RequestId == id).OrderByDescending(a => a.RunNumber)
                .Select(a => (long?)a.LitigationAiAnalysisRunId).FirstOrDefault(),
            db.LitigationAiAnalysisRuns.Where(a => a.RequestId == id).OrderByDescending(a => a.RunNumber)
                .Select(a => (LitigationAiAnalysisRunStatus?)a.Status).FirstOrDefault(),
            r.LiveDataVersion
        )).FirstOrDefaultAsync(ct);
    }
}

public sealed class RequestPageChangedException : Exception
{
    /// <summary>True when only the live-data counter moved while the page loaded (an import still running): the page may be read again ignoring that
    /// counter. Any other change (ingestion, analysis, filings, snapshot) must send the user to a refresh.</summary>
    public bool LiveDataOnly { get; init; }
}
