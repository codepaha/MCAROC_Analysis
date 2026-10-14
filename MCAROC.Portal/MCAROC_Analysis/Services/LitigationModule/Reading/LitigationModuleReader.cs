using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Reading;

/// <summary>Which consumer instance is asking. Only <see cref="ModuleScopeKind.Request"/> is served from today's tables so far; an assignment is served once
/// the pre-login records are bridged (plan section 12.5).</summary>
public sealed record ScopeRef(ModuleScopeKind Kind, long ExternalId);

/// <param name="Status">Pending, Disposed or Unknown (<see cref="LitigationCaseStatusBucket"/>); null means no filter.</param>
/// <param name="IncludeHidden">Hidden cases are left out unless asked for (the owner's "show hidden" view).</param>
public sealed record CaseFilter(LitigationCaseStatusBucket? Status = null, string? Court = null, string? Search = null, bool IncludeHidden = false);

/// <summary>A keyset page: <paramref name="AfterCaseId"/> is the last id of the previous page. Offsets are not used, so a 15,000-case scope costs the same per page.</summary>
public sealed record PageRequest(long? AfterCaseId = null, int Size = 100)
{
    public const int MaxSize = 500;
    public int ClampedSize => Math.Clamp(Size, 1, MaxSize);
}

/// <summary>One case of a scope's view. <see cref="LegacyLitigationCaseId"/> is the stable key today; <see cref="CaseId"/> and <see cref="Alias"/> are filled once the case is in the module.</summary>
public sealed record ModuleCaseSummary(
    long LegacyLitigationCaseId, long? CaseId, string? Alias, string NumberDisplay, CaseNumberCompleteness NumberCompleteness, string? Court, string? CaseStatus,
    LitigationCaseStatusBucket StatusBucket, string? LastHearingDate, string? NextHearingDate, bool InLatestFeed, bool IsHidden, bool IsExcluded,
    string? ExclusionReason = null, string? AnalystNote = null, string? PinnedFieldsJson = null, bool IncludedInReport = true, string? RedirectAliasesJson = null);

public sealed record ModuleCaseCounts(int Total, int Pending, int Disposed, int Unknown, int NotInLatestFeed, int Hidden);

public sealed record ModuleCasePage(IReadOnlyList<ModuleCaseSummary> Items, long? NextAfterCaseId, ModuleCaseCounts Counts);

public sealed record ModuleOrderSummary(long LitigationCaseOrderId, string? OrderDate, string? OrderType);

