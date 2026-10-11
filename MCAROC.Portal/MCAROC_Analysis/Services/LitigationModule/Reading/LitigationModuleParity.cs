using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
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
/// Read-only. The reader is expected to differ from legacy in exactly two ways, both explained: it also lists cases a later vendor snapshot dropped (flagged
/// <c>InLatestFeed = false</c>), and it leaves out cases the scope has hidden.</summary>
public sealed class LitigationModuleParity(AppDbContext db)
{
    public async Task<ParityReport> CheckRequestAsync(long requestId, CancellationToken ct)
    {
        var legacy = await LegacyCasesAsync(requestId, ct);
        var reader = new LitigationModuleReader(db);
        var scope = new ScopeRef(ModuleScopeKind.Request, requestId);

        var items = new List<ModuleCaseSummary>();
        long? after = null;
        while (true)
        {
            var page = await reader.GetCasesAsync(scope, new CaseFilter(IncludeHidden: true), new PageRequest(after, PageRequest.MaxSize), ct);
            items.AddRange(page.Items);
            if (page.NextAfterCaseId is not { } next) break;
            after = next;
        }

        var unexplained = new List<string>();
        var explained = new List<string>();
        var readerById = items.ToDictionary(i => i.LegacyLitigationCaseId);

        foreach (var id in legacy.Keys.Where(id => !readerById.ContainsKey(id)).OrderBy(id => id))
            unexplained.Add($"case {id} is in the legacy list but not in the reader's set");
        foreach (var item in items.Where(i => !legacy.ContainsKey(i.LegacyLitigationCaseId)).OrderBy(i => i.LegacyLitigationCaseId))
        {
            if (!item.InLatestFeed) explained.Add($"case {item.LegacyLitigationCaseId} is retained although the latest vendor snapshot no longer lists it");
            else unexplained.Add($"case {item.LegacyLitigationCaseId} is in the reader's set and the latest feed but not in the legacy list");
        }
        foreach (var item in items.Where(i => i.IsHidden).OrderBy(i => i.LegacyLitigationCaseId))
            explained.Add($"case {item.LegacyLitigationCaseId} is hidden in this scope");

        // On the cases both sides list, the status the reader derives must be the one legacy derives.
        foreach (var (id, legacyBucket) in legacy.OrderBy(kv => kv.Key))
            if (readerById.TryGetValue(id, out var item) && item.StatusBucket != legacyBucket)
                unexplained.Add($"case {id}: status is {item.StatusBucket} in the reader and {legacyBucket} in legacy");

        return new ParityReport(requestId, legacy.Count, items.Count, unexplained, explained);
    }

    private async Task<Dictionary<long, LitigationCaseStatusBucket>> LegacyCasesAsync(long requestId, CancellationToken ct)
    {
        var snapshotId = await LitigationSnapshotSelection.ForRequest(db, requestId).Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
        if (snapshotId is not { } sid) return [];
        var query = await new LitigationCaseListQuery(db).FilterAsync(sid, null, null, null, null, requestId, ct);
        var rows = await query.Select(c => new { c.LitigationCaseId, c.CaseStatus, c.CaseStage }).ToListAsync(ct);
        return rows.ToDictionary(r => r.LitigationCaseId, r => LitigationCaseStatusClassifier.Classify(r.CaseStatus, r.CaseStage));
    }
}
