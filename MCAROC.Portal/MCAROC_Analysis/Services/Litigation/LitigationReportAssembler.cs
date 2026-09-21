using System.Globalization;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// Assembles the complete data model for standalone litigation PDF and CSV deliverables.
/// Adheres to:
/// - Server-side subquery discipline to avoid SQL Server parameter limit overflow on large snapshots.
/// - Authoritative snapshot selection matching RequestsController.
/// - Canonical order availability and local retention resolution.
/// - Zero vendor URL leakage into DTOs or render models.
/// </summary>
public class LitigationReportAssembler(
    AppDbContext db,
    ILogger<LitigationReportAssembler>? logger = null)
{
    public async Task<StandaloneLitigationReport?> AssembleAsync(long requestId, CancellationToken ct = default)
    {
        var asOfUtc = DateTime.UtcNow;
        var generatedAtUtc = new DateTimeOffset(asOfUtc);

        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return null;

        var job = await db.LitigationSearchJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.RequestId == requestId, ct);
        if (job is null) return null;

        // Authoritative snapshot resolution matching RequestsController
        LitigationReportSnapshot? authoritativeSnapshot = null;
        bool isPriorRun = false;

        if (!string.IsNullOrWhiteSpace(job.RawResponseHash))
        {
            var currentAttemptSnapshot = await db.LitigationReportSnapshots
                .AsNoTracking()
                .Where(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.ReportHash == job.RawResponseHash)
                .OrderByDescending(s => s.RetrievedUtc)
                .FirstOrDefaultAsync(ct);

            if (currentAttemptSnapshot?.Status == LitigationReportSnapshotStatus.Completed)
            {
                authoritativeSnapshot = currentAttemptSnapshot;
                isPriorRun = false;
            }
        }

        if (authoritativeSnapshot is null)
        {
            var latestCompleted = await db.LitigationReportSnapshots
                .AsNoTracking()
                .Where(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.Status == LitigationReportSnapshotStatus.Completed)
                .OrderByDescending(s => s.RetrievedUtc)
                .FirstOrDefaultAsync(ct);

            if (latestCompleted is not null)
            {
                authoritativeSnapshot = latestCompleted;
                isPriorRun = true;
            }
        }

        if (authoritativeSnapshot is null)
        {
            return null; // Signals 404 (no completed snapshot available)
        }

        // Keywords
        List<string> keywords = [];
        if (!string.IsNullOrWhiteSpace(job.KeywordsJson))
        {
            try
            {
                var kwList = JsonSerializer.Deserialize<List<LitigationKeyword>>(job.KeywordsJson);
                if (kwList is not null)
                {
                    keywords = kwList.Select(k => k.Value).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
                }
            }
            catch { }
        }

        // Server-Side Subquery Loading for Case IDs
        var snapshotId = authoritativeSnapshot.LitigationReportSnapshotId;
        var caseIdsQuery = db.LitigationCaseSourceReports
            .Where(sr => sr.LitigationReportSnapshotId == snapshotId)
            .Select(sr => sr.LitigationCaseId)
            .Distinct();

        var cases = await db.LitigationCases
            .AsNoTracking()
            .Where(c => caseIdsQuery.Contains(c.LitigationCaseId))
            .Include(c => c.Orders)
            .ToListAsync(ct);

        // Server-Side Subquery Loading for Order Documents
        var orderIdsQuery = db.LitigationCaseOrders
            .Where(o => caseIdsQuery.Contains(o.LitigationCaseId))
            .Select(o => o.LitigationCaseOrderId);

        var docs = await db.LitigationOrderDocuments
            .AsNoTracking()
            .Where(d => orderIdsQuery.Contains(d.LitigationCaseOrderId))
            .ToListAsync(ct);

        var docByOrderId = docs.ToDictionary(d => d.LitigationCaseOrderId);

        // AI Analysis (stable LitigationCaseId join)
        var latestAiRun = await db.LitigationAiAnalysisRuns
            .AsNoTracking()
            .Where(r => r.RequestId == requestId && (r.Status == LitigationAiAnalysisRunStatus.Completed || r.Status == LitigationAiAnalysisRunStatus.CompletedWithErrors))
            .OrderByDescending(r => r.RunNumber)
            .Include(r => r.PortfolioAnalysis)
            .FirstOrDefaultAsync(ct);

        LitigationPortfolioAnalysis? portfolioAnalysis = null;
        Dictionary<long, LitigationCaseAnalysis> caseAnalysesByCaseId = [];

        if (latestAiRun is not null)
        {
            if (latestAiRun.PortfolioAnalysis is { } pa && !string.IsNullOrWhiteSpace(pa.AnalysisJson))
            {
                try
                {
                    using var pDoc = JsonDocument.Parse(pa.AnalysisJson);
                    var pRoot = pDoc.RootElement;
                    string? summary = pRoot.TryGetProperty("summary", out var sProp) ? sProp.GetString() : null;
                    List<string> findings = [];
                    if (pRoot.TryGetProperty("unknowns", out var uProp) && uProp.ValueKind == JsonValueKind.Array)
                    {
                        findings = uProp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                    }
                    string? riskLevel = pRoot.TryGetProperty("riskLevel", out var rProp) ? rProp.GetString() : null;
                    portfolioAnalysis = new LitigationPortfolioAnalysis(pa.Status.ToString(), riskLevel, summary, findings);
                }
                catch { }
            }

            var aiCaseAnalyses = await db.LitigationCaseAiAnalyses
                .AsNoTracking()
                .Where(ca => ca.LitigationAiAnalysisRunId == latestAiRun.LitigationAiAnalysisRunId)
                .ToListAsync(ct);

            foreach (var ca in aiCaseAnalyses)
            {
                if (string.IsNullOrWhiteSpace(ca.AnalysisJson))
                {
                    caseAnalysesByCaseId[ca.LitigationCaseId] = new LitigationCaseAnalysis(ca.Status.ToString(), null, ca.FailureReason ?? "Analysis pending.", null, null);
                    continue;
                }
                try
                {
                    using var cDoc = JsonDocument.Parse(ca.AnalysisJson);
                    var cRoot = cDoc.RootElement;
                    string? summary = cRoot.TryGetProperty("summary", out var sProp) ? sProp.GetString() : null;
                    string? riskLevel = cRoot.TryGetProperty("riskLevel", out var rProp) ? rProp.GetString() : null;
                    List<string> keyIssues = [];
                    if (cRoot.TryGetProperty("unknowns", out var uProp) && uProp.ValueKind == JsonValueKind.Array)
                    {
                        keyIssues = uProp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                    }
                    string? recommendedAction = cRoot.TryGetProperty("recommendedAction", out var raProp) ? raProp.GetString() : null;
                    caseAnalysesByCaseId[ca.LitigationCaseId] = new LitigationCaseAnalysis(ca.Status.ToString(), riskLevel, summary, keyIssues, recommendedAction);
                }
                catch
                {
                    caseAnalysesByCaseId[ca.LitigationCaseId] = new LitigationCaseAnalysis(ca.Status.ToString(), null, null, null, null);
                }
            }
        }

        // Build DTOs with Chronological Order Sorting
        var reportCases = new List<StandaloneReportCaseDto>(cases.Count);
        foreach (var c in cases
                     .OrderBy(c => string.IsNullOrWhiteSpace(c.Court) ? "Unspecified Court" : c.Court.Trim())
                     .ThenByDescending(c => c.LastHearingDate ?? string.Empty)
                     .ThenBy(c => c.LitigationCaseId))
        {
            var sortedOrders = c.Orders
                .OrderByDescending(o => ParseOrderDate(o.OrderDate) ?? DateTime.MinValue)
                .ThenByDescending(o => o.OrderDate ?? string.Empty)
                .ThenBy(o => o.LitigationCaseOrderId)
                .Select(o =>
                {
                    docByOrderId.TryGetValue(o.LitigationCaseOrderId, out var doc);
                    var (bucket, disclosure, csvStatus, extractionLabel) = LitigationOrderAvailabilityResolver.Resolve(doc, asOfUtc);
                    return new StandaloneReportOrderDto(
                        o.LitigationCaseOrderId,
                        o.OrderDate,
                        o.OrderType,
                        bucket,
                        doc?.Status,
                        doc?.TextExtractionStatus,
                        doc?.RetainedUntilUtc > DateTime.MinValue ? doc.RetainedUntilUtc : null,
                        disclosure,
                        csvStatus,
                        extractionLabel);
                })
                .ToList();

            reportCases.Add(new StandaloneReportCaseDto(
                c.LitigationCaseId,
                c.ProviderCaseId,
                c.CspId,
                c.Cnr,
                c.CourtCategory,
                c.Direction,
                c.CaseClassification,
                c.Type,
                c.Court,
                c.Bench,
                c.CaseNumber,
                c.CaseType,
                c.CaseYear,
                c.CaseStage,
                c.CaseStatus,
                c.Act,
                c.FilingDate,
                c.LastHearingDate,
                c.NextHearingDate,
                c.DecisionDate,
                c.State,
                c.District,
                c.PetitionersJson,
                c.RespondentsJson,
                c.PetitionerAdvocatesJson,
                c.RespondentAdvocatesJson,
                sortedOrders));
        }

        // Build Court Summary Grid
        var courtGrid = new LitigationCourtSummaryGrid();
        var courtGroups = reportCases
            .GroupBy(c => new
            {
                Court = string.IsNullOrWhiteSpace(c.Court) ? "Unspecified Court" : c.Court.Trim(),
                Category = c.CourtCategory
            })
            .OrderBy(g => g.Key.Court);

        foreach (var g in courtGroups)
        {
            int pending = 0;
            int disposed = 0;
            int unknown = 0;
            int ordersCount = 0;

            foreach (var item in g)
            {
                var bucket = LitigationCaseStatusClassifier.Classify(item.CaseStatus, item.CaseStage);
                switch (bucket)
                {
                    case LitigationCaseStatusBucket.Pending: pending++; break;
                    case LitigationCaseStatusBucket.Disposed: disposed++; break;
                    default: unknown++; break;
                }
                ordersCount += item.Orders.Count;
            }

            courtGrid.Rows.Add(new LitigationCourtSummaryRow
            {
                CourtName = g.Key.Court,
                CourtCategory = g.Key.Category,
                TotalCases = g.Count(),
                PendingCases = pending,
                DisposedCases = disposed,
                UnknownCases = unknown,
                TotalOrders = ordersCount
            });
        }

        return new StandaloneLitigationReport(
            request.RequestNumber ?? $"REQ-{request.RequestId}",
            request.CompanyName ?? "Unspecified Company",
            generatedAtUtc,
            authoritativeSnapshot.LitigationReportSnapshotId,
            authoritativeSnapshot.RetrievedUtc,
            isPriorRun,
            keywords,
            courtGrid,
            reportCases,
            portfolioAnalysis,
            caseAnalysesByCaseId);
    }

    internal static DateTime? ParseOrderDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] formats = ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy/MM/dd"];
        if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;
        return null;
    }
}
