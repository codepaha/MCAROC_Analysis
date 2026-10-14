using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Fetching;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Freshness;

public sealed record CourtRefreshStatusDto(
    string State,
    int Percent,
    int Handled,
    int Total,
    double? EstimatedRemainingSeconds,
    string? EstimatedRemainingDisplay,
    string Phase,
    string PhaseLabel,
    DateTime? StartedUtc,
    string? StartedIstDisplay,
    DateTime? LastAgentHeartbeatUtc,
    string? Message,
    bool Finished,
    DateTime? FinishedUtc,
    WhatIsNotUpdatedSummaryDto? NotUpdated,
    string? FinishedIstDateDisplay = null);

public sealed record WhatIsNotUpdatedSummaryDto(
    int TotalCases,
    int UpdatedCount,
    int NotUpdatedCount,
    int QueuedCount,
    int InProgressCount,
    int NextHearingFutureCount,
    int DisposedCount,
    int NotFoundCount,
    int IdentityMismatchCount,
    int FailedAttemptsCount,
    IReadOnlyList<ReasonCountDto> CommonFailureReasons,
    IReadOnlyDictionary<string, int> MissingFieldsCount,
    int OrdersNotDownloadedCount,
    int OrdersExtractionPendingCount,
    int OrdersUnsupportedFormatCount,
    int OrdersDratCount,
    int CasesQueuedForAnalysisCount,
    bool PortfolioSummaryPending);

public sealed record ReasonCountDto(string Reason, int Count);

public sealed record LitigationDataCurrencySummary(
    DateTime? AsOfUtc,
    string AsOfIst,
    int TotalCases,
    int RefreshedCasesCount,
    int UnrefreshedCasesCount,
    string CurrencyNote = "Unrefreshed cases show the last known values.",
    bool RefreshInProgress = false,
    string? InProgressNote = null);

public interface ICourtRefreshStatusService
{
    Task<CourtRefreshStatusDto> GetRefreshStatusAsync(long requestId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<long, CaseFreshnessResult>> GetCaseFreshnessBatchAsync(long requestId, IReadOnlyList<long> caseIds, CancellationToken ct = default);
    Task<CaseFreshnessResult> GetCaseFreshnessAsync(long requestId, long caseId, CancellationToken ct = default);
    Task<WhatIsNotUpdatedSummaryDto> GetWhatIsNotUpdatedSummaryAsync(long requestId, CancellationToken ct = default);
    Task<WhatIsNotUpdatedSummaryDto> GetWhatIsNotUpdatedSummaryAsync(long requestId, IReadOnlyList<long>? caseIds, CancellationToken ct = default);
    Task<LitigationDataCurrencySummary> GetDataCurrencySummaryAsync(long requestId, CancellationToken ct = default);
    Task<LitigationDataCurrencySummary> GetDataCurrencySummaryAsync(long requestId, IReadOnlyList<long>? caseIds, CancellationToken ct = default);
    Task<LitigationDataCurrencySummary> GetDossierDataCurrencySummaryAsync(long requestId, IReadOnlyList<Litigation> dossierLitigations, DateTime? sourceSnapshotUtc = null, CancellationToken ct = default);
}

public sealed class CourtRefreshStatusService(
    AppDbContext db,
    TimeProvider? time = null,
    LitigationReaderGate? readerGate = null) : ICourtRefreshStatusService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime NowUtc => _time.GetUtcNow().UtcDateTime;

    private async Task<List<long>> ResolveRequestCaseIdsAsync(long requestId, CancellationToken ct)
    {
        if (readerGate is not null)
        {
            var snapshotId = await LitigationSnapshotSelection.ForRequest(db, requestId)
                .Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
            if (snapshotId is { } sid)
            {
                var source = await readerGate.ResolveCaseListAsync(requestId, ct);
                var activeQuery = await LitigationCaseSets.ForAsync(db, source, requestId, sid, ct);
                return await activeQuery.ToListAsync(ct);
            }
            return [];
        }

        return await db.LitigationCases.AsNoTracking()
            .Where(c => c.RequestId == requestId && c.MergedIntoCaseId == null)
            .Where(LitigationData.Attribution.LitigationAttribution.IsShown)
            .Select(c => c.LitigationCaseId)
            .ToListAsync(ct);
    }

