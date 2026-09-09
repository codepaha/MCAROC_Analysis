using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>Builds the full DashboardViewModel for one filter set. The filtered request set is materialized
/// once (a single round trip) and every subsequent count/group/rank is plain LINQ-to-Objects — deliberate,
/// not an oversight: RequestStatus/ReviewPriority/FindingSeverity/FindingSection/TemporalStatus are all
/// HasConversion&lt;string&gt; columns, so any ranking by them would sort alphabetically if pushed to SQL
/// (RequestsController.Details hit this same trap first). In-memory processing sidesteps that entirely and
/// is well within budget at today's data volumes (largest table ~800 rows).
/// TODO: if a production dataset ever makes "materialize the filtered window, then aggregate in memory"
/// a real cost, revisit with SQL-safe numeric severity/priority projection columns — not needed now.</summary>
public class DashboardQueryService(AppDbContext db)
{
    public async Task<DashboardViewModel> BuildAsync(DashboardFilterCriteria filters, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (from, to) = filters.ResolveWindow(today);
        var (priorFrom, priorTo) = DashboardFilterCriteria.PriorPeriod(from, to);

        var clients = await db.Clients.OrderBy(c => c.ClientName).ToListAsync(ct);

        var currentRows = await RequestRiskDefinitions.ApplyFilters(RequestRiskDefinitions.Query(db), filters, from, to).ToListAsync(ct);
        var priorTotal = await RequestRiskDefinitions.ApplyFilters(RequestRiskDefinitions.Query(db), filters, priorFrom, priorTo).CountAsync(ct);

        var isProcessing = RequestRiskDefinitions.IsProcessing.Compile();
        var isAttentionRequired = RequestRiskDefinitions.IsAttentionRequired.Compile();

        var vm = new DashboardViewModel { Filters = filters, Clients = clients, Window = (from, to) };

        vm.TotalRequests = currentRows.Count;
        vm.TotalRequestsTrendPercent = priorTotal == 0 ? null : Math.Round((vm.TotalRequests - priorTotal) * 100.0 / priorTotal, 1);

        vm.ProcessingCount = currentRows.Count(isProcessing);
        vm.ProcessingByRequestStatus = currentRows
            .Where(r => RequestRiskDefinitions.ProcessingStatuses.Contains(r.Request.RequestStatus))
            .GroupBy(r => r.Request.RequestStatus)
            .ToDictionary(g => g.Key, g => g.Count());
        vm.ProcessingByFilingBatchStatus = currentRows
            .Where(r => r.LatestFilingBatchStatus is { } s && RequestRiskDefinitions.FilingBatchActiveStatuses.Contains(s))
            .GroupBy(r => r.LatestFilingBatchStatus!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        vm.AnalysisCompletedCount = currentRows.Count(r => r.Request.RequestStatus == RequestStatus.AnalysisCompleted);
        vm.AnalysisCompletionRatePercent = vm.TotalRequests == 0 ? 0 : vm.AnalysisCompletedCount * 100.0 / vm.TotalRequests;

        vm.AttentionRequiredCount = currentRows.Count(isAttentionRequired);
        vm.HighPriorityRequestCount = currentRows.Count(r => r.LatestReviewPriority == ReviewPriority.High);

        vm.PriorityDistribution = currentRows
            .Where(r => r.LatestReviewPriority is not null)
            .GroupBy(r => r.LatestReviewPriority!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        vm.RequestTrend = TrendBucketBuilder.Build(
            currentRows.Select(r => DateOnly.FromDateTime(r.Request.CreatedDate)).ToList(), from, to);
        vm.TrendGranularity = TrendBucketBuilder.GranularityFor(from, to);

        // Findings: joined through each request's LATEST AnalysisRun only (never raw RequestId), and
        // TemporalStatus != Historical everywhere below — a resolved/past issue must never inflate current
        // portfolio risk. "Companies affected" dedups by CIN/LLPIN (RequestSummaryRow.EntityKey), not
        // RequestId, so the same company re-submitted under multiple requests counts once.
        var entityKeyByRequestId = currentRows.ToDictionary(r => r.Request.RequestId, r => r.EntityKey);
        var latestRunIds = currentRows.Where(r => r.LatestAnalysisRunId is not null).Select(r => r.LatestAnalysisRunId!.Value).ToList();
        var currentFindings = await db.AnalysisFindings
            .Where(f => latestRunIds.Contains(f.AnalysisRunId) && f.TemporalStatus != TemporalStatus.Historical)
            .Select(f => new DashboardFindingRow(f.RequestId, "", f.Code, f.Title, f.Section, f.Severity, f.TemporalStatus, f.DisplayPriority, f.ObservationDate))
            .ToListAsync(ct);
        currentFindings = currentFindings
            .Select(f => f with { EntityKey = entityKeyByRequestId.GetValueOrDefault(f.RequestId, $"REQ-{f.RequestId}") })
            .ToList();

        var criticalFindings = currentFindings.Where(f => f.Severity == FindingSeverity.Critical).ToList();
        vm.CriticalFindingsCount = criticalFindings.Count;
        vm.CriticalFindingsCompanyCount = criticalFindings.Select(f => f.EntityKey).Distinct().Count();

        var reviewFindings = currentFindings.Where(f => f.Severity == FindingSeverity.Review).ToList();
        vm.ReviewFindingsCount = reviewFindings.Count;
        vm.ReviewFindingsCompanyCount = reviewFindings.Select(f => f.EntityKey).Distinct().Count();

        vm.PositiveFindingsCount = currentFindings.Count(f => f.Severity == FindingSeverity.Positive);

        var nonPositiveFindings = currentFindings.Where(f => f.Severity != FindingSeverity.Positive).ToList();
        vm.FindingsBySection = nonPositiveFindings
            .GroupBy(f => (f.Section, f.Severity))
            .Select(g => new SectionSeverityCount(g.Key.Section, g.Key.Severity, g.Count()))
            .OrderByDescending(s => nonPositiveFindings.Count(f => f.Section == s.Section))
            .ToList();

        vm.TopRiskIndicators = TopRiskIndicatorBuilder.Build(nonPositiveFindings);

        // Priority requests: High/Medium priority OR attention-required (excludes Low-priority noise from
        // a "top 10 to look at" queue while still surfacing pure-technical attention cases).
        var findingsByRequestId = currentFindings.GroupBy(f => f.RequestId).ToDictionary(g => g.Key, g => (IReadOnlyList<DashboardFindingRow>)g.ToList());
        var candidates = currentRows
            .Where(r => r.LatestReviewPriority is ReviewPriority.High or ReviewPriority.Medium || isAttentionRequired(r))
            .Select(r => new PriorityRequestRow(
                r.Request, r.LatestReviewPriority, r.LatestCriticalFindingsCount, r.LatestRunStatus,
                AttentionReasons: BuildAttentionReasons(r),
                KeyFindingTitle: KeyFindingSelector.Select(findingsByRequestId.GetValueOrDefault(r.Request.RequestId, []))?.Title ?? "—"))
            .ToList();
        vm.PriorityRequests = PriorityRequestRanker.Rank(candidates, take: 10);

        return vm;
    }

    /// <summary>Mirrors RequestRiskDefinitions.IsAttentionRequired's exact conditions, but as a list of
    /// human-readable labels rather than a single boolean — shown as badges on drill-down rows so a user
    /// landing on "Attention Required = 36" doesn't have to open every row to see why it's there.</summary>
    internal static List<string> BuildAttentionReasons(RequestSummaryRow row)
    {
        var reasons = new List<string>();
        if (row.LatestReviewPriority == ReviewPriority.High) reasons.Add("High Review Priority");
        if (row.Request.IsManualReviewRequired) reasons.Add("Manual Review Required");
        if (row.Request.RequestStatus == RequestStatus.ExtractionFailed) reasons.Add("Extraction Failed");
        if (row.Request.RequestStatus == RequestStatus.ValidationFailed) reasons.Add("Validation Failed");
        if (row.Request.RequestStatus == RequestStatus.AiAnalysisFailed) reasons.Add("AI Analysis Failed");
        if (row.LatestFilingBatchStatus == FilingBatchStatus.Failed) reasons.Add("MCA Filing Batch Failed");
        return reasons;
    }
}
