using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Bridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.Reading;

/// <summary>Where a consumer reads from (plan section 12.6). <see cref="Legacy"/> serves today's tables; <see cref="Shadow"/> still serves legacy but compares the module's
/// answer beside it and logs every difference; <see cref="Module"/> serves the module's answer.</summary>
public enum LitigationReaderMode { Legacy, Shadow, Module }

/// <summary>Per-consumer switches, section <c>LitigationModule:Reader</c>. Every consumer starts on <see cref="LitigationReaderMode.Legacy"/>, so merging this changes nothing
/// until a consumer is promoted by configuration (and rolled back the same way).</summary>
public sealed class LitigationReaderOptions
{
    public const string SectionName = "LitigationModule:Reader";

    /// <summary>The request's case register: the set of cases listed, counted and summarised on the litigation tab and its case-list endpoint.</summary>
    public LitigationReaderMode CaseList { get; set; } = LitigationReaderMode.Legacy;

    /// <summary>The standalone page of one case: whether the case belongs to the request, and the "related cases" drawn from the same set.</summary>
    public LitigationReaderMode CasePage { get; set; } = LitigationReaderMode.Legacy;

    /// <summary>The charge-to-case comparison: which cases the charges are compared against.</summary>
    public LitigationReaderMode ChargeLinks { get; set; } = LitigationReaderMode.Legacy;
}

/// <summary>Which definition of "the request's cases" a read uses once the gate has decided.</summary>
public enum CaseSetSource { Legacy, Module }

/// <summary>The two definitions of the request's case set, each as one query so every read of the set (list, counts, court summary, order counts, charge links) can use the
/// same one. Legacy: the cases of the request's authoritative vendor snapshot. Module: the scope's retained set (any completed snapshot, minus the cases the scope hid).</summary>
public static class LitigationCaseSets
{
    public static IQueryable<long> Legacy(AppDbContext db, long snapshotId) =>
        db.LitigationCaseSourceReports.AsNoTracking()
            .Where(sr => sr.LitigationReportSnapshotId == snapshotId && sr.Case!.MergedIntoCaseId == null)
            .Select(sr => sr.LitigationCaseId).Distinct();

    public static async Task<IQueryable<long>> ForAsync(AppDbContext db, CaseSetSource source, long requestId, long snapshotId, CancellationToken ct) =>
        source == CaseSetSource.Module
            ? await new LitigationModuleReader(db).ScopeCaseIdsAsync(new ScopeRef(Store.ModuleScopeKind.Request, requestId), includeHidden: false, ct)
            : Legacy(db, snapshotId);
}

/// <summary>Decides, per request, which source serves the case register. Falls back to legacy whenever the module cannot be trusted (the bridge is behind or has a
/// dead-lettered event), and in shadow mode logs how the module's set differs from the legacy one.</summary>
public sealed class LitigationReaderGate(AppDbContext db, IOptions<LitigationReaderOptions> options, IOptions<LitigationBridgeOptions> bridge, ILogger<LitigationReaderGate>? logger = null)
{
    public Task<CaseSetSource> ResolveCaseListAsync(long requestId, CancellationToken ct) => ResolveAsync("case list", options.Value.CaseList, requestId, ct);

    public Task<CaseSetSource> ResolveChargeLinksAsync(long requestId, CancellationToken ct) => ResolveAsync("charge links", options.Value.ChargeLinks, requestId, ct);

    public Task<CaseSetSource> ResolveCasePageAsync(long requestId, CancellationToken ct) => ResolveAsync("case page", options.Value.CasePage, requestId, ct);

    private async Task<CaseSetSource> ResolveAsync(string consumer, LitigationReaderMode mode, long requestId, CancellationToken ct)
    {
        if (mode == LitigationReaderMode.Legacy) return CaseSetSource.Legacy;

        var readiness = await new LitigationBridgeProcessor(db, bridge.Value).GetReadinessAsync(requestId, ct);
        if (readiness != BridgeReadiness.Ready)
        {
            logger?.LogWarning("Litigation {Consumer} for request {RequestId} is served from legacy: the module is {Readiness}.", consumer, requestId, readiness);
            return CaseSetSource.Legacy;
        }
        if (mode == LitigationReaderMode.Module) return CaseSetSource.Module;

        await CompareAsync(requestId, ct);
        return CaseSetSource.Legacy;
    }

    /// <summary>Shadow comparison of the two case sets. Only a summary is logged (counts and the first ids), never case content.</summary>
    public async Task<CaseSetComparison?> CompareAsync(long requestId, CancellationToken ct)
    {
        var snapshotId = await LitigationData.LitigationSnapshotSelection.ForRequest(db, requestId).Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
        if (snapshotId is not { } sid) return null;
        var legacy = await LitigationCaseSets.Legacy(db, sid).ToListAsync(ct);
        var module = await (await LitigationCaseSets.ForAsync(db, CaseSetSource.Module, requestId, sid, ct)).ToListAsync(ct);
        var comparison = CaseSetComparison.Of(legacy, module);
        if (!comparison.IsIdentical)
            logger?.LogWarning("Litigation case list shadow difference for request {RequestId}: legacy {Legacy}, module {Module}, only in legacy [{OnlyLegacy}], only in module [{OnlyModule}].",
                requestId, legacy.Count, module.Count, string.Join(',', comparison.OnlyInLegacy.Take(20)), string.Join(',', comparison.OnlyInModule.Take(20)));
        return comparison;
    }
}

public sealed record CaseSetComparison(IReadOnlyList<long> OnlyInLegacy, IReadOnlyList<long> OnlyInModule)
{
    public bool IsIdentical => OnlyInLegacy.Count == 0 && OnlyInModule.Count == 0;

    public static CaseSetComparison Of(IEnumerable<long> legacy, IEnumerable<long> module)
    {
        var l = legacy.ToHashSet();
        var m = module.ToHashSet();
        return new CaseSetComparison(l.Except(m).OrderBy(x => x).ToList(), m.Except(l).OrderBy(x => x).ToList());
    }
}