    public async Task<CourtRefreshStatusDto> GetRefreshStatusAsync(long requestId, CancellationToken ct = default)
    {
        var run = await db.Set<ModuleRefreshRun>().AsNoTracking()
            .Where(r => r.RequestId == requestId)
            .OrderByDescending(r => r.RunId)
            .FirstOrDefaultAsync(ct);

        var notUpdated = await GetWhatIsNotUpdatedSummaryAsync(requestId, ct);

        if (run is null)
        {
            return new CourtRefreshStatusDto(
                State: RefreshBannerState.Idle.ToString(),
                Percent: 0,
                Handled: 0,
                Total: 0,
                EstimatedRemainingSeconds: null,
                EstimatedRemainingDisplay: null,
                Phase: RefreshPhase.RefreshingCases.ToString(),
                PhaseLabel: "Refreshing cases",
                StartedUtc: null,
                StartedIstDisplay: null,
                LastAgentHeartbeatUtc: null,
                Message: null,
                Finished: false,
                FinishedUtc: null,
                NotUpdated: notUpdated,
                FinishedIstDateDisplay: null);
        }

        var runItemLinks = await db.Set<ModuleFetchWorkItemRun>().AsNoTracking()
            .Where(l => l.RunId == run.RunId)
            .Select(l => l.WorkItemId)
            .ToListAsync(ct);

        var items = runItemLinks.Count == 0
            ? []
            : await db.Set<ModuleFetchWorkItem>().AsNoTracking()
                .Where(w => runItemLinks.Contains(w.WorkItemId))
                .ToListAsync(ct);

        var completionTimes = items
            .Where(w => w.CompletedUtc != null)
            .Select(w => w.CompletedUtc!.Value)
            .OrderBy(t => t)
            .ToList();

        var handled = items.Count(w =>
            w.Status == (int)FetchWorkStatus.Succeeded
            || w.Status == (int)FetchWorkStatus.Failed
            || w.Status == (int)FetchWorkStatus.Cancelled);

        var input = new RefreshProgressInput(
            StartedUtc: run.StartedUtc,
            EligibleAtStart: run.EligibleAtStart,
            Handled: handled,
            CompletionTimesUtc: completionTimes,
            LastAgentHeartbeatUtc: run.LastAgentHeartbeatUtc,
            Phase: (RefreshPhase)run.Phase,
            StageFailedReason: run.FailedReason,
            Finished: run.FinishedUtc != null);

        var banner = RefreshBannerCalculator.Compute(input, NowUtc);

        var estDisplay = FormatTimeSpanEstimate(banner.EstimatedRemaining);
        var phaseLabel = FormatPhaseLabel(banner.Phase);
        var startedIst = run.StartedUtc == default ? null : Ist.Format(run.StartedUtc, "HH:mm") + " IST";
        var finishedIstDate = run.FinishedUtc is { } finUtc ? Ist.Date(finUtc, "dd-MMM-yyyy") : null;

        return new CourtRefreshStatusDto(
            State: banner.State.ToString(),
            Percent: banner.Percent,
            Handled: banner.Handled,
            Total: banner.Total,
            EstimatedRemainingSeconds: banner.EstimatedRemaining?.TotalSeconds,
            EstimatedRemainingDisplay: estDisplay,
            Phase: banner.Phase.ToString(),
            PhaseLabel: phaseLabel,
            StartedUtc: run.StartedUtc,
            StartedIstDisplay: startedIst,
            LastAgentHeartbeatUtc: run.LastAgentHeartbeatUtc,
            Message: banner.Message,
            Finished: run.FinishedUtc != null,
            FinishedUtc: run.FinishedUtc,
            NotUpdated: notUpdated,
            FinishedIstDateDisplay: finishedIstDate);
    }

    public async Task<CaseFreshnessResult> GetCaseFreshnessAsync(long requestId, long caseId, CancellationToken ct = default)
    {
        var map = await GetCaseFreshnessBatchAsync(requestId, [caseId], ct);
        return map.TryGetValue(caseId, out var result)
            ? result
            : new CaseFreshnessResult(FreshnessLabel.NotYetUpdated, null, "case not found", false);
    }

    public async Task<IReadOnlyDictionary<long, CaseFreshnessResult>> GetCaseFreshnessBatchAsync(
        long requestId, IReadOnlyList<long> caseIds, CancellationToken ct = default)
    {
        if (caseIds.Count == 0) return new Dictionary<long, CaseFreshnessResult>();

        var distinctIds = caseIds.Distinct().ToList();

        var cases = await db.LitigationCases.AsNoTracking()
            .Where(c => c.RequestId == requestId && distinctIds.Contains(c.LitigationCaseId))
            .Select(c => new
            {
                c.LitigationCaseId,
                c.CaseNumber,
                c.CaseType,
                c.CaseYear,
                c.FilingDate,
                c.Cnr,
                c.Court,
                c.State,
                c.District,
                c.CaseStatus,
                c.CaseStage,
                c.LastHearingDate,
                c.NextHearingDate,
                c.DecisionDate,
                c.MergedIntoCaseId,
                c.LastSeenUtc
            })
            .ToListAsync(ct);

        var moduleCases = await db.Set<ModuleCase>().AsNoTracking()
            .Where(mc => mc.LegacyLitigationCaseId != null && distinctIds.Contains(mc.LegacyLitigationCaseId.Value))
            .Select(mc => new { mc.CaseId, LegacyId = mc.LegacyLitigationCaseId!.Value })
            .ToListAsync(ct);

        var legacyToModule = moduleCases.ToDictionary(mc => mc.LegacyId, mc => mc.CaseId);
        var moduleIds = moduleCases.Select(mc => mc.CaseId).Distinct().ToList();

        var lives = moduleIds.Count == 0
            ? []
            : await db.Set<ModuleCaseLive>().AsNoTracking()
                .Where(l => moduleIds.Contains(l.CaseId))
                .ToDictionaryAsync(l => l.CaseId, ct);

        // Fetch latest observations per module case
        var observations = moduleIds.Count == 0
            ? []
            : await db.Set<ModuleCaseObservation>().AsNoTracking()
                .Where(o => moduleIds.Contains(o.CaseId))
                .GroupBy(o => o.CaseId)
                .Select(g => g.OrderByDescending(o => o.RetrievedUtc).ThenByDescending(o => o.ObservationId).First())
                .ToDictionaryAsync(o => o.CaseId, ct);

        // Check status work items (both active and failed/completed for failure reasons)
        var statusWorkItems = moduleIds.Count == 0
            ? []
            : await db.Set<ModuleFetchWorkItem>().AsNoTracking()
                .Where(w => w.CaseId != null && moduleIds.Contains(w.CaseId.Value) && w.Kind == (int)FetchWorkKind.StatusFetch)
                .ToListAsync(ct);

        var activeStatusItemCaseIds = statusWorkItems
            .Where(w => w.Status == (int)FetchWorkStatus.Queued || w.Status == (int)FetchWorkStatus.Claimed)
            .Select(w => w.CaseId!.Value)
            .ToHashSet();

        var latestStatusWorkByCaseId = statusWorkItems
            .Where(w => w.LastError != null || w.LastOutcome != null || w.Status == (int)FetchWorkStatus.Failed)
            .GroupBy(w => w.CaseId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.CompletedUtc ?? w.CreatedUtc).ThenByDescending(w => w.WorkItemId).First());