/// <summary>The read side of the litigation module (plan section 12.3): a facade over today's tables. It returns the scope's <b>retained set</b> (plan section 4.2b):
/// every case linked to <b>any</b> completed vendor snapshot of the request, not only the latest, minus the cases the scope has hidden. A case the latest feed no longer
/// lists stays, flagged <see cref="ModuleCaseSummary.InLatestFeed"/> = false. It never writes. Nothing calls it yet.</summary>
public sealed class LitigationModuleReader(AppDbContext db)
{
    /// <summary>The scope's retained cases, before the overlay and filters: the single definition of "which cases belong to this request".</summary>
    private IQueryable<LitigationCase> RetainedSet(long requestId) =>
        db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId && c.MergedIntoCaseId == null &&
            c.SourceReports.Any(sr => sr.ReportSnapshot!.Status == LitigationReportSnapshotStatus.Completed && sr.ReportSnapshot.RequestId == requestId))
            .Where(LitigationData.Attribution.LitigationAttribution.IsShown);   // #489: a case that is not the company's is not in its set

    public async Task<ModuleCasePage> GetCasesAsync(ScopeRef scope, CaseFilter filter, PageRequest page, CancellationToken ct)
    {
        if (scope.Kind == ModuleScopeKind.Assignment)
        {
            return await GetAssignmentCasesAsync(scope.ExternalId, filter, page, ct);
        }

        var requestId = RequireRequest(scope);
        var scopeId = await db.Set<ModuleScope>().AsNoTracking().Where(s => s.Kind == scope.Kind && s.ExternalId == scope.ExternalId)
            .Select(s => (long?)s.ScopeId).FirstOrDefaultAsync(ct);
        var latestFeed = LatestFeedCaseIds(requestId);
        var hiddenIds = HiddenLegacyIds(scopeId);

        var all = RetainedSet(requestId);
        var visible = filter.IncludeHidden ? all : all.Where(c => !hiddenIds.Contains(c.LitigationCaseId));

        // Counts follow the court and search filters but not the status itself, so each tab shows what choosing it would list (as the legacy register does).
        var unsegmented = ApplyCourtAndSearch(visible, filter);
        var counts = new ModuleCaseCounts(
            await unsegmented.CountAsync(ct),
            await unsegmented.CountAsync(LitigationCaseStatusClassifier.IsPendingExpr, ct),
            await unsegmented.CountAsync(LitigationCaseStatusClassifier.IsDisposedExpr, ct),
            await unsegmented.CountAsync(LitigationCaseStatusClassifier.IsUnknownExpr, ct),
            await unsegmented.CountAsync(c => !latestFeed.Contains(c.LitigationCaseId), ct),
            await all.CountAsync(c => hiddenIds.Contains(c.LitigationCaseId), ct));

        var query = ApplyStatus(unsegmented, filter.Status);
        if (page.AfterCaseId is { } after) query = query.Where(c => c.LitigationCaseId > after);
        var size = page.ClampedSize;
        var rows = await query.OrderBy(c => c.LitigationCaseId).Take(size + 1)
            .Select(c => new
            {
                c.LitigationCaseId, c.CaseNumber, c.CaseType, c.CaseYear, c.FilingDate, c.Cnr, c.Court, c.CaseStatus, c.CaseStage, c.LastHearingDate, c.NextHearingDate,
                InLatestFeed = latestFeed.Contains(c.LitigationCaseId)
            }).ToListAsync(ct);

        var pageRows = rows.Take(size).ToList();
        var ids = pageRows.Select(r => r.LitigationCaseId).ToArray();
        var overlays = scopeId is null ? [] : await (from sc in db.Set<ModuleScopeCase>().AsNoTracking()
            join mc in db.Set<ModuleCase>().AsNoTracking() on sc.CaseId equals mc.CaseId
            where sc.ScopeId == scopeId && mc.LegacyLitigationCaseId != null && ids.Contains(mc.LegacyLitigationCaseId.Value)
            select new { Legacy = mc.LegacyLitigationCaseId!.Value, mc.CaseId, sc.Alias, sc.IsHidden, sc.IsExcluded, sc.ExclusionReason, sc.AnalystNote, sc.PinnedFieldsJson, sc.IncludedInReport, sc.RedirectAliasesJson }).ToDictionaryAsync(x => x.Legacy, ct);

        var items = pageRows.Select(r =>
        {
            var parts = CaseNumberNormaliser.Parse(new CaseNumberInput(r.CaseNumber, r.CaseType, r.CaseYear, r.FilingDate, r.Cnr));
            overlays.TryGetValue(r.LitigationCaseId, out var o);
            return new ModuleCaseSummary(r.LitigationCaseId, o?.CaseId, o?.Alias, parts.Display, parts.Completeness, r.Court, r.CaseStatus,
                LitigationCaseStatusClassifier.Classify(r.CaseStatus, r.CaseStage), r.LastHearingDate, r.NextHearingDate, r.InLatestFeed,
                o?.IsHidden ?? false, o?.IsExcluded ?? false, o?.ExclusionReason, o?.AnalystNote, o?.PinnedFieldsJson, o?.IncludedInReport ?? true, o?.RedirectAliasesJson);
        }).ToList();

        return new ModuleCasePage(items, rows.Count > size ? pageRows[^1].LitigationCaseId : null, counts);
    }

    /// <summary>One case of the scope, or null when it is not in the scope's retained set.</summary>
    public async Task<ModuleCaseSummary?> GetCaseAsync(ScopeRef scope, long legacyLitigationCaseId, CancellationToken ct)
    {
        if (scope.Kind == ModuleScopeKind.Assignment)
        {
            var p = await GetAssignmentCasesAsync(scope.ExternalId, new CaseFilter(IncludeHidden: true), new PageRequest(legacyLitigationCaseId - 1, 1), ct);
            return p.Items.FirstOrDefault(i => i.LegacyLitigationCaseId == legacyLitigationCaseId);
        }

        var requestId = RequireRequest(scope);
        if (!await RetainedSet(requestId).AnyAsync(c => c.LitigationCaseId == legacyLitigationCaseId, ct)) return null;
        var page = await GetCasesAsync(scope, new CaseFilter(IncludeHidden: true), new PageRequest(legacyLitigationCaseId - 1, 1), ct);
        return page.Items.FirstOrDefault(i => i.LegacyLitigationCaseId == legacyLitigationCaseId);
    }

    /// <summary>The orders of a case that belongs to the scope; empty for a case that does not.</summary>
    public async Task<IReadOnlyList<ModuleOrderSummary>> GetOrdersAsync(ScopeRef scope, long legacyLitigationCaseId, CancellationToken ct)
    {
        var requestId = RequireRequest(scope);
        if (!await RetainedSet(requestId).AnyAsync(c => c.LitigationCaseId == legacyLitigationCaseId, ct)) return [];
        return await db.LitigationCaseOrders.AsNoTracking().Where(o => o.LitigationCaseId == legacyLitigationCaseId)
            .OrderBy(o => o.LitigationCaseOrderId).Select(o => new ModuleOrderSummary(o.LitigationCaseOrderId, o.OrderDate, o.OrderType)).ToListAsync(ct);
    }

    /// <summary>The ids of the scope's retained cases as one query (hidden ones left out unless asked for), for consumers that build their own projections over the set.</summary>
    public async Task<IQueryable<long>> ScopeCaseIdsAsync(ScopeRef scope, bool includeHidden, CancellationToken ct)
    {
        if (scope.Kind == ModuleScopeKind.Assignment)
        {
            var assignmentScopeId = await db.Set<ModuleScope>().AsNoTracking()
                .Where(s => s.Kind == scope.Kind && s.ExternalId == scope.ExternalId)
                .Select(s => (long?)s.ScopeId).FirstOrDefaultAsync(ct);
            var q = db.Set<ModuleScopeCase>().AsNoTracking()
                .Where(sc => sc.ScopeId == assignmentScopeId);
            if (!includeHidden) q = q.Where(sc => !sc.IsHidden);
            return q.Select(sc => sc.CaseId);
        }

        var requestId = RequireRequest(scope);
        var scopeId = await db.Set<ModuleScope>().AsNoTracking().Where(s => s.Kind == scope.Kind && s.ExternalId == scope.ExternalId)
            .Select(s => (long?)s.ScopeId).FirstOrDefaultAsync(ct);
        var all = RetainedSet(requestId);
        if (includeHidden) return all.Select(c => c.LitigationCaseId);
        var hidden = HiddenLegacyIds(scopeId);
        return all.Where(c => !hidden.Contains(c.LitigationCaseId)).Select(c => c.LitigationCaseId);
    }

    private async Task<ModuleCasePage> GetAssignmentCasesAsync(long jobId, CaseFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = await db.Set<ModuleScope>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == jobId, ct);
        if (scope is null)
        {
            return new ModuleCasePage([], null, new ModuleCaseCounts(0, 0, 0, 0, 0, 0));
        }

        var job = await db.PreLoginReportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.PreLoginReportJobId == jobId, ct);

        InstaReportData? data = null;
        if (!string.IsNullOrWhiteSpace(job?.DataJson))
        {
            try { data = System.Text.Json.JsonSerializer.Deserialize<InstaReportData>(job.DataJson); }
            catch { }
        }

        var legacyCaseMap = (data?.LegalCases?.Cases ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.CaseRowId))
            .ToDictionary(c => c.CaseRowId, StringComparer.OrdinalIgnoreCase);

        var scopeCases = await (from sc in db.Set<ModuleScopeCase>().AsNoTracking()
            join mc in db.Set<ModuleCase>().AsNoTracking() on sc.CaseId equals mc.CaseId
            where sc.ScopeId == scope.ScopeId
            select new { sc, mc }).ToListAsync(ct);

        var allItems = new List<ModuleCaseSummary>();
        foreach (var item in scopeCases)
        {
            var sc = item.sc;
            var mc = item.mc;
            legacyCaseMap.TryGetValue(sc.Alias, out var rec);

            var caseNumber = rec?.CaseNo ?? "-";
            var caseType = rec?.CaseType ?? "-";
            var caseYear = rec?.CaseYear ?? "-";
            var filingDate = rec?.DateOfFiling ?? "-";
            var cnr = rec?.CnrNumber ?? "-";
            var court = rec?.Court ?? "-";
            var status = rec?.Status ?? "-";
            var stage = rec?.CaseStage ?? "-";
            var lastHearing = rec?.DateOfHearing;
            var nextHearing = rec?.DateOfHearing;

            if (!string.IsNullOrWhiteSpace(sc.PinnedFieldsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(sc.PinnedFieldsJson);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("Court", out var pCourt) && pCourt.ValueKind == System.Text.Json.JsonValueKind.String)
                            court = pCourt.GetString() ?? court;
                        if (doc.RootElement.TryGetProperty("CaseStatus", out var pStatus) && pStatus.ValueKind == System.Text.Json.JsonValueKind.String)
                            status = pStatus.GetString() ?? status;
                        if (doc.RootElement.TryGetProperty("CaseStage", out var pStage) && pStage.ValueKind == System.Text.Json.JsonValueKind.String)
                            stage = pStage.GetString() ?? stage;
                        if (doc.RootElement.TryGetProperty("CaseNumber", out var pNumber) && pNumber.ValueKind == System.Text.Json.JsonValueKind.String)
                            caseNumber = pNumber.GetString() ?? caseNumber;
                    }
                }
                catch { }
            }

            var parts = CaseNumberNormaliser.Parse(new CaseNumberInput(caseNumber, caseType, caseYear, filingDate, cnr));
            var statusBucket = LitigationCaseStatusClassifier.Classify(status, stage);

            allItems.Add(new ModuleCaseSummary(
                LegacyLitigationCaseId: mc.CaseId,
                CaseId: mc.CaseId,
                Alias: sc.Alias,
                NumberDisplay: parts.Display,
                NumberCompleteness: parts.Completeness,
                Court: court,
                CaseStatus: status,
                StatusBucket: statusBucket,
                LastHearingDate: lastHearing,
                NextHearingDate: nextHearing,
                InLatestFeed: sc.FeedLastSeenUtc != null && (!sc.AnalystNote?.Contains("(not in latest feed)", StringComparison.OrdinalIgnoreCase) ?? true),
                IsHidden: sc.IsHidden,
                IsExcluded: sc.IsExcluded,
                ExclusionReason: sc.ExclusionReason,
                AnalystNote: sc.AnalystNote,
                PinnedFieldsJson: sc.PinnedFieldsJson,
                IncludedInReport: sc.IncludedInReport,
                RedirectAliasesJson: sc.RedirectAliasesJson
            ));
        }

        var visible = filter.IncludeHidden ? allItems : allItems.Where(i => !i.IsHidden).ToList();

        if (!string.IsNullOrWhiteSpace(filter.Court))
        {
            var courtName = filter.Court.Trim();
            visible = courtName.Equals("Unspecified Court", StringComparison.OrdinalIgnoreCase)
                ? visible.Where(c => string.IsNullOrWhiteSpace(c.Court) || c.Court == "-").ToList()
                : visible.Where(c => string.Equals(c.Court?.Trim(), courtName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            visible = visible.Where(c => (c.NumberDisplay?.ToLowerInvariant().Contains(term) ?? false) ||
                                         (c.Court?.ToLowerInvariant().Contains(term) ?? false) ||
                                         (c.Alias?.ToLowerInvariant().Contains(term) ?? false)).ToList();
        }

        var counts = new ModuleCaseCounts(
            visible.Count,
            visible.Count(c => c.StatusBucket == LitigationCaseStatusBucket.Pending),
            visible.Count(c => c.StatusBucket == LitigationCaseStatusBucket.Disposed),
            visible.Count(c => c.StatusBucket == LitigationCaseStatusBucket.Unknown),
            visible.Count(c => !c.InLatestFeed),
            allItems.Count(c => c.IsHidden)
        );

        if (filter.Status.HasValue)
        {
            visible = visible.Where(c => c.StatusBucket == filter.Status.Value).ToList();
        }

        visible = visible.OrderBy(c => c.CaseId).ToList();

        if (page.AfterCaseId is { } after)
        {
            visible = visible.Where(c => c.LegacyLitigationCaseId > after).ToList();
        }

        var size = page.ClampedSize;
        var pagedItems = visible.Take(size).ToList();
        long? nextAfter = visible.Count > size ? pagedItems[^1].LegacyLitigationCaseId : null;

        return new ModuleCasePage(pagedItems, nextAfter, counts);
    }

    /// <summary>The ids of the cases listed by the request's authoritative (latest) vendor snapshot.</summary>
    private IQueryable<long> LatestFeedCaseIds(long requestId)
    {
        var snapshotId = LitigationSnapshotSelection.ForRequest(db, requestId).Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefault();
        return db.LitigationCaseSourceReports.Where(s => s.LitigationReportSnapshotId == snapshotId).Select(s => s.LitigationCaseId);
    }

    private IQueryable<long> HiddenLegacyIds(long? scopeId) =>
        from sc in db.Set<ModuleScopeCase>().AsNoTracking()
        join mc in db.Set<ModuleCase>().AsNoTracking() on sc.CaseId equals mc.CaseId
        where scopeId != null && sc.ScopeId == scopeId && sc.IsHidden && mc.LegacyLitigationCaseId != null
        select mc.LegacyLitigationCaseId!.Value;

    private static IQueryable<LitigationCase> ApplyCourtAndSearch(IQueryable<LitigationCase> query, CaseFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Court))
        {
            var name = filter.Court.Trim();
            query = name.Equals("Unspecified Court", StringComparison.OrdinalIgnoreCase)
                ? query.Where(c => c.Court == null || c.Court.Trim() == "")
                : query.Where(c => c.Court != null && c.Court.Trim() == name);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(c => (c.CaseNumber != null && c.CaseNumber.ToLower().Contains(term))
                || (c.Cnr != null && c.Cnr.ToLower().Contains(term))
                || (c.Court != null && c.Court.ToLower().Contains(term))
                || (c.PetitionersJson != null && c.PetitionersJson.ToLower().Contains(term))
                || (c.RespondentsJson != null && c.RespondentsJson.ToLower().Contains(term)));
        }
        return query;
    }

    private static IQueryable<LitigationCase> ApplyStatus(IQueryable<LitigationCase> query, LitigationCaseStatusBucket? status) => status switch
    {
        LitigationCaseStatusBucket.Pending => query.Where(LitigationCaseStatusClassifier.IsPendingExpr),
        LitigationCaseStatusBucket.Disposed => query.Where(LitigationCaseStatusClassifier.IsDisposedExpr),
        LitigationCaseStatusBucket.Unknown => query.Where(LitigationCaseStatusClassifier.IsUnknownExpr),
        _ => query
    };

    private static long RequireRequest(ScopeRef scope) =>
        scope.Kind == ModuleScopeKind.Request ? scope.ExternalId
            : throw new NotSupportedException("Assignment scopes are served once the pre-login records are bridged into the module.");
}
