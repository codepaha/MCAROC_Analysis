using System.Linq.Expressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>One request joined to its latest AnalysisRun and latest McaFilingBatch — the base row every
/// dashboard/list aggregate is built from. Excel/analysis (RequestStatus) and MCA filing PDF processing
/// (McaFilingBatch.Status) are fully decoupled pipelines (confirmed: nothing in FilingBatchProcessor ever
/// touches McaRequest.RequestStatus), so a request can be RequestStatus.AnalysisCompleted while its filing
/// batch is still processing or has failed — this row carries both signals so callers never have to
/// re-derive that coupling themselves.</summary>
public class RequestSummaryRow
{
    public required McaRequest Request { get; set; }
    public long? LatestAnalysisRunId { get; set; }
    public ReviewPriority? LatestReviewPriority { get; set; }
    public int LatestCriticalFindingsCount { get; set; }
    public AnalysisRunStatus? LatestRunStatus { get; set; }
    public FilingBatchStatus? LatestFilingBatchStatus { get; set; }

    /// <summary>Dedup key for "distinct companies/entities affected" counts across every dashboard
    /// aggregate. Two McaRequests for the same company (a re-submission, or a follow-up review) share a
    /// Cin/Llpin and must count as ONE company, never two — counting distinct RequestId here would silently
    /// overcount. Falls back to the request's own id only when neither identifier is present.</summary>
    public string EntityKey => Request.Cin ?? Request.Llpin ?? $"REQ-{Request.RequestId}";
}

/// <summary>Single source of truth for "processing"/"attention required"/"current findings" — both
/// DashboardQueryService and RequestListQueryService must compute these identically, so every definition
/// lives here exactly once.</summary>
public static class RequestRiskDefinitions
{
    /// <summary>Everything between creation and a terminal outcome on the Excel+analysis pipeline.</summary>
    public static readonly RequestStatus[] ProcessingStatuses =
    [
        RequestStatus.Created, RequestStatus.DocumentsUploaded, RequestStatus.Validating,
        RequestStatus.ExtractionInProgress, RequestStatus.DataExtracted, RequestStatus.AiAnalysisInProgress
    ];

    public static readonly RequestStatus[] FailedStatuses =
    [
        RequestStatus.ValidationFailed, RequestStatus.ExtractionFailed, RequestStatus.AiAnalysisFailed
    ];

    /// <summary>MCA filing PDF pipeline states that mean "still working" — mirrors ProcessingStatuses but
    /// for the independent McaFilingBatch.Status dimension.</summary>
    public static readonly FilingBatchStatus[] FilingBatchActiveStatuses =
    [
        FilingBatchStatus.Uploaded, FilingBatchStatus.Unpacking, FilingBatchStatus.Indexing, FilingBatchStatus.Processing
    ];

    /// <summary>A request counts as "still processing" if EITHER pipeline is still running — closes the gap
    /// where RequestStatus alone would miss a request whose Excel/analysis pipeline finished but whose
    /// optional PDF archive is still being unpacked/OCR'd/classified in the background.</summary>
    public static readonly Expression<Func<RequestSummaryRow, bool>> IsProcessing = row =>
        ProcessingStatuses.Contains(row.Request.RequestStatus)
        || (row.LatestFilingBatchStatus != null && FilingBatchActiveStatuses.Contains(row.LatestFilingBatchStatus.Value));

    /// <summary>Union of every reason a request needs a human to look at it: manual review flag, a terminal
    /// failure on either pipeline, or a High review priority. Documented assumption: Cancelled is excluded —
    /// a deliberate cancellation isn't a thing to review, unlike a failure.</summary>
    public static readonly Expression<Func<RequestSummaryRow, bool>> IsAttentionRequired = row =>
        row.Request.IsManualReviewRequired
        || FailedStatuses.Contains(row.Request.RequestStatus)
        || row.LatestReviewPriority == ReviewPriority.High
        || row.LatestFilingBatchStatus == FilingBatchStatus.Failed;

    /// <summary>Base projection: left-join each request to its latest AnalysisRun (by RunNumber desc — a
    /// strictly increasing per-request sequence, already deterministic) and its latest McaFilingBatch (by
    /// StartedDate desc, tie-broken by BatchId desc). Composable: callers add .Where/.OrderBy/.Skip/.Take
    /// before a single ToListAsync/CountAsync.</summary>
    public static IQueryable<RequestSummaryRow> Query(AppDbContext db) =>
        db.Requests.Include(r => r.Client).Select(r => new RequestSummaryRow
        {
            Request = r,
            LatestAnalysisRunId = db.AnalysisRuns.Where(a => a.RequestId == r.RequestId)
                .OrderByDescending(a => a.RunNumber).Select(a => (long?)a.AnalysisRunId).FirstOrDefault(),
            LatestReviewPriority = db.AnalysisRuns.Where(a => a.RequestId == r.RequestId)
                .OrderByDescending(a => a.RunNumber).Select(a => a.OverallReviewPriority).FirstOrDefault(),
            LatestCriticalFindingsCount = db.AnalysisRuns.Where(a => a.RequestId == r.RequestId)
                .OrderByDescending(a => a.RunNumber).Select(a => (int?)a.CriticalFindingsCount).FirstOrDefault() ?? 0,
            LatestRunStatus = db.AnalysisRuns.Where(a => a.RequestId == r.RequestId)
                .OrderByDescending(a => a.RunNumber).Select(a => (AnalysisRunStatus?)a.Status).FirstOrDefault(),
            LatestFilingBatchStatus = db.McaFilingBatches.Where(b => b.RequestId == r.RequestId)
                .OrderByDescending(b => b.StartedDate).ThenByDescending(b => b.BatchId)
                .Select(b => (FilingBatchStatus?)b.Status).FirstOrDefault()
        });

    /// <summary>Date range applies to Request.CreatedDate only — explicit, documented convention. Findings/
    /// charges/filing-batch dates are NOT what this dashboard filters by; an event-date-scoped dashboard is
    /// a future, separate addition. Status/Priority filters are equality-only (safe to push to SQL even
    /// though these columns are string-backed — equality on a converted value still translates correctly;
    /// it's only ORDER BY on them that's unsafe).</summary>
    public static IQueryable<RequestSummaryRow> ApplyFilters(
        IQueryable<RequestSummaryRow> query, DashboardFilterCriteria filters, DateOnly from, DateOnly to)
    {
        var fromUtc = from.ToDateTime(TimeOnly.MinValue);
        var toExclusiveUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
        query = query.Where(row => row.Request.CreatedDate >= fromUtc && row.Request.CreatedDate < toExclusiveUtc);

        if (filters.ClientId is { } clientId) query = query.Where(row => row.Request.ClientId == clientId);
        if (filters.Status is { } status) query = query.Where(row => row.Request.RequestStatus == status);
        if (filters.Priority is { } priority) query = query.Where(row => row.LatestReviewPriority == priority);
        if (filters.EntityType is { } entityType) query = query.Where(row => row.Request.EntityType == entityType);

        return query;
    }
}
