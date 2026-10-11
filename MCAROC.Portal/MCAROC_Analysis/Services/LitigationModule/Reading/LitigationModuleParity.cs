using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.RequestDetails;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Reading;

/// <param name="UnexplainedDifferences">Differences nothing accounts for. Promotion of a consumer needs this to be empty (plan section 12.6).</param>
/// <param name="ExplainedDifferences">Differences that are the module's deliberate improvement, each with its reason.</param>
public sealed record ParityReport(
    long RequestId, int LegacyCount, int ReaderCount, IReadOnlyList<string> UnexplainedDifferences, IReadOnlyList<string> ExplainedDifferences)
{
    public bool IsClean => UnexplainedDifferences.Count == 0;
}

/// <summary>The parity checker of plan section 12.6: runs today's case list and the module reader side by side for one request and reports every difference.
/// Read-only. It compares the case set, the consumer's default view, every field the reader returns (number, court, status, hearing dates, freshness) and the
/// aliases and overlays against what the scope stores. The reader is expected to differ from legacy in exactly two ways, both explained: it also lists cases a
/// later vendor snapshot dropped (flagged <c>InLatestFeed = false</c>), and it leaves out cases the scope has hidden.</summary>
public sealed class LitigationModuleParity(AppDbContext db)
{
    /// <summary>The legacy view of one case: the stored columns the reader must reproduce.</summary>
    public sealed record LegacyRow(long Id, string? CaseNumber, string? CaseType, string? CaseYear, string? FilingDate, string? Cnr, string? Court, string? CaseStatus,
        string? CaseStage, string? LastHearingDate, string? NextHearingDate);

    public async Task<ParityReport> CheckRequestAsync(long requestId, CancellationToken ct)
    {
        var legacy = await LegacyCasesAsync(requestId, ct);
        var reader = new LitigationModuleReader(db);
        var scope = new ScopeRef(ModuleScopeKind.Request, requestId);

        // The consumer's normal view (hidden left out) and the full retained set, both paged exactly as a consumer would read them.
        var visible = await ReadAllAsync(reader, scope, includeHidden: false, ct);
        var all = await ReadAllAsync(reader, scope, includeHidden: true, ct);

        var unexplained = new List<string>();
        var explained = new List<string>();
        var allById = all.ToDictionary(i => i.LegacyLitigationCaseId);
        var visibleIds = visible.Select(i => i.LegacyLitigationCaseId).ToHashSet();

        // 1. Case set. Legacy must be a subset of the reader's set; the extras must each be explained.
        foreach (var id in legacy.Keys.Where(id => !allById.ContainsKey(id)).OrderBy(id => id))
            unexplained.Add($"case {id} is in the legacy list but not in the reader's set");
        foreach (var item in all.Where(i => !legacy.ContainsKey(i.LegacyLitigationCaseId)).OrderBy(i => i.LegacyLitigationCaseId))
        {
            if (!item.InLatestFeed) explained.Add($"case {item.LegacyLitigationCaseId} is retained although the latest vendor snapshot no longer lists it");
            else unexplained.Add($"case {item.LegacyLitigationCaseId} is in the reader's set and the latest feed but not in the legacy list");
        }

        // 2. The normal consumer view: a case is visible exactly when it is not hidden in this scope.
        foreach (var item in all.OrderBy(i => i.LegacyLitigationCaseId))
        {
            var shown = visibleIds.Contains(item.LegacyLitigationCaseId);
            if (item.IsHidden && shown) unexplained.Add($"case {item.LegacyLitigationCaseId} is hidden but the default view still lists it");
            else if (!item.IsHidden && !shown) unexplained.Add($"case {item.LegacyLitigationCaseId} is not hidden but the default view omits it");
            else if (item.IsHidden) explained.Add($"case {item.LegacyLitigationCaseId} is hidden in this scope");
        }

        // 3. Fields, case by case, for every case both sides list: number, court, status, hearing dates, freshness.
        unexplained.AddRange(FieldDifferences(legacy, allById));

        // 4. Aliases and overlays: what the reader shows must be what the scope stores, and nothing stored may point outside the scope's retained set.
        var stored = await StoredOverlaysAsync(requestId, ct);
        unexplained.AddRange(OverlayDifferences(stored, allById));

        return new ParityReport(requestId, legacy.Count, all.Count, unexplained, explained);
    }

