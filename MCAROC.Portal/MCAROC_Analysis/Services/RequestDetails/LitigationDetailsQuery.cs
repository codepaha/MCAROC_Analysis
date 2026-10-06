using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MCAROC_Analysis.Services.RequestDetails;

public sealed class LitigationDetailsQuery(AppDbContext db,
    Microsoft.Extensions.Options.IOptions<BprLitigationOptions>? bprOptions = null,
    LitigationAiAnalysisOrchestrator? litigationAnalysis = null, LitigationOrderOutcomeQuery? orderOutcomes = null)
{
    public async Task LoadAsync(RequestDetailsViewModel vm, string? court, string? status, int page, string? outcome, long? focusCase, CancellationToken ct, string? search = null, int pageSize = 25, bool compact = false)
    {
        var request = vm.Request;
        var id = request.RequestId;
        // Live court records, orders, AI analysis and reports are client-facing: every signed-in portal user
        // sees them. Access to the request itself is enforced upstream (portal login + request boundary).
        {
            var litVm = new LitigationTabViewModel
            {
                Request = request,
                CurrentPage = Math.Max(1, page),
                PageSize = LitigationCaseListQuery.NormalizePageSize(pageSize),
                SelectedOutcome = outcome
            };
            var litCt = ct;
            // The DI-registered query caches the evidence hashes, so this per-page-load lookup doesn't reread every order's text.
            var outcomeLookup = compact && string.IsNullOrWhiteSpace(outcome) ? OrderOutcomeLookup.Empty
                : await (orderOutcomes ?? new LitigationOrderOutcomeQuery(db)).FindAsync(id, Enum.GetValues<LitigationOrderOutcome>(), litCt);
            litVm.TotalClassifiedOrdersCount = outcomeLookup.OrdersClassified;
            litVm.OutcomeCounts = outcomeLookup.Matches
                .SelectMany(m => m.Outcomes)
                .GroupBy(o => o)
                .ToDictionary(g => g.Key, g => g.Count());
            var outcomeByOrderId = outcomeLookup.Matches
                .GroupBy(m => m.LitigationCaseOrderId)
                .ToDictionary(g => g.Key, g => g.First());

            var bprOpts = bprOptions?.Value ?? new BprLitigationOptions();
            litVm.Refresh = await LitigationRefreshPolicy.GetAsync(db, id, bprOpts, DateTime.UtcNow, litCt);
            litVm.HasCompletedAnalysis = await db.LitigationAiAnalysisRuns.AsNoTracking().AnyAsync(r => r.RequestId == id
                && (r.Status == LitigationAiAnalysisRunStatus.Completed || r.Status == LitigationAiAnalysisRunStatus.CompletedWithErrors));
            litVm.AnalysisInProgress = await db.LitigationAiAnalysisRuns.AsNoTracking().AnyAsync(r => r.RequestId == id
                && (r.Status == LitigationAiAnalysisRunStatus.Pending || r.Status == LitigationAiAnalysisRunStatus.InProgress));
            litVm.AnalysisAvailabilityPending = compact && !litVm.AnalysisInProgress && litigationAnalysis is not null;
            litVm.NeedsAnalysis = !compact && !litVm.AnalysisInProgress && litigationAnalysis is not null
                && await litigationAnalysis.NeedsAnalysisAsync(id, litCt);
            litVm.RefreshIntervalDays = bprOpts.RefreshIntervalDays;

            var job = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == id)
                .Select(j => new LitigationSearchJob { LitigationSearchJobId = j.LitigationSearchJobId, RequestId = j.RequestId,
                    Status = j.Status, ProgressPercent = j.ProgressPercent, StatusMessage = j.StatusMessage,
                    FailureReason = j.FailureReason, RawResponseHash = j.RawResponseHash, KeywordsJson = j.KeywordsJson,
                    CreatedUtc = j.CreatedUtc, CompletedUtc = j.CompletedUtc }).FirstOrDefaultAsync(ct);
            litVm.SearchJob = job;

            if (job is not null)
            {
                LitigationReportSnapshot? currentAttemptSnapshot = null;
                if (!string.IsNullOrWhiteSpace(job.RawResponseHash))
                {
                    currentAttemptSnapshot = await db.LitigationReportSnapshots.AsNoTracking()
                        .AsNoTracking()
                        .Where(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.ReportHash == job.RawResponseHash)
                        .Select(s => new LitigationReportSnapshot
                        {
                            LitigationReportSnapshotId = s.LitigationReportSnapshotId, LitigationSearchJobId = s.LitigationSearchJobId,
                            RequestId = s.RequestId, ReportHash = s.ReportHash, Status = s.Status, RetrievedUtc = s.RetrievedUtc,
                            FailureReason = s.FailureReason, ReusedFromRequestId = s.ReusedFromRequestId,
                            ReusedFromSnapshotId = s.ReusedFromSnapshotId, OriginSnapshotId = s.OriginSnapshotId,
                            CasesPersistedCount = s.CasesPersistedCount, ReportFormat = s.ReportFormat
                        }).FirstOrDefaultAsync(ct);
                }
                litVm.CurrentAttemptSnapshot = currentAttemptSnapshot;

                if (string.IsNullOrWhiteSpace(job.RawResponseHash))
                {
                    litVm.ImportState = SnapshotImportState.NotCreated;
                }
                else if (currentAttemptSnapshot is null)
                {
                    litVm.ImportState = job.Status == LitigationSearchJobStatus.Completed
                        ? SnapshotImportState.SnapshotMissing
                        : SnapshotImportState.NotCreated;
                }
                else
                {
                    litVm.ImportState = currentAttemptSnapshot.Status switch
                    {
                        LitigationReportSnapshotStatus.Pending => SnapshotImportState.InProgress,
                        LitigationReportSnapshotStatus.InProgress => SnapshotImportState.InProgress,
                        LitigationReportSnapshotStatus.Completed => SnapshotImportState.Completed,
                        LitigationReportSnapshotStatus.Failed => SnapshotImportState.Failed,
                        _ => SnapshotImportState.NotCreated
                    };
                }

                var authoritativeSnapshot = await LitigationSnapshotSelection.ForRequest(db, id)
                        .Select(s => new LitigationReportSnapshot
                        {
                            LitigationReportSnapshotId = s.LitigationReportSnapshotId, LitigationSearchJobId = s.LitigationSearchJobId,
                            RequestId = s.RequestId, ReportHash = s.ReportHash, Status = s.Status, RetrievedUtc = s.RetrievedUtc,
                            FailureReason = s.FailureReason, ReusedFromRequestId = s.ReusedFromRequestId,
                            ReusedFromSnapshotId = s.ReusedFromSnapshotId, OriginSnapshotId = s.OriginSnapshotId,
                            CasesPersistedCount = s.CasesPersistedCount, ReportFormat = s.ReportFormat
                        }).FirstOrDefaultAsync(ct);
                var isPriorRun = authoritativeSnapshot is not null
                    && authoritativeSnapshot.LitigationReportSnapshotId != currentAttemptSnapshot?.LitigationReportSnapshotId;

                litVm.AuthoritativeSnapshot = authoritativeSnapshot;
                litVm.IsPriorRunDataShown = isPriorRun;

                if (authoritativeSnapshot?.ReusedFromRequestId is { } reusedReqId)
                {
                    litVm.ReusedFromRequestNumber = await db.Requests.AsNoTracking()
                        .AsNoTracking()
                        .Where(r => r.RequestId == reusedReqId)
                        .Select(r => r.RequestNumber)
                        .FirstOrDefaultAsync(ct);
                }

                if (authoritativeSnapshot is not null)
                {
                    var authoritativeSnapshotId = authoritativeSnapshot.LitigationReportSnapshotId;
                    var caseIdsQuery = db.LitigationCaseSourceReports.AsNoTracking()
                        .Where(sr => sr.LitigationReportSnapshotId == authoritativeSnapshotId)
                        .Select(sr => sr.LitigationCaseId)
                        .Distinct();

                    var casesQuery = db.LitigationCases.AsNoTracking()
                        .AsNoTracking()
                        .Where(c => caseIdsQuery.Contains(c.LitigationCaseId));

                    if (!string.IsNullOrWhiteSpace(court))
                    {
                        var trimmedCourt = court.Trim();
                        if (string.Equals(trimmedCourt, "Unspecified Court", StringComparison.OrdinalIgnoreCase))
                        {
                            casesQuery = casesQuery.Where(c => c.Court == null || c.Court.Trim() == "");
                        }
                        else
                        {
                            casesQuery = casesQuery.Where(c => c.Court != null && c.Court.Trim() == trimmedCourt);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        if (Enum.TryParse<LitigationCaseStatusBucket>(status, true, out var bucket))
                        {
                            switch (bucket)
                            {
                                case LitigationCaseStatusBucket.Pending:
                                    casesQuery = casesQuery.Where(LitigationCaseStatusClassifier.IsPendingExpr);
                                    break;
                                case LitigationCaseStatusBucket.Disposed:
                                    casesQuery = casesQuery.Where(LitigationCaseStatusClassifier.IsDisposedExpr);
                                    break;
                                case LitigationCaseStatusBucket.Unknown:
                                    casesQuery = casesQuery.Where(LitigationCaseStatusClassifier.IsUnknownExpr);
                                    break;
                            }
                        }
                        else
                        {
                            var lowerStatus = status.Trim().ToLower();
                            casesQuery = casesQuery.Where(c => c.CaseStatus != null && c.CaseStatus.ToLower() == lowerStatus);
                        }
                    }

                    if (!compact && !string.IsNullOrWhiteSpace(outcome) && Enum.TryParse<LitigationOrderOutcome>(outcome, true, out var parsedOutcome))
                    {
                        var matchingCaseIds = outcomeLookup.Matches
                            .Where(m => m.Outcomes.Contains(parsedOutcome))
                            .Select(m => m.LitigationCaseId)
                            .Distinct()
                            .ToList();

                        casesQuery = casesQuery.Where(c => matchingCaseIds.Contains(c.LitigationCaseId));
                    }

                    if (compact)
                        casesQuery = await new LitigationCaseListQuery(db, orderOutcomes)
                            .FilterAsync(authoritativeSnapshotId, court, status, search, outcome, id, ct);
                    litVm.TotalCaseCount = await casesQuery.CountAsync(ct);
                    litVm.CurrentPage = Math.Clamp(litVm.CurrentPage, 1, Math.Max(1, litVm.TotalPages));

                    var courtSummaryData = await (compact ? db.LitigationCases.AsNoTracking().Where(c => caseIdsQuery.Contains(c.LitigationCaseId)) : casesQuery)
                        .GroupBy(c => (c.Court == null || c.Court.Trim() == "") ? "Unspecified Court" : c.Court.Trim())
                        .Select(g => new
                        {
                            CourtName = g.Key,
                            CourtCategory = g.Select(x => x.CourtCategory).FirstOrDefault(cat => cat != null && cat != ""),
                            TotalCases = g.Count(),
                            TotalOrders = g.Sum(c => c.Orders.Count),
                            DisposedCases = g.Sum(c => (
                                (c.CaseStatus != null && (
                                    c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
                                    c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
                                    c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
                                    c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
                                    c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject")
                                )) || (c.CaseStage != null && (
                                    c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
                                    c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
                                    c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
                                    c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
                                    c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject")
                                ))
                            ) ? 1 : 0),
                            PendingCases = g.Sum(c => (
                                !(
                                    (c.CaseStatus != null && (
                                        c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
                                        c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
                                        c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
                                        c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
                                        c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject")
                                    )) || (c.CaseStage != null && (
                                        c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
                                        c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
                                        c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
                                        c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
                                        c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject")
                                    ))
                                ) && (
                                    (c.CaseStatus != null && (
                                        c.CaseStatus.ToLower().Contains("pend")  || c.CaseStatus.ToLower().Contains("admit") ||
                                        c.CaseStatus.ToLower().Contains("hear")  || c.CaseStatus.ToLower().Contains("stage") ||
                                        c.CaseStatus.ToLower().Contains("evid")  || c.CaseStatus.ToLower().Contains("argum") ||
                                        c.CaseStatus.ToLower().Contains("notic") || c.CaseStatus.ToLower().Contains("stay") ||
                                        c.CaseStatus.ToLower().Contains("trial") || c.CaseStatus.ToLower().Contains("appear")
                                    )) || (c.CaseStage != null && (
                                        c.CaseStage.ToLower().Contains("pend")  || c.CaseStage.ToLower().Contains("admit") ||
                                        c.CaseStage.ToLower().Contains("hear")  || c.CaseStage.ToLower().Contains("stage") ||
                                        c.CaseStage.ToLower().Contains("evid")  || c.CaseStage.ToLower().Contains("argum") ||
                                        c.CaseStage.ToLower().Contains("notic") || c.CaseStage.ToLower().Contains("stay") ||
                                        c.CaseStage.ToLower().Contains("trial") || c.CaseStage.ToLower().Contains("appear")
                                    ))
                                )
                            ) ? 1 : 0)
                        })
                        .OrderBy(x => x.CourtName)
                        .ToListAsync(ct);

                    var summaryGrid = new LitigationCourtSummaryGrid();
                    foreach (var d in courtSummaryData)
                    {
                        summaryGrid.Rows.Add(new LitigationCourtSummaryRow
                        {
                            CourtName = d.CourtName,
                            CourtCategory = d.CourtCategory,
                            TotalCases = d.TotalCases,
                            PendingCases = d.PendingCases,
                            DisposedCases = d.DisposedCases,
                            UnknownCases = d.TotalCases - d.PendingCases - d.DisposedCases,
                            TotalOrders = d.TotalOrders
                        });
                    }
                    litVm.CourtSummaryGrid = summaryGrid;

                    var keywords = LitigationKeyword.ParseJson(job.KeywordsJson).ToList();

                    var allCompletedSnapshots = await db.LitigationReportSnapshots.AsNoTracking()
                        .AsNoTracking()
                        .Where(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.Status == LitigationReportSnapshotStatus.Completed)
                        .OrderByDescending(s => s.RetrievedUtc)
                        .Select(s => new LitigationSnapshotSummary(s.LitigationReportSnapshotId, s.RetrievedUtc, s.ReportHash, s.CasesPersistedCount, s.ReportFormat))
                        .ToListAsync(ct);

                    var orderCounts = await (
                        from o in db.LitigationCaseOrders
                        where caseIdsQuery.Contains(o.LitigationCaseId)
                        join d in db.LitigationOrderDocuments on o.LitigationCaseOrderId equals d.LitigationCaseOrderId into docs
                        from doc in docs.DefaultIfEmpty()
                        select (LitigationOrderDocumentStatus?)(doc != null ? doc.Status : null)
                    ).GroupBy(_ => 1).Select(g => new
                    {
                        Total = g.Count(), Downloaded = g.Count(x => x == LitigationOrderDocumentStatus.Downloaded),
                        Failed = g.Count(x => x == LitigationOrderDocumentStatus.Failed), Expired = g.Count(x => x == LitigationOrderDocumentStatus.Expired),
                        Pending = g.Count(x => x == null || x == LitigationOrderDocumentStatus.Pending || x == LitigationOrderDocumentStatus.InProgress)
                    }).FirstOrDefaultAsync(ct);
                    var totalOrdersCount = orderCounts?.Total ?? 0;
                    var downloadedCount = orderCounts?.Downloaded ?? 0;
                    var failedCount = orderCounts?.Failed ?? 0;
                    var expiredCount = orderCounts?.Expired ?? 0;
                    var pendingOrdersCount = orderCounts?.Pending ?? 0;

                    var totalObservationsCount = await db.LitigationCaseSourceReports.AsNoTracking()
                        .Where(sr => sr.LitigationReportSnapshotId == authoritativeSnapshotId)
                        .CountAsync(ct);
                    var uniqueCasesCount = await caseIdsQuery.CountAsync(ct);

                    bool isAuthoritativeCoverage = !isPriorRun && litVm.ImportState == SnapshotImportState.Completed;

                    litVm.SourceCoverage = new LitigationSourceCoverageViewModel
                    {
                        Keywords = keywords,
                        Snapshots = allCompletedSnapshots,
                        IsAuthoritativeCoverage = isAuthoritativeCoverage,
                        CoverageSummaryText = isAuthoritativeCoverage
                            ? $"Authoritative snapshot ({Ist.Format(authoritativeSnapshot.RetrievedUtc)}) de-duplicated across {keywords.Count} search keywords into {uniqueCasesCount} unique legal proceedings ({totalObservationsCount} source observations). Request history includes {allCompletedSnapshots.Count} completed search snapshot(s)."
                            : $"Showing data from previous completed search snapshot retrieved {Ist.Format(authoritativeSnapshot.RetrievedUtc)}.",
                        UniqueCasesCount = uniqueCasesCount,
                        TotalObservationsCount = totalObservationsCount,
                        TotalOrders = totalOrdersCount,
                        DownloadedOrders = downloadedCount,
                        PendingOrders = pendingOrdersCount,
                        FailedOrders = failedCount,
                        ExpiredOrders = expiredCount
                    };

                    if (!compact && focusCase.HasValue)
                    {
                        var matchingOrderedIds = await casesQuery
                            .OrderBy(c => (c.Court == null || c.Court.Trim() == "") ? "Unspecified Court" : c.Court.Trim())
                            .ThenByDescending(c => c.LastHearingDate ?? string.Empty)
                            .ThenBy(c => c.LitigationCaseId)
                            .Select(c => c.LitigationCaseId)
                            .ToListAsync(ct);
                        var caseIdx = matchingOrderedIds.IndexOf(focusCase.Value);
                        if (caseIdx >= 0)
                        {
                            litVm.CurrentPage = (caseIdx / litVm.PageSize) + 1;
                        }
                    }

                    // Age is measured to the day the data was retrieved (IST), so the page and the reports agree whenever opened.
                    var ageAsOf = DateOnly.FromDateTime(Ist.FromUtc(authoritativeSnapshot.RetrievedUtc));
                    litVm.AgeAsOf = ageAsOf;
                    var companyNames = new List<string>();
                    if (!string.IsNullOrWhiteSpace(request.CompanyName)) companyNames.Add(request.CompanyName);
                    if (request.LatestCompletedIngestionRunId is { } nameRunId)
                        companyNames.AddRange(vm.NameHistory.Where(h => !string.IsNullOrWhiteSpace(h.PreviousName)).Select(h => h.PreviousName!));
                    var ageRows = await db.LitigationCases.AsNoTracking()
                        .Where(c => caseIdsQuery.Contains(c.LitigationCaseId))
                        .Select(c => new
                        {
                            c.LitigationCaseId, c.FilingDate, c.DecisionDate, c.CaseYear, c.Cnr, c.CaseNumber, c.CaseStatus, c.CaseStage,
                            c.Type, c.Court, c.CourtCategory, c.CaseType, c.Act, c.CaseClassification, c.ProceedingType, c.PetitionersJson, c.RespondentsJson, c.Direction
                        })
                        .ToListAsync(ct);
                    var orderDatesOfCase = (await db.LitigationCaseOrders.AsNoTracking()
                            .Where(o => caseIdsQuery.Contains(o.LitigationCaseId))
                            .Select(o => new { o.LitigationCaseId, o.OrderDate }).ToListAsync(ct))
                        .GroupBy(o => o.LitigationCaseId).ToDictionary(g => g.Key, g => g.Select(o => o.OrderDate).ToList());
                    foreach (var r in ageRows)
                    {
                        var rowStatus = LitigationCaseStatusClassifier.Classify(r.CaseStatus, r.CaseStage);
                        litVm.AgeProfile.Add(r.LitigationCaseId, LitigationCaseAges.Compute(r.FilingDate, r.DecisionDate, r.CaseYear, rowStatus,
                            orderDatesOfCase.GetValueOrDefault(r.LitigationCaseId) ?? [], ageAsOf, r.Cnr, r.CaseNumber));
                        var rowPetitioners = LitigationCaseCardMapper.ParseNames(r.PetitionersJson);
                        var rowSide = LitigationCompanySides.Determine(rowPetitioners, LitigationCaseCardMapper.ParseNames(r.RespondentsJson), companyNames, r.Direction);
                        litVm.RiskProfile.Add(LitigationBaselineRisk.Assess(new LitigationRiskInput(
                            r.Type, r.Court, r.CourtCategory, r.CaseType, r.Act, r.CaseStage, r.CaseClassification, r.ProceedingType, rowStatus, rowSide, rowPetitioners)));
                    }

                    if (compact)
                    {
                        var portfolio = await (from pa in db.LitigationPortfolioAiAnalyses.AsNoTracking()
                            join ar in db.LitigationAiAnalysisRuns on pa.LitigationAiAnalysisRunId equals ar.LitigationAiAnalysisRunId
                            where ar.RequestId == id
                            orderby ar.RunNumber descending
                            select new { pa.Status, pa.CompletedUtc, pa.AnalysisJson, ar.RunNumber }).FirstOrDefaultAsync(ct);
                        if (portfolio is not null)
                        {
                            litVm.PortfolioAnalysis = new LitigationPortfolioAiAnalysisViewModel
                            { Status = portfolio.Status, CompletedUtc = portfolio.CompletedUtc, RunNumber = portfolio.RunNumber };
                            if (!string.IsNullOrWhiteSpace(portfolio.AnalysisJson))
                                try
                                {
                                    using var json = JsonDocument.Parse(portfolio.AnalysisJson);
                                    if (json.RootElement.TryGetProperty("summary", out var summary)) litVm.PortfolioAnalysis.Summary = summary.GetString();
                                    if (json.RootElement.TryGetProperty("unknowns", out var unknowns) && unknowns.ValueKind == JsonValueKind.Array)
                                        litVm.PortfolioAnalysis.Unknowns = unknowns.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                                }
                                catch (JsonException) { }
                        }
                        litVm.CaseList = await new LitigationCaseListQuery(db, orderOutcomes)
                            .BuildAsync(id, court, status, page, litVm.PageSize, search, outcome, focusCase, ct);
                        litVm.Cases = litVm.CaseList!.Cases;
                        litVm.CurrentPage = litVm.CaseList.Page;
                    }
                    else
                    {
                    var pagedCases = await casesQuery
                        .OrderBy(c => (c.Court == null || c.Court.Trim() == "") ? "Unspecified Court" : c.Court.Trim())
                        .ThenByDescending(c => c.LastHearingDate ?? string.Empty)
                        .ThenBy(c => c.LitigationCaseId)
                        .Skip((litVm.CurrentPage - 1) * litVm.PageSize)
                        .Take(litVm.PageSize)
                        .Include(c => c.Orders)
                        .ToListAsync(ct);

                    var pagedCaseIds = pagedCases.Select(c => c.LitigationCaseId).ToList();

                    var orderIds = pagedCases.SelectMany(c => c.Orders).Select(o => o.LitigationCaseOrderId).Distinct().ToList();
                    var orderDocs = await db.LitigationOrderDocuments.AsNoTracking()
                        .AsNoTracking()
                        .Where(d => orderIds.Contains(d.LitigationCaseOrderId))
                        .ToListAsync(ct);
                    var orderDocByOrderId = orderDocs.ToDictionary(d => d.LitigationCaseOrderId);

                    var companyProfile = vm.CompanyProfile;
                    var epfoEsts = vm.EpfoEstablishments;
                    var charges = vm.Charges.Where(c => c.SatisfactionDate == null).ToList();

                    var addressPool = LitigationOrderAddressMatcher.BuildAddressPool(companyProfile, epfoEsts, charges);
                    var propertyMatches = LitigationOrderAddressMatcher.MatchCases(addressPool, pagedCases, orderDocByOrderId);
                    var propertyMatchesByOrderId = propertyMatches
                        .GroupBy(m => m.LitigationCaseOrderId)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var latestAiRun = await db.LitigationAiAnalysisRuns.AsNoTracking()
                        .AsNoTracking()
                        .Where(r => r.RequestId == id)
                        .OrderByDescending(r => r.RunNumber)
                        .Include(r => r.PortfolioAnalysis)
                        .FirstOrDefaultAsync(ct);

                    Dictionary<long, LitigationCaseAiAnalysis> caseAiByCaseId = [];
                    if (latestAiRun is not null)
                    {
                        var caseAnalyses = await db.LitigationCaseAiAnalyses.AsNoTracking()
                            .AsNoTracking()
                            .Where(ca => ca.LitigationAiAnalysisRunId == latestAiRun.LitigationAiAnalysisRunId && pagedCaseIds.Contains(ca.LitigationCaseId))
                            .ToListAsync(ct);
                        caseAiByCaseId = caseAnalyses.ToDictionary(ca => ca.LitigationCaseId);

                        if (latestAiRun.PortfolioAnalysis is { } pa)
                        {
                            var vmPa = new LitigationPortfolioAiAnalysisViewModel
                            {
                                LitigationPortfolioAiAnalysisId = pa.LitigationPortfolioAiAnalysisId,
                                Status = pa.Status,
                                CompletedUtc = pa.CompletedUtc,
                                FailureReason = pa.FailureReason,
                                RunNumber = latestAiRun.RunNumber
                            };
                            if (!string.IsNullOrWhiteSpace(pa.AnalysisJson))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(pa.AnalysisJson);
                                    var root = doc.RootElement;
                                    if (root.TryGetProperty("summary", out var sProp)) vmPa.Summary = sProp.GetString();
                                    if (root.TryGetProperty("unknowns", out var uProp) && uProp.ValueKind == JsonValueKind.Array)
                                    {
                                        vmPa.Unknowns = uProp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
                                    }
                                }
                                catch { }
                            }
                            litVm.PortfolioAnalysis = vmPa;
                        }
                    }

                    var mapContext = new LitigationCaseMapContext(orderDocByOrderId, propertyMatchesByOrderId, outcomeByOrderId, outcomeLookup,
                        caseAiByCaseId, latestAiRun?.RunNumber ?? 0, ageAsOf, companyNames);
                    foreach (var c in pagedCases) litVm.Cases.Add(LitigationCaseCardMapper.Map(c, mapContext));
                    }
                }
            }

            vm.LitigationDataLake = litVm;
        }

    }
}