        // Check final order presence
        var finalOrderCaseIds = (await db.LitigationCaseOrders.AsNoTracking()
            .Where(o => distinctIds.Contains(o.LitigationCaseId) && o.OrderType != null
                && (o.OrderType.Contains("Final") || o.OrderType.Contains("Judgment") || o.OrderType.Contains("Disposal")))
            .Select(o => o.LitigationCaseId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        // Check AI analysis freshness
        var latestRun = await db.LitigationAiAnalysisRuns.AsNoTracking()
            .Where(r => r.RequestId == requestId)
            .OrderByDescending(r => r.RunNumber)
            .Select(r => new { r.LitigationAiAnalysisRunId })
            .FirstOrDefaultAsync(ct);

        var analysisStaleCaseIds = new HashSet<long>();
        if (latestRun != null)
        {
            var analyses = await db.LitigationCaseAiAnalyses.AsNoTracking()
                .Where(a => a.LitigationAiAnalysisRunId == latestRun.LitigationAiAnalysisRunId && distinctIds.Contains(a.LitigationCaseId))
                .Select(a => new { a.LitigationCaseId, a.CompletedUtc })
                .ToListAsync(ct);

            foreach (var a in analyses)
            {
                var c = cases.FirstOrDefault(x => x.LitigationCaseId == a.LitigationCaseId);
                if (c != null && a.CompletedUtc != null)
                {
                    var liveUpdated = legacyToModule.TryGetValue(c.LitigationCaseId, out var mId)
                        && lives.TryGetValue(mId, out var lv)
                        && lv.LastRetrievedUtc > a.CompletedUtc;
                    if (c.LastSeenUtc > a.CompletedUtc || liveUpdated)
                    {
                        analysisStaleCaseIds.Add(a.LitigationCaseId);
                    }
                }
            }
        }

        var todayIst = DateOnly.FromDateTime(Ist.FromUtc(NowUtc));
        var result = new Dictionary<long, CaseFreshnessResult>();

        foreach (var c in cases)
        {
            var hasModuleId = legacyToModule.TryGetValue(c.LitigationCaseId, out var modCaseId);
            var live = hasModuleId && lives.TryGetValue(modCaseId, out var l) ? l : null;
            var obs = hasModuleId && observations.TryGetValue(modCaseId, out var o) ? o : null;
            var work = hasModuleId && latestStatusWorkByCaseId.TryGetValue(modCaseId, out var w) ? w : null;
            var queued = hasModuleId && activeStatusItemCaseIds.Contains(modCaseId);
            var finalOrder = finalOrderCaseIds.Contains(c.LitigationCaseId);
            var analysisStale = analysisStaleCaseIds.Contains(c.LitigationCaseId);

            var numberParts = CaseNumberNormaliser.Parse(new CaseNumberInput(c.CaseNumber, c.CaseType, c.CaseYear, c.FilingDate, c.Cnr));
            var confirmedEmpty = ParseConfirmedEmpty(live?.FieldSourcesJson);

            var state = new LiveRefreshCaseState(
                CaseStatus: c.CaseStatus,
                CaseStage: c.CaseStage,
                Court: c.Court,
                State: c.State,
                District: c.District,
                FilingDate: c.FilingDate,
                LastHearingDate: c.LastHearingDate,
                NextHearingDate: c.NextHearingDate,
                NumberCompleteness: numberParts.Completeness,
                EverLiveVerified: live?.LastRetrievedUtc != null || (obs?.Verified ?? false),
                NewlyDiscovered: false,
                ConfirmedEmpty: confirmedEmpty,
                OrdersRequested: false,
                FinalOrderOnFile: finalOrder);

            var decision = LiveRefreshEligibility.Decide(state, todayIst, NowUtc);

            var lastAttemptKind = LastAttemptKind.None;
            string? failureReason = null;
            DateTime? lastAttemptUtc = null;

            if (obs != null)
            {
                lastAttemptUtc = obs.RetrievedUtc;
                lastAttemptKind = obs.Outcome switch
                {
                    1 => LastAttemptKind.Verified, // found
                    2 => LastAttemptKind.NotFound,
                    3 => LastAttemptKind.Failed,
                    4 => LastAttemptKind.IdentityRejected,
                    _ => LastAttemptKind.None
                };
            }

            if (work is { Status: (int)FetchWorkStatus.Failed })
            {
                var workUtc = work.CompletedUtc ?? work.CreatedUtc;
                if (obs == null || workUtc >= obs.RetrievedUtc)
                {
                    lastAttemptKind = LastAttemptKind.Failed;
                    lastAttemptUtc = workUtc;
                }
            }

            if (lastAttemptKind == LastAttemptKind.Failed)
            {
                failureReason = ResolveFailureReason(work?.LastError, obs?.AppliedAs, work?.LastOutcome);
            }

            var input = new CaseFreshnessInput(
                MergedAway: c.MergedIntoCaseId != null,
                StatusBucket: LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage),
                FinalOrderOnFile: finalOrder,
                Eligibility: decision,
                LastVerifiedUtc: live?.LastRetrievedUtc ?? (obs?.Verified == true ? obs.RetrievedUtc : null),
                LastAttempt: lastAttemptKind,
                LastAttemptUtc: lastAttemptUtc,
                FailureReason: failureReason,
                QueuedOrInProgress: queued,
                AnalysisOnEarlierData: analysisStale);

            result[c.LitigationCaseId] = CaseFreshness.Of(input);
        }

        return result;
    }

