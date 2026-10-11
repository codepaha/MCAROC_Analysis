using System.Globalization;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Merging;

public sealed record CaseMergeResult(bool Performed, MergeRefusal Refusal, string Reason, long? MergeId, long? SurvivorCaseId, long? LoserCaseId, int ChangedValues);

/// <summary>A value a reversal left alone because something changed it after the merge (a live publication, a vendor rerun, a new row).</summary>
public sealed record ReversalConflict(string Table, long RowId, string Column, string? ExpectedAfterMerge, string? Current, string Why);

public sealed record ReversalReport(
    bool Reversed, bool AlreadyReversed, int Restored, IReadOnlyList<ReversalConflict> Conflicts, IReadOnlyList<string> PostMergeRows);

/// <summary>Executes and reverses soft-merges of duplicate cases (plan section 4.6). A merge changes <b>identifiers and flags only</b>, journals every changed value in the same
/// transaction, never deletes a row, and re-evaluates eligibility under the per-case locks so two merges or a merge and a publication cannot interleave. Reversal is conditional
/// per value: it restores a value only if it still equals what the merge wrote.
/// <para>This step covers the case row, its orders and its source-report links. Documents, chunks, classifications, analyses and scope overlays follow in later steps.</para></summary>
public sealed class CaseMergeService(AppDbContext db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public const string TCases = "LitigationCases", TOrders = "LitigationCaseOrders", TSourceReports = "LitigationCaseSourceReports";
    public const string CMergedInto = "MergedIntoCaseId", CCaseId = "LitigationCaseId", CCollapsedInto = "CollapsedIntoOrderId";

    public async Task<CaseMergeResult> MergeAsync(long requestId, long caseA, long caseB, CancellationToken ct)
    {
        if (caseA == caseB) return Refused(MergeRefusal.SameCase, "a case cannot be merged into itself");
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // Locks in id order, so two merges (or a merge and a publication) over the same cases can never deadlock.
        foreach (var id in new[] { caseA, caseB }.OrderBy(i => i)) await CaseLock.AcquireAsync(db, id, ct);

        var a = await LoadCandidateAsync(requestId, caseA, ct);
        var b = await LoadCandidateAsync(requestId, caseB, ct);
        if (a is null || b is null) return Refused(MergeRefusal.DifferentRequest, "a case does not exist in this request");
        var decision = MergeRules.Evaluate(a, b);
        if (!decision.Allowed) return new CaseMergeResult(false, decision.Refusal, decision.Reason, null, null, null, 0);

        var survivorId = decision.SurvivorCaseId!.Value;
        var loserId = decision.LoserCaseId!.Value;
        var now = _time.GetUtcNow().UtcDateTime;
        var merge = new ModuleCaseMerge { RequestId = requestId, SurvivorCaseId = survivorId, LoserCaseId = loserId, Reason = decision.Reason, MergedUtc = now };
        db.Set<ModuleCaseMerge>().Add(merge);
        await db.SaveChangesAsync(ct);

        var journal = new List<ModuleCaseMergeChange>();
        void Record(string table, long rowId, string column, long? before, long? after) =>
            journal.Add(new ModuleCaseMergeChange { MergeId = merge.MergeId, Table = table, RowId = rowId, Column = column, BeforeValue = Text(before), AfterValue = Text(after) });

        // Source-report links: re-point those the survivor does not already have for the same snapshot; the others stay on the loser (the survivor already carries that membership).
        var survivorSnapshots = (await db.LitigationCaseSourceReports.Where(s => s.LitigationCaseId == survivorId).Select(s => s.LitigationReportSnapshotId).ToListAsync(ct)).ToHashSet();
        foreach (var link in await db.LitigationCaseSourceReports.Where(s => s.LitigationCaseId == loserId).ToListAsync(ct))
        {
            if (survivorSnapshots.Contains(link.LitigationReportSnapshotId)) continue;
            Record(TSourceReports, link.LitigationCaseSourceReportId, CCaseId, link.LitigationCaseId, survivorId);
            link.LitigationCaseId = survivorId;
        }

        // Orders: an order the survivor already holds (same url, date and type) is collapsed into it - flagged, not re-pointed; any other is re-pointed.
        var survivorOrders = await db.LitigationCaseOrders.Where(o => o.LitigationCaseId == survivorId).ToListAsync(ct);
        foreach (var order in await db.LitigationCaseOrders.Where(o => o.LitigationCaseId == loserId).ToListAsync(ct))
        {
            var same = survivorOrders.FirstOrDefault(o => OrderKey(o) == OrderKey(order));
            if (same is not null)
            {
                Record(TOrders, order.LitigationCaseOrderId, CCollapsedInto, order.CollapsedIntoOrderId, same.LitigationCaseOrderId);
                order.CollapsedIntoOrderId = same.LitigationCaseOrderId;
            }
            else
            {
                Record(TOrders, order.LitigationCaseOrderId, CCaseId, order.LitigationCaseId, survivorId);
                order.LitigationCaseId = survivorId;
            }
        }

        // The loser row stays, flagged. Nothing is deleted.
        var loser = await db.LitigationCases.FirstAsync(c => c.LitigationCaseId == loserId, ct);
        Record(TCases, loserId, CMergedInto, loser.MergedIntoCaseId, survivorId);
        loser.MergedIntoCaseId = survivorId;

        db.Set<ModuleCaseMergeChange>().AddRange(journal);
        await db.SaveChangesAsync(ct);
        await RequestLiveData.BumpAsync(db, requestId, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new CaseMergeResult(true, MergeRefusal.None, decision.Reason, merge.MergeId, survivorId, loserId, journal.Count);
    }

    /// <summary>Reverses a merge value by value. A value is restored only if it still equals what the merge wrote; anything changed since is kept and reported. Rows created after the merge
    /// (new orders on the survivor) are listed for review and left where they are. A second reversal does nothing.</summary>
    public async Task<ReversalReport> ReverseAsync(long mergeId, CancellationToken ct)
    {
        var merge = await db.Set<ModuleCaseMerge>().AsNoTracking().FirstOrDefaultAsync(m => m.MergeId == mergeId, ct);
        if (merge is null) return new ReversalReport(false, false, 0, [], []);
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        foreach (var id in new[] { merge.SurvivorCaseId, merge.LoserCaseId }.OrderBy(i => i)) await CaseLock.AcquireAsync(db, id, ct);

        var current = await db.Set<ModuleCaseMerge>().FirstAsync(m => m.MergeId == mergeId, ct);
        if (current.ReversedUtc is not null) return new ReversalReport(false, true, 0, [], []);

        var changes = await db.Set<ModuleCaseMergeChange>().AsNoTracking().Where(c => c.MergeId == mergeId).OrderByDescending(c => c.ChangeId).ToListAsync(ct);
        var conflicts = new List<ReversalConflict>();
        var restored = 0;
        foreach (var change in changes)
        {
            var (ok, now, why) = await TryRestoreAsync(change, ct);
            if (ok) { restored++; continue; }
            conflicts.Add(new ReversalConflict(change.Table, change.RowId, change.Column, change.AfterValue, now, why));
        }

        // Rows that exist now on the survivor but were not part of the merge: new orders since. They stay; the owner reviews them.
        var journaledOrderIds = changes.Where(c => c.Table == TOrders).Select(c => c.RowId).ToHashSet();
        var post = (await db.LitigationCaseOrders.AsNoTracking().Where(o => o.LitigationCaseId == merge.SurvivorCaseId && o.CreatedUtc > merge.MergedUtc).ToListAsync(ct))
            .Where(o => !journaledOrderIds.Contains(o.LitigationCaseOrderId))
            .Select(o => $"order {o.LitigationCaseOrderId} ({o.OrderType} {o.OrderDate}) on case {merge.SurvivorCaseId} was added after the merge").ToList();

        current.ReversedUtc = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await RequestLiveData.BumpAsync(db, merge.RequestId, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new ReversalReport(true, false, restored, conflicts, post);
    }

    private async Task<(bool Restored, string? Current, string Why)> TryRestoreAsync(ModuleCaseMergeChange change, CancellationToken ct)
    {
        var after = Parse(change.AfterValue);
        var before = Parse(change.BeforeValue);
        switch (change.Table, change.Column)
        {
            case (TCases, CMergedInto):
            {
                var row = await db.LitigationCases.FirstOrDefaultAsync(c => c.LitigationCaseId == change.RowId, ct);
                if (row is null) return (false, null, "the row no longer exists");
                if (row.MergedIntoCaseId != after) return (false, Text(row.MergedIntoCaseId), "changed after the merge");
                row.MergedIntoCaseId = before;
                return (true, null, "");
            }
            case (TOrders, CCollapsedInto):
            {
                var row = await db.LitigationCaseOrders.FirstOrDefaultAsync(o => o.LitigationCaseOrderId == change.RowId, ct);
                if (row is null) return (false, null, "the row no longer exists");
                if (row.CollapsedIntoOrderId != after) return (false, Text(row.CollapsedIntoOrderId), "changed after the merge");
                row.CollapsedIntoOrderId = before;
                return (true, null, "");
            }
            case (TOrders, CCaseId):
            {
                var row = await db.LitigationCaseOrders.FirstOrDefaultAsync(o => o.LitigationCaseOrderId == change.RowId, ct);
                if (row is null) return (false, null, "the row no longer exists");
                if (row.LitigationCaseId != after) return (false, Text(row.LitigationCaseId), "changed after the merge");
                if (before is { } target && await db.LitigationCaseOrders.AnyAsync(o => o.LitigationCaseId == target && o.LitigationCaseOrderId != row.LitigationCaseOrderId
                        && o.PdfUrl == row.PdfUrl && o.OrderDate == row.OrderDate && o.OrderType == row.OrderType, ct))
                    return (false, Text(row.LitigationCaseId), "the original case now holds an identical order");
                row.LitigationCaseId = before ?? row.LitigationCaseId;
                return (true, null, "");
            }
            case (TSourceReports, CCaseId):
            {
                var row = await db.LitigationCaseSourceReports.FirstOrDefaultAsync(s => s.LitigationCaseSourceReportId == change.RowId, ct);
                if (row is null) return (false, null, "the row no longer exists");
                if (row.LitigationCaseId != after) return (false, Text(row.LitigationCaseId), "changed after the merge");
                if (before is { } target && await db.LitigationCaseSourceReports.AnyAsync(s => s.LitigationCaseId == target && s.LitigationReportSnapshotId == row.LitigationReportSnapshotId, ct))
                    return (false, Text(row.LitigationCaseId), "the original case now holds the same snapshot link");
                row.LitigationCaseId = before ?? row.LitigationCaseId;
                return (true, null, "");
            }
            default:
                return (false, null, $"unsupported journal entry {change.Table}.{change.Column}");
        }
    }

    private async Task<MergeCandidate?> LoadCandidateAsync(long requestId, long legacyCaseId, CancellationToken ct)
    {
        var c = await db.LitigationCases.AsNoTracking().Where(x => x.LitigationCaseId == legacyCaseId && x.RequestId == requestId)
            .Select(x => new { x.LitigationCaseId, x.FirstSeenUtc, x.MergedIntoCaseId }).FirstOrDefaultAsync(ct);
        if (c is null) return null;
        var module = await db.Set<ModuleCase>().AsNoTracking().Where(m => m.LegacyLitigationCaseId == legacyCaseId).Select(m => new { m.CaseId, m.CaseKey }).FirstOrDefaultAsync(ct);
        var linkVerified = module is not null && await db.Set<ModuleRequestLink>().AsNoTracking().AnyAsync(l => l.RequestId == requestId && l.CaseId == module.CaseId && l.Verdict == (int)PartyMatchOutcome.Verified, ct);
        var live = module is null ? null : await db.Set<ModuleCaseLive>().AsNoTracking().Where(l => l.CaseId == module.CaseId).Select(l => new { l.HearingCount }).FirstOrDefaultAsync(ct);
        var orders = await db.LitigationCaseOrders.AsNoTracking().CountAsync(o => o.LitigationCaseId == legacyCaseId && o.CollapsedIntoOrderId == null, ct);
        return new MergeCandidate(c.LitigationCaseId, requestId, module?.CaseKey, linkVerified && live is not null, orders, live?.HearingCount ?? 0, c.FirstSeenUtc, c.MergedIntoCaseId);
    }

    private static string OrderKey(LitigationCaseOrder o) => $"{o.PdfUrl?.Trim().ToUpperInvariant()}|{o.OrderDate?.Trim().ToUpperInvariant()}|{o.OrderType?.Trim().ToUpperInvariant()}";
    private static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static long? Parse(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static CaseMergeResult Refused(MergeRefusal refusal, string reason) => new(false, refusal, reason, null, null, null, 0);
}
