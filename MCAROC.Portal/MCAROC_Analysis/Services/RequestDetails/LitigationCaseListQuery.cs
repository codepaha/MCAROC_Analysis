using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.RequestDetails;

/// <summary>A bounded case register. No orders, document bodies, PDF URLs or extracted text are materialized.
/// The standalone case page owns that evidence. All filtering happens before SQL pagination.</summary>
public sealed class LitigationCaseListQuery(AppDbContext db, LitigationOrderOutcomeQuery? outcomes = null)
{
    public static int NormalizePageSize(int size) => size is 25 or 50 or 100 ? size : 25;

    public async Task<IQueryable<LitigationCase>> FilterAsync(long snapshotId, string? court, string? status,
        string? search, string? outcome, long requestId, CancellationToken ct)
    {
        var ids = db.LitigationCaseSourceReports.Where(s => s.LitigationReportSnapshotId == snapshotId)
            .Select(s => s.LitigationCaseId);
        var query = db.LitigationCases.AsNoTracking().Where(c => ids.Contains(c.LitigationCaseId));
        if (!string.IsNullOrWhiteSpace(court))
        {
            var name = court.Trim();
            query = name.Equals("Unspecified Court", StringComparison.OrdinalIgnoreCase)
                ? query.Where(c => c.Court == null || c.Court.Trim() == "")
                : query.Where(c => c.Court != null && c.Court.Trim() == name);
        }
        if (Enum.TryParse<LitigationCaseStatusBucket>(status, true, out var bucket))
            query = bucket switch
            {
                LitigationCaseStatusBucket.Pending => query.Where(LitigationCaseStatusClassifier.IsPendingExpr),
                LitigationCaseStatusBucket.Disposed => query.Where(LitigationCaseStatusClassifier.IsDisposedExpr),
                _ => query.Where(LitigationCaseStatusClassifier.IsUnknownExpr)
            };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(c => (c.CaseNumber != null && c.CaseNumber.ToLower().Contains(term))
                || (c.Cnr != null && c.Cnr.ToLower().Contains(term))
                || (c.Court != null && c.Court.ToLower().Contains(term))
                || (c.PetitionersJson != null && c.PetitionersJson.ToLower().Contains(term))
                || (c.RespondentsJson != null && c.RespondentsJson.ToLower().Contains(term)));
        }
        // Retain evidence-hash validation for an explicitly selected outcome; never show stale classifications.
        if (Enum.TryParse<LitigationOrderOutcome>(outcome, true, out var selected))
        {
            var lookup = await (outcomes ?? new LitigationOrderOutcomeQuery(db)).FindAsync(requestId, [selected], ct);
            var matches = lookup.Matches.Where(m => m.Outcomes.Contains(selected)).Select(m => m.LitigationCaseId).Distinct().ToArray();
            query = query.Where(c => matches.Contains(c.LitigationCaseId));
        }
        return query;
    }

    public async Task<LitigationCaseListViewModel?> BuildAsync(long requestId, string? court, string? status,
        int page, int pageSize, string? search, string? outcome, long? focusCase, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => new { r.CompanyName, r.LatestCompletedIngestionRunId }).FirstOrDefaultAsync(ct);
        if (request is null) return null;
        var snapshotId = await LitigationSnapshotSelection.ForRequest(db, requestId)
            .Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
        var model = new LitigationCaseListViewModel { RequestId = requestId, SnapshotId = snapshotId,
            Court = court?.Trim(), Status = status, Search = search?.Trim(), Outcome = outcome, PageSize = NormalizePageSize(pageSize) };
        if (Enum.TryParse<LitigationCaseStatusBucket>(status, true, out var normalizedStatus) && Enum.IsDefined(normalizedStatus))
            model.Status = normalizedStatus.ToString();
        if (Enum.TryParse<LitigationOrderOutcome>(outcome, true, out var normalizedOutcome) && Enum.IsDefined(normalizedOutcome))
            model.Outcome = normalizedOutcome.ToString();
        if (snapshotId is not { } sid) return model;
        var query = await FilterAsync(sid, court, status, search, outcome, requestId, ct);
        model.Total = await query.CountAsync(ct);
        model.Page = Math.Clamp(page, 1, model.TotalPages);
        if (focusCase is { } focused)
        {
            // A rank count avoids materializing every case id for a deep link.
            var target = await query.Where(c => c.LitigationCaseId == focused)
                .Select(c => new { Court = c.Court == null || c.Court.Trim() == "" ? "Unspecified Court" : c.Court.Trim(), Hearing = c.LastHearingDate ?? "" })
                .FirstOrDefaultAsync(ct);
            if (target is not null)
            {
                var before = await query.CountAsync(c =>
                    string.Compare(c.Court == null || c.Court.Trim() == "" ? "Unspecified Court" : c.Court.Trim(), target.Court) < 0
                    || ((c.Court == null || c.Court.Trim() == "" ? "Unspecified Court" : c.Court.Trim()) == target.Court
                        && (string.Compare(c.LastHearingDate ?? "", target.Hearing) > 0
                            || ((c.LastHearingDate ?? "") == target.Hearing && c.LitigationCaseId < focused))), ct);
                model.Page = before / model.PageSize + 1;
            }
        }
        var rows = await query.OrderBy(c => c.Court == null || c.Court.Trim() == "" ? "Unspecified Court" : c.Court.Trim())
            .ThenByDescending(c => c.LastHearingDate ?? "").ThenBy(c => c.LitigationCaseId)
            .Skip((model.Page - 1) * model.PageSize).Take(model.PageSize)
            .Select(c => new
            {
                c.LitigationCaseId, c.CaseNumber, c.Cnr, c.Court, c.CourtCategory, c.CaseType, c.Type, c.Act,
                c.CaseStage, c.CaseClassification, c.ProceedingType, c.CaseStatus, c.Direction,
                c.PetitionersJson, c.RespondentsJson, c.NextHearingDate, c.LastSeenUtc
            }).ToListAsync(ct);
        var pageIds = rows.Select(c => c.LitigationCaseId).ToArray();
        // Page first, then aggregate only those cases. Avoid correlated document-count subqueries in the paging plan.
        var orderCounts = await (from o in db.LitigationCaseOrders
            where pageIds.Contains(o.LitigationCaseId)
            join d in db.LitigationOrderDocuments on o.LitigationCaseOrderId equals d.LitigationCaseOrderId into documents
            from d in documents.DefaultIfEmpty()
            select new { o.LitigationCaseId, Status = d == null ? (LitigationOrderDocumentStatus?)null : d.Status })
            .GroupBy(x => x.LitigationCaseId)
            .Select(g => new { CaseId = g.Key, Total = g.Count(), Downloaded = g.Count(x => x.Status == LitigationOrderDocumentStatus.Downloaded) })
            .ToDictionaryAsync(x => x.CaseId, ct);
        var run = await db.LitigationAiAnalysisRuns.AsNoTracking().Where(r => r.RequestId == requestId)
            .OrderByDescending(r => r.RunNumber).Select(r => new { r.LitigationAiAnalysisRunId, r.RunNumber }).FirstOrDefaultAsync(ct);
        // Only scalar warning flags cross the wire; even a large stored order analysis stays on the case page.
        var analyses = run is null ? [] : await db.Database.SqlQuery<LitigationListAnalysis>($$"""
            SELECT [LitigationCaseId], [Status], [CompletedUtc],
                JSON_VALUE(CASE WHEN ISJSON([AnalysisJson]) = 1 THEN [AnalysisJson] ELSE N'{}' END, '$.active_restraint') AS [ActiveRestraint],
                JSON_VALUE(CASE WHEN ISJSON([AnalysisJson]) = 1 THEN [AnalysisJson] ELSE N'{}' END, '$.lis_pendens') AS [LisPendens]
            FROM [LitigationCaseAiAnalyses] WHERE [LitigationAiAnalysisRunId] = {{run.LitigationAiAnalysisRunId}}
            """).Where(a => pageIds.Contains(a.LitigationCaseId)).ToListAsync(ct);
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.CompanyName)) names.Add(request.CompanyName);
        if (request.LatestCompletedIngestionRunId is { } rid)
            names.AddRange(await db.CompanyNameHistories.AsNoTracking().Where(h => h.RequestId == requestId && h.IngestionRunId == rid)
                .Select(h => h.PreviousName).ToListAsync(ct));
        foreach (var row in rows)
        {
            var petitioners = LitigationCaseCardMapper.ParseNames(row.PetitionersJson);
            var respondents = LitigationCaseCardMapper.ParseNames(row.RespondentsJson);
            var side = LitigationCompanySides.Determine(petitioners, respondents, names, row.Direction);
            var statusBucket = LitigationCaseStatusClassifier.Classify(row.CaseStatus, row.CaseStage);
            var analysis = analyses.FirstOrDefault(a => a.LitigationCaseId == row.LitigationCaseId);
            model.Cases.Add(new LitigationCaseCardViewModel
            {
                LitigationCaseId = row.LitigationCaseId, CaseNumber = row.CaseNumber, Cnr = row.Cnr, Court = row.Court,
                CaseType = row.CaseType, Petitioners = petitioners, Respondents = respondents,
                CompanySide = side, StatusBucket = statusBucket, NextHearingDate = row.NextHearingDate,
                OrderCount = orderCounts.GetValueOrDefault(row.LitigationCaseId)?.Total ?? 0,
                DownloadedOrderCount = orderCounts.GetValueOrDefault(row.LitigationCaseId)?.Downloaded ?? 0,
                Risk = LitigationBaselineRisk.Assess(new LitigationRiskInput(row.Type, row.Court, row.CourtCategory, row.CaseType,
                    row.Act, row.CaseStage, row.CaseClassification, row.ProceedingType, statusBucket, side, petitioners)),
                Analysis = analysis is null ? null : new LitigationCaseAiAnalysisViewModel
                {
                    Status = Enum.Parse<LitigationAiAnalysisItemStatus>(analysis.Status), CompletedUtc = analysis.CompletedUtc,
                    RunNumber = run!.RunNumber, ActiveRestraint = analysis.ActiveRestraint == "true", LisPendens = analysis.LisPendens == "true"
                },
                IsAnalysisStaleComparedToCase = analysis?.CompletedUtc is { } completed && row.LastSeenUtc > completed
            });
        }
        return model;
    }
}

public sealed class LitigationListAnalysis
{
    public long LitigationCaseId { get; set; }
    public string Status { get; set; } = "";
    public DateTime? CompletedUtc { get; set; }
    public string? ActiveRestraint { get; set; }
    public string? LisPendens { get; set; }
}