    public Task<WhatIsNotUpdatedSummaryDto> GetWhatIsNotUpdatedSummaryAsync(long requestId, CancellationToken ct = default)
        => GetWhatIsNotUpdatedSummaryAsync(requestId, null, ct);

    public async Task<WhatIsNotUpdatedSummaryDto> GetWhatIsNotUpdatedSummaryAsync(
        long requestId,
        IReadOnlyList<long>? caseIds,
        CancellationToken ct = default)
    {
        var ids = caseIds is not null
            ? caseIds.Distinct().ToList()
            : await ResolveRequestCaseIdsAsync(requestId, ct);

        var total = ids.Count;
        if (total == 0)
        {
            return new WhatIsNotUpdatedSummaryDto(
                TotalCases: 0,
                UpdatedCount: 0,
                NotUpdatedCount: 0,
                QueuedCount: 0,
                InProgressCount: 0,
                NextHearingFutureCount: 0,
                DisposedCount: 0,
                NotFoundCount: 0,
                IdentityMismatchCount: 0,
                FailedAttemptsCount: 0,
                CommonFailureReasons: [],
                MissingFieldsCount: new Dictionary<string, int>(),
                OrdersNotDownloadedCount: 0,
                OrdersExtractionPendingCount: 0,
                OrdersUnsupportedFormatCount: 0,
                OrdersDratCount: 0,
                CasesQueuedForAnalysisCount: 0,
                PortfolioSummaryPending: false);
        }

        var freshnessMap = await GetCaseFreshnessBatchAsync(requestId, ids, ct);

        var updated = freshnessMap.Values.Count(f => f.Label == FreshnessLabel.Updated);
        var notUpdated = total - updated;

        var moduleCases = await db.Set<ModuleCase>().AsNoTracking()
            .Where(mc => mc.LegacyLitigationCaseId != null && ids.Contains(mc.LegacyLitigationCaseId.Value))
            .Select(mc => new { mc.CaseId, LegacyId = mc.LegacyLitigationCaseId!.Value })
            .ToListAsync(ct);

        var moduleIds = moduleCases.Select(mc => mc.CaseId).Distinct().ToList();

        var activeStatusWork = moduleIds.Count == 0
            ? []
            : await db.Set<ModuleFetchWorkItem>().AsNoTracking()
                .Where(w => w.CaseId != null && moduleIds.Contains(w.CaseId.Value) && w.Kind == (int)FetchWorkKind.StatusFetch
                    && (w.Status == (int)FetchWorkStatus.Queued || w.Status == (int)FetchWorkStatus.Claimed))
                .Select(w => new { CaseId = w.CaseId!.Value, w.Status })
                .ToListAsync(ct);

        var queued = activeStatusWork.Count(w => w.Status == (int)FetchWorkStatus.Queued);
        var inProgress = activeStatusWork.Count(w => w.Status == (int)FetchWorkStatus.Claimed);
        var nextHearingFuture = freshnessMap.Values.Count(f => f.Label == FreshnessLabel.NotDue);
        var disposed = freshnessMap.Values.Count(f => f.Label is FreshnessLabel.DisposedFinalOrderOnFile or FreshnessLabel.DisposedNotRefreshed);
        var couldNotVerify = freshnessMap.Values.Where(f => f.Label == FreshnessLabel.CouldNotVerify).ToList();

        var notFound = couldNotVerify.Count(c => c.Reason?.Contains("no such case", StringComparison.OrdinalIgnoreCase) == true);
        var mismatch = couldNotVerify.Count(c => c.Reason?.Contains("different case", StringComparison.OrdinalIgnoreCase) == true
            || c.Reason?.Contains("identity", StringComparison.OrdinalIgnoreCase) == true);
        var failedCases = couldNotVerify
            .Where(c => c.Reason?.Contains("no such case", StringComparison.OrdinalIgnoreCase) != true
                && c.Reason?.Contains("different case", StringComparison.OrdinalIgnoreCase) != true
                && c.Reason?.Contains("identity", StringComparison.OrdinalIgnoreCase) != true)
            .ToList();
        var failed = failedCases.Count;

        var commonReasons = (failedCases.Count > 0 ? failedCases : couldNotVerify)
            .GroupBy(c => c.Reason ?? "Unknown")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(g => new ReasonCountDto(g.Key, g.Count()))
            .ToList();

        // Calculate fields still missing across all cases
        var cases = await db.LitigationCases.AsNoTracking()
            .Where(c => ids.Contains(c.LitigationCaseId))
            .Select(c => new
            {
                c.CaseStatus,
                c.Court,
                c.State,
                c.District,
                c.FilingDate,
                c.LastHearingDate,
                c.NextHearingDate,
                c.DecisionDate,
                c.CaseStage,
                c.LastSeenUtc
            })
            .ToListAsync(ct);

        var missingFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = cases.Count(c => CaseValue.IsMissing(c.CaseStatus)),
            ["court"] = cases.Count(c => CaseValue.IsMissing(c.Court)),
            ["state"] = cases.Count(c => CaseValue.IsMissing(c.State)),
            ["district"] = cases.Count(c => CaseValue.IsMissing(c.District)),
            ["filing date"] = cases.Count(c => CaseValue.IsMissing(c.FilingDate)),
            ["last hearing"] = cases.Count(c => CaseValue.IsMissing(c.LastHearingDate)),
            ["next hearing"] = cases.Count(c =>
                LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage) != LitigationCaseStatusBucket.Disposed
                && CaseValue.IsMissing(c.NextHearingDate)),
            ["decision date"] = cases.Count(c =>
                LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage) == LitigationCaseStatusBucket.Disposed
                && CaseValue.IsMissing(c.DecisionDate))
        };

        // Orders summary
        var orders = await db.LitigationCaseOrders.AsNoTracking()
            .Where(o => ids.Contains(o.LitigationCaseId))
            .Select(o => o.LitigationCaseOrderId)
            .ToListAsync(ct);

        var orderDocs = orders.Count == 0
            ? []
            : await db.LitigationOrderDocuments.AsNoTracking()
                .Where(d => orders.Contains(d.LitigationCaseOrderId))
                .Select(d => new { d.Status, d.TextExtractionStatus, d.ContentType, d.StoragePath, d.FailureReason })
                .ToListAsync(ct);

        var downloadedOrderDocCount = orderDocs.Count(d => d.Status == LitigationOrderDocumentStatus.Downloaded);
        var ordersNotDownloaded = orders.Count - downloadedOrderDocCount;
        var ordersUnsupportedFormat = orderDocs.Count(d => IsUnsupportedOrderFormat(d.ContentType, d.StoragePath, d.FailureReason));
        var ordersExtractionPending = orderDocs.Count(d => d.Status == LitigationOrderDocumentStatus.Downloaded
            && d.TextExtractionStatus != FilingDocumentProcessingStatus.TextExtracted
            && !IsUnsupportedOrderFormat(d.ContentType, d.StoragePath, d.FailureReason));
        var dratOrders = cases.Count(c => c.Court != null && c.Court.Contains("DRAT", StringComparison.OrdinalIgnoreCase));

        // Analysis summary
        var casesQueuedForAnalysis = freshnessMap.Values.Count(f => f.AnalysisOnEarlierData);
        var maxCaseLastSeenUtc = cases.Count > 0 ? cases.Max(c => c.LastSeenUtc) : (DateTime?)null;
        var portfolioSummaryPending = await IsPortfolioSummaryPendingAsync(requestId, ids, casesQueuedForAnalysis, maxCaseLastSeenUtc, ct);

        return new WhatIsNotUpdatedSummaryDto(
            TotalCases: total,
            UpdatedCount: updated,
            NotUpdatedCount: notUpdated,
            QueuedCount: queued,
            InProgressCount: inProgress,
            NextHearingFutureCount: nextHearingFuture,
            DisposedCount: disposed,
            NotFoundCount: notFound,
            IdentityMismatchCount: mismatch,
            FailedAttemptsCount: failed,
            CommonFailureReasons: commonReasons,
            MissingFieldsCount: missingFields,
            OrdersNotDownloadedCount: Math.Max(0, ordersNotDownloaded),
            OrdersExtractionPendingCount: ordersExtractionPending,
            OrdersUnsupportedFormatCount: ordersUnsupportedFormat,
            OrdersDratCount: dratOrders,
            CasesQueuedForAnalysisCount: casesQueuedForAnalysis,
            PortfolioSummaryPending: portfolioSummaryPending);
    }

    private async Task<bool> IsPortfolioSummaryPendingAsync(
        long requestId, IReadOnlyList<long> caseIds, int casesQueuedForAnalysis, DateTime? maxCaseLastSeenUtc, CancellationToken ct)
    {
        if (caseIds.Count == 0) return false;
        if (casesQueuedForAnalysis > 0) return true;

        var hasActiveRun = await db.LitigationAiAnalysisRuns.AsNoTracking()
            .AnyAsync(r => r.RequestId == requestId
                && (r.Status == LitigationAiAnalysisRunStatus.Pending || r.Status == LitigationAiAnalysisRunStatus.InProgress), ct);
        if (hasActiveRun) return true;

        var latestPortfolio = await (from pa in db.LitigationPortfolioAiAnalyses.AsNoTracking()
            join ar in db.LitigationAiAnalysisRuns.AsNoTracking() on pa.LitigationAiAnalysisRunId equals ar.LitigationAiAnalysisRunId
            where ar.RequestId == requestId
            orderby ar.RunNumber descending, pa.LitigationPortfolioAiAnalysisId descending
            select new { pa.Status, pa.CaseSetHash, pa.CompletedUtc })
            .FirstOrDefaultAsync(ct);

        if (latestPortfolio is null) return true;
        if (latestPortfolio.Status != LitigationAiAnalysisItemStatus.Completed
            && latestPortfolio.Status != LitigationAiAnalysisItemStatus.InsufficientEvidence)
        {
            return true;
        }

        var currentSetHash = LitigationAiAnalysisOrchestrator.CaseSetHash(caseIds);
        if (!string.Equals(latestPortfolio.CaseSetHash, currentSetHash, StringComparison.Ordinal))
            return true;

        if (latestPortfolio.CompletedUtc is { } pCompleted && maxCaseLastSeenUtc is { } maxSeen && maxSeen > pCompleted)
            return true;

        return false;
    }

    private static readonly HashSet<string> ProjectionOutcomeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ProjectionOutcome.Applied),
        nameof(ProjectionOutcome.IgnoredNotVerified),
        nameof(ProjectionOutcome.IgnoredLate),
        nameof(ProjectionOutcome.HeldStatusRegression),
        nameof(ProjectionOutcome.HeldShrinkingHistory)
    };

    private static string? ResolveFailureReason(string? workError, string? obsAppliedAs, string? workOutcome)
    {
        if (!string.IsNullOrWhiteSpace(workError)) return workError.Trim();
        if (!string.IsNullOrWhiteSpace(obsAppliedAs) && !ProjectionOutcomeNames.Contains(obsAppliedAs.Trim()))
            return obsAppliedAs.Trim();
        if (!string.IsNullOrWhiteSpace(workOutcome)) return workOutcome.Trim();
        return null;
    }

    internal static bool IsUnsupportedOrderFormat(string? contentType, string? storagePath, string? failureReason)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.Trim();
            if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("msword", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("application/zip", StringComparison.OrdinalIgnoreCase)
                || ct.Contains("x-zip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(storagePath))
        {
            var ext = Path.GetExtension(storagePath);
            if (ext.Equals(".doc", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(failureReason)
            && (failureReason.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || failureReason.Contains("unsupported", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public Task<LitigationDataCurrencySummary> GetDataCurrencySummaryAsync(long requestId, CancellationToken ct = default) =>
        GetDataCurrencySummaryAsync(requestId, caseIds: null, ct);

    public async Task<LitigationDataCurrencySummary> GetDataCurrencySummaryAsync(
        long requestId,
        IReadOnlyList<long>? caseIds,
        CancellationToken ct = default)
    {
        var (completedRunUtc, refreshInProgress, inProgressNote) = await GetRefreshRunStateAsync(requestId, ct);
        var sourceSnapshotUtc = await GetAuthoritativeSourceTimestampAsync(requestId, ct);

        var cases = caseIds is not null
            ? caseIds.Distinct().ToList()
            : await ResolveRequestCaseIdsAsync(requestId, ct);

        var latestObsUtc = await GetLatestObservationUtcForLegacyCasesAsync(cases, ct);
        var asOfUtc = MaxUtc(sourceSnapshotUtc, MaxUtc(latestObsUtc, completedRunUtc));
        var asOfIst = asOfUtc.HasValue ? Ist.Format(asOfUtc.Value, "dd-MMM-yyyy, HH:mm") : "Not yet retrieved";

        if (cases.Count == 0)
        {
            return new LitigationDataCurrencySummary(
                AsOfUtc: asOfUtc,
                AsOfIst: asOfIst,
                TotalCases: 0,
                RefreshedCasesCount: 0,
                UnrefreshedCasesCount: 0,
                RefreshInProgress: refreshInProgress,
                InProgressNote: inProgressNote);
        }

        var freshnessMap = await GetCaseFreshnessBatchAsync(requestId, cases, ct);
        var refreshed = freshnessMap.Values.Count(f => f.Label == FreshnessLabel.Updated);
        var unrefreshed = cases.Count - refreshed;

        return new LitigationDataCurrencySummary(
            AsOfUtc: asOfUtc,
            AsOfIst: asOfIst,
            TotalCases: cases.Count,
            RefreshedCasesCount: refreshed,
            UnrefreshedCasesCount: unrefreshed,
            RefreshInProgress: refreshInProgress,
            InProgressNote: inProgressNote);
    }

    public async Task<LitigationDataCurrencySummary> GetDossierDataCurrencySummaryAsync(
        long requestId,
        IReadOnlyList<Litigation> dossierLitigations,
        DateTime? sourceSnapshotUtc = null,
        CancellationToken ct = default)
    {
        var (_, refreshInProgress, inProgressNote) = await GetRefreshRunStateAsync(requestId, ct);
        var baseAsOfUtc = sourceSnapshotUtc ?? await GetIngestionSourceTimestampAsync(requestId, ct);

        if (dossierLitigations.Count == 0)
        {
            var emptyAsOfIst = baseAsOfUtc.HasValue ? Ist.Format(baseAsOfUtc.Value, "dd-MMM-yyyy, HH:mm") : "Not yet retrieved";
            return new LitigationDataCurrencySummary(
                AsOfUtc: baseAsOfUtc,
                AsOfIst: emptyAsOfIst,
                TotalCases: 0,
                RefreshedCasesCount: 0,
                UnrefreshedCasesCount: 0,
                RefreshInProgress: refreshInProgress,
                InProgressNote: inProgressNote);
        }

        var courtCaseIds = await ResolveRequestCaseIdsAsync(requestId, ct);
        var courtCases = courtCaseIds.Count == 0
            ? []
            : await db.LitigationCases.AsNoTracking()
                .Where(c => courtCaseIds.Contains(c.LitigationCaseId))
                .ToListAsync(ct);

        var freshnessMap = courtCases.Count == 0
            ? new Dictionary<long, CaseFreshnessResult>()
            : await GetCaseFreshnessBatchAsync(requestId, courtCases.Select(c => c.LitigationCaseId).ToList(), ct);

        var matchedCourtCaseIds = new List<long>();
        var refreshedCount = 0;

        var dossierKeyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var lit in dossierLitigations)
        {
            if (TryGetCompleteDossierMatchKey(lit.CaseNumber, lit.CaseType, null, null, null, lit.Court, out var key))
            {
                dossierKeyCounts[key] = dossierKeyCounts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var lit in dossierLitigations)
        {
            if (!TryGetCompleteDossierMatchKey(lit.CaseNumber, lit.CaseType, null, null, null, lit.Court, out var litKey)
                || dossierKeyCounts.GetValueOrDefault(litKey) != 1)
            {
                continue;
            }

            var matched = MatchDossierLitigationToCourtCase(lit, courtCases);
            if (matched is null) continue;

            matchedCourtCaseIds.Add(matched.LitigationCaseId);
            if (freshnessMap.TryGetValue(matched.LitigationCaseId, out var freshness)
                && freshness.Label == FreshnessLabel.Updated)
            {
                refreshedCount++;
                ApplyCourtCaseRefreshToDossierRow(lit, matched);
            }
        }

        var latestObsUtc = await GetLatestObservationUtcForLegacyCasesAsync(matchedCourtCaseIds, ct);
        var asOfUtc = MaxUtc(baseAsOfUtc, latestObsUtc);
        var asOfIst = asOfUtc.HasValue ? Ist.Format(asOfUtc.Value, "dd-MMM-yyyy, HH:mm") : "Not yet retrieved";

        return new LitigationDataCurrencySummary(
            AsOfUtc: asOfUtc,
            AsOfIst: asOfIst,
            TotalCases: dossierLitigations.Count,
            RefreshedCasesCount: refreshedCount,
            UnrefreshedCasesCount: dossierLitigations.Count - refreshedCount,
            RefreshInProgress: refreshInProgress,
            InProgressNote: inProgressNote);
    }

    private async Task<(DateTime? CompletedRunUtc, bool RefreshInProgress, string? InProgressNote)> GetRefreshRunStateAsync(
        long requestId,
        CancellationToken ct)
    {
        var run = await db.Set<ModuleRefreshRun>().AsNoTracking()
            .Where(r => r.RequestId == requestId)
            .OrderByDescending(r => r.RunId)
            .FirstOrDefaultAsync(ct);

        var completedRunUtc = run?.FinishedUtc != null && string.IsNullOrWhiteSpace(run.FailedReason)
            ? run.FinishedUtc
            : await db.Set<ModuleRefreshRun>().AsNoTracking()
                .Where(r => r.RequestId == requestId && r.FinishedUtc != null && r.FailedReason == null)
                .OrderByDescending(r => r.FinishedUtc)
                .Select(r => r.FinishedUtc)
                .FirstOrDefaultAsync(ct);

        var refreshInProgress = run is not null && run.FinishedUtc is null && string.IsNullOrWhiteSpace(run.FailedReason);
        var inProgressNote = refreshInProgress
            ? (run!.StartedUtc != default
                ? $"Court refresh in progress (started {Ist.Format(run.StartedUtc, "dd-MMM-yyyy, HH:mm")})."
                : "Court refresh in progress.")
            : null;

        return (completedRunUtc, refreshInProgress, inProgressNote);
    }

    private async Task<DateTime?> GetLatestObservationUtcForLegacyCasesAsync(
        IReadOnlyList<long> legacyCaseIds,
        CancellationToken ct)
    {
        if (legacyCaseIds.Count == 0) return null;

        var distinctIds = legacyCaseIds.Distinct().ToList();
        var modIds = await db.Set<ModuleCase>().AsNoTracking()
            .Where(mc => mc.LegacyLitigationCaseId != null && distinctIds.Contains(mc.LegacyLitigationCaseId.Value))
            .Select(mc => mc.CaseId)
            .ToListAsync(ct);

        if (modIds.Count == 0) return null;

        var maxObs = await db.Set<ModuleCaseObservation>().AsNoTracking()
            .Where(o => modIds.Contains(o.CaseId) && o.Outcome != (int)ObservationOutcome.Error)
            .MaxAsync(o => (DateTime?)o.RetrievedUtc, ct);
        var maxLive = await db.Set<ModuleCaseLive>().AsNoTracking()
            .Where(l => modIds.Contains(l.CaseId))
            .MaxAsync(l => l.LastRetrievedUtc, ct);
        return MaxUtc(maxObs, maxLive);
    }

    internal static LitigationCase? MatchDossierLitigationToCourtCase(
        Litigation lit,
        IReadOnlyList<LitigationCase> courtCases)
    {
        if (courtCases.Count == 0
            || !TryGetCompleteDossierMatchKey(lit.CaseNumber, lit.CaseType, null, null, null, lit.Court, out var litKey))
        {
            return null;
        }

        LitigationCase? singleMatch = null;
        foreach (var c in courtCases)
        {
            if (!TryGetCompleteDossierMatchKey(c.CaseNumber, c.CaseType, c.CaseYear, c.FilingDate, c.Cnr, c.Court, out var cKey))
                continue;

            if (string.Equals(litKey, cKey, StringComparison.OrdinalIgnoreCase))
            {
                if (singleMatch is not null)
                {
                    // Multiple court cases share the same key -> ambiguous, refuse to match.
                    return null;
                }
                singleMatch = c;
            }
        }

        return singleMatch;
    }

    private static bool TryGetCompleteDossierMatchKey(
        string? caseNumber,
        string? caseType,
        string? caseYear,
        string? filingDate,
        string? cnr,
        string? court,
        out string key)
    {
        key = string.Empty;
        if (CaseValue.IsMissing(caseNumber) || CaseValue.IsMissing(court))
            return false;

        var parts = CaseNumberNormaliser.Parse(new CaseNumberInput(caseNumber, caseType, caseYear, filingDate, cnr));
        if (parts.Completeness != CaseNumberCompleteness.Complete || string.IsNullOrWhiteSpace(parts.Display))
            return false;

        var normCourt = NormalizeCaseLookupKey(court);
        var normDisplay = NormalizeCaseLookupKey(parts.Display);
        if (string.IsNullOrEmpty(normCourt) || string.IsNullOrEmpty(normDisplay))
            return false;

        key = $"{normCourt}|{normDisplay}";
        return true;
    }

    private static void ApplyCourtCaseRefreshToDossierRow(Litigation lit, LitigationCase courtCase)
    {
        if (!CaseValue.IsMissing(courtCase.CaseStatus))
            lit.CaseStatus = courtCase.CaseStatus;
        if (!CaseValue.IsMissing(courtCase.LastHearingDate)
            && DateOnly.TryParse(courtCase.LastHearingDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var lhd)
            && (lit.LastHearingDate is null || lhd >= lit.LastHearingDate.Value))
        {
            lit.LastHearingDate = lhd;
        }
    }

    private static string NormalizeCaseLookupKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ")
            .TrimEnd('.', ',', ')')
            .ToUpperInvariant();
    }

    private async Task<DateTime?> GetAuthoritativeSourceTimestampAsync(long requestId, CancellationToken ct)
    {
        var job = await db.LitigationSearchJobs.AsNoTracking()
            .Where(j => j.RequestId == requestId)
            .Select(j => new { j.LitigationSearchJobId, j.RawResponseHash })
            .FirstOrDefaultAsync(ct);

        if (job is not null && !string.IsNullOrWhiteSpace(job.RawResponseHash))
        {
            var currentSnapshotUtc = await db.LitigationReportSnapshots.AsNoTracking()
                .Where(s => s.LitigationSearchJobId == job.LitigationSearchJobId
                    && s.ReportHash == job.RawResponseHash
                    && s.Status == LitigationReportSnapshotStatus.Completed)
                .OrderByDescending(s => s.RetrievedUtc)
                .Select(s => (DateTime?)s.RetrievedUtc)
                .FirstOrDefaultAsync(ct);

            if (currentSnapshotUtc.HasValue)
                return currentSnapshotUtc.Value;
        }

        var latestSnapshotUtc = await db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => (job != null && s.LitigationSearchJobId == job.LitigationSearchJobId || s.RequestId == requestId)
                && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => s.RetrievedUtc)
            .Select(s => (DateTime?)s.RetrievedUtc)
            .FirstOrDefaultAsync(ct);

        if (latestSnapshotUtc.HasValue)
            return latestSnapshotUtc.Value;

        return await GetIngestionSourceTimestampAsync(requestId, ct);
    }

    private async Task<DateTime?> GetIngestionSourceTimestampAsync(long requestId, CancellationToken ct) =>
        await db.IngestionRuns.AsNoTracking()
            .Where(r => r.RequestId == requestId && r.CompletedDate != null)
            .OrderByDescending(r => r.CompletedDate)
            .Select(r => r.SourceSnapshotDate ?? r.CompletedDate)
            .FirstOrDefaultAsync(ct);

    private static DateTime? MaxUtc(DateTime? a, DateTime? b) =>
        (a, b) switch
        {
            (null, null) => null,
            ( { } x, null) => x,
            (null, { } y) => y,
            ( { } x, { } y) => x >= y ? x : y
        };

    private static IReadOnlyDictionary<string, DateTime>? ParseConfirmedEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var sources = LiveProjectionEngine.DeserializeSources(json);
        if (sources.Count == 0) return null;
        var dict = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, source) in sources)
        {
            if (source.ConfirmedEmpty)
            {
                dict[field] = source.WhenUtc;
            }
        }
        return dict.Count > 0 ? dict : null;
    }

    private static string? FormatTimeSpanEstimate(TimeSpan? estimate)
    {
        if (estimate is null) return null;
        var ts = estimate.Value;
        if (ts.TotalHours >= 1)
            return $"about {(int)ts.TotalHours} h {ts.Minutes} min left";
        if (ts.TotalMinutes >= 1)
            return $"about {(int)Math.Ceiling(ts.TotalMinutes)} min left";
        return "less than a minute left";
    }

    private static string FormatPhaseLabel(RefreshPhase phase) => phase switch
    {
        RefreshPhase.RefreshingCases => "Refreshing cases",
        RefreshPhase.FindingMissingCases => "Finding missing cases",
        RefreshPhase.FetchingOrders => "Fetching orders",
        RefreshPhase.ReanalysingChangedCases => "Re-analysing changed cases",
        _ => "Court update in progress"
    };
}