    /// <summary>Every field the reader returns, compared with the legacy row of the same case. Public so the comparison itself is tested with differing data.</summary>
    public static List<string> FieldDifferences(IReadOnlyDictionary<long, LegacyRow> legacy, IReadOnlyDictionary<long, ModuleCaseSummary> reader)
    {
        var differences = new List<string>();
        foreach (var (id, row) in legacy.OrderBy(kv => kv.Key))
        {
            if (!reader.TryGetValue(id, out var item)) continue;
            var expected = CaseNumberNormaliser.Parse(new CaseNumberInput(row.CaseNumber, row.CaseType, row.CaseYear, row.FilingDate, row.Cnr));
            Diff(differences, id, "case number", expected.Display, item.NumberDisplay);
            Diff(differences, id, "number completeness", expected.Completeness, item.NumberCompleteness);
            Diff(differences, id, "court", row.Court, item.Court);
            Diff(differences, id, "status", row.CaseStatus, item.CaseStatus);
            Diff(differences, id, "status bucket", LitigationCaseStatusClassifier.Classify(row.CaseStatus, row.CaseStage), item.StatusBucket);
            Diff(differences, id, "last hearing date", row.LastHearingDate, item.LastHearingDate);
            Diff(differences, id, "next hearing date", row.NextHearingDate, item.NextHearingDate);
            Diff(differences, id, "in the latest feed", true, item.InLatestFeed);   // every legacy case is, by definition, in the latest feed
        }
        return differences;
    }

    /// <summary>The scope's stored associations against what the reader shows: alias, hidden, excluded (and its reason), analyst note, pinned edits, report inclusion and
    /// redirect aliases. Nothing stored may point outside the retained set, and no alias may be shown that is not stored or shown twice.</summary>
    public static List<string> OverlayDifferences(IReadOnlyDictionary<long, ModuleScopeCase> stored, IReadOnlyDictionary<long, ModuleCaseSummary> reader)
    {
        var differences = new List<string>();
        foreach (var (legacyId, o) in stored.OrderBy(kv => kv.Key))
        {
            if (!reader.TryGetValue(legacyId, out var item))
            {
                differences.Add($"case {legacyId} has an overlay in this scope but is not in the scope's retained set");
                continue;
            }
            Diff(differences, legacyId, "alias", o.Alias, item.Alias);
            Diff(differences, legacyId, "hidden", o.IsHidden, item.IsHidden);
            Diff(differences, legacyId, "excluded", o.IsExcluded, item.IsExcluded);
            Diff(differences, legacyId, "exclusion reason", o.ExclusionReason, item.ExclusionReason);
            Diff(differences, legacyId, "analyst note", o.AnalystNote, item.AnalystNote);
            Diff(differences, legacyId, "pinned field edits", o.PinnedFieldsJson, item.PinnedFieldsJson);
            Diff(differences, legacyId, "report inclusion", o.IncludedInReport, item.IncludedInReport);
            Diff(differences, legacyId, "redirect aliases", o.RedirectAliasesJson, item.RedirectAliasesJson);
        }
        foreach (var item in reader.Values.Where(i => i.Alias is not null && !stored.ContainsKey(i.LegacyLitigationCaseId)).OrderBy(i => i.LegacyLitigationCaseId))
            differences.Add($"case {item.LegacyLitigationCaseId} shows alias {item.Alias} but the scope stores none");
        foreach (var dup in reader.Values.Where(i => i.Alias is not null).GroupBy(i => i.Alias).Where(g => g.Count() > 1))
            differences.Add($"alias {dup.Key} is shown for {dup.Count()} different cases");
        return differences;
    }

    private static void Diff<T>(List<string> unexplained, long id, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            unexplained.Add($"case {id}: {field} is \"{actual}\" in the reader and \"{expected}\" in legacy");
    }

    private static async Task<List<ModuleCaseSummary>> ReadAllAsync(LitigationModuleReader reader, ScopeRef scope, bool includeHidden, CancellationToken ct)
    {
        var items = new List<ModuleCaseSummary>();
        long? after = null;
        while (true)
        {
            var page = await reader.GetCasesAsync(scope, new CaseFilter(IncludeHidden: includeHidden), new PageRequest(after, PageRequest.MaxSize), ct);
            items.AddRange(page.Items);
            if (page.NextAfterCaseId is not { } next) return items;
            after = next;
        }
    }

    private async Task<Dictionary<long, ModuleScopeCase>> StoredOverlaysAsync(long requestId, CancellationToken ct)
    {
        var rows = await (from s in db.Set<ModuleScope>().AsNoTracking()
            where s.Kind == ModuleScopeKind.Request && s.ExternalId == requestId
            join sc in db.Set<ModuleScopeCase>().AsNoTracking() on s.ScopeId equals sc.ScopeId
            join mc in db.Set<ModuleCase>().AsNoTracking() on sc.CaseId equals mc.CaseId
            where mc.LegacyLitigationCaseId != null
            select new { Legacy = mc.LegacyLitigationCaseId!.Value, Link = sc }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Legacy, r => r.Link);
    }

    private async Task<Dictionary<long, LegacyRow>> LegacyCasesAsync(long requestId, CancellationToken ct)
    {
        var snapshotId = await LitigationSnapshotSelection.ForRequest(db, requestId).Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
        if (snapshotId is not { } sid) return [];
        var query = await new LitigationCaseListQuery(db).FilterAsync(sid, null, null, null, null, requestId, ct);
        var rows = await query.Select(c => new LegacyRow(c.LitigationCaseId, c.CaseNumber, c.CaseType, c.CaseYear, c.FilingDate, c.Cnr, c.Court, c.CaseStatus, c.CaseStage,
            c.LastHearingDate, c.NextHearingDate)).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id);
    }
}
