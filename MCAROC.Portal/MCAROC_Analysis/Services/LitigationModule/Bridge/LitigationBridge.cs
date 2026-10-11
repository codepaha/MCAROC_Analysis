using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Bridge;

/// <summary>Switches for the bridge between the legacy litigation stores and the module (plan section 12.5). Off by default: with no consumer reading the module
/// yet, nothing should queue events for it. Section <c>LitigationModule:Bridge</c>.</summary>
public sealed class LitigationBridgeOptions
{
    public const string SectionName = "LitigationModule:Bridge";

    /// <summary>When true, legacy writers record outbox events and the worker applies them.</summary>
    public bool Enabled { get; set; }

    public int BatchSize { get; set; } = 200;
    public int PollSeconds { get; set; } = 10;

    /// <summary>Failed attempts before an event is dead-lettered (kept, flagged, and counted in the lag report; never dropped).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>A scope whose pending backlog exceeds this is reported as syncing, so a consumer can serve it from legacy instead of half-synced data.</summary>
    public int SyncingBacklogThreshold { get; set; } = 500;
}

/// <summary>What a legacy writer calls. It adds an outbox row to the caller's own <see cref="AppDbContext"/> so that it is saved <b>in the same transaction</b> as the
/// legacy write; nothing is saved here.</summary>
public static class LitigationBridgeWriter
{
    /// <summary>Records that a legacy case was inserted or changed. Returns the row added (so a caller that retries a failed save can detach it), or null when the bridge is off.</summary>
    public static object? RecordCaseUpserted(AppDbContext db, LitigationCase legacyCase, LitigationBridgeOptions? options, DateTime nowUtc)
    {
        if (options is not { Enabled: true }) return null;
        var row = new ModuleBridgeOutbox
        {
            Kind = BridgeEventKind.CaseUpserted, LegacyCase = legacyCase, RequestId = legacyCase.RequestId, CreatedUtc = nowUtc
        };
        db.Set<ModuleBridgeOutbox>().Add(row);
        return row;
    }
}

public sealed record BridgeLag(int Pending, int DeadLettered, TimeSpan? OldestPendingAge);

public sealed record BridgeBatchResult(int Applied, int Failed, int DeadLettered);

/// <summary>Applies outbox events to the module, idempotently and in order. Applying an event is "make the module agree with the legacy row as it is now": it finds
/// or creates the registry case by its permanent legacy reference, makes sure the request's scope and subject exist, puts the case in the scope with its alias, and
/// records when the scope's feed last listed it. Running it twice, or after a crash half way through a batch, gives the same result.</summary>
public sealed class LitigationBridgeProcessor(AppDbContext db, LitigationBridgeOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<BridgeBatchResult> ProcessBatchAsync(CancellationToken ct)
    {
        var batch = await db.Set<ModuleBridgeOutbox>()
            .Where(o => o.ProcessedUtc == null && o.DeadLetteredUtc == null)
            .OrderBy(o => o.OutboxId).Take(options.BatchSize).ToListAsync(ct);

        int applied = 0, failed = 0, dead = 0;
        // Several events for one case collapse to one apply: the module is brought up to the row as it is now, so applying once is enough and the order per case is kept.
        foreach (var group in batch.GroupBy(o => o.LegacyLitigationCaseId))
        {
            var now = _time.GetUtcNow().UtcDateTime;
            try
            {
                await ApplyAsync(group.Key, group.Max(o => o.CreatedUtc), ct);
                foreach (var o in group) o.ProcessedUtc = now;
                applied += group.Count();
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                var message = ex.Message.Length > 1800 ? ex.Message[..1800] : ex.Message;
                foreach (var o in group.Select(g => g.OutboxId))
                {
                    var row = await db.Set<ModuleBridgeOutbox>().FirstAsync(x => x.OutboxId == o, ct);
                    row.Attempts++;
                    row.LastError = message;
                    if (row.Attempts >= options.MaxAttempts) { row.DeadLetteredUtc = now; dead++; }
                    failed++;
                }
                await db.SaveChangesAsync(ct);
            }
        }
        return new BridgeBatchResult(applied, failed, dead);
    }

    /// <summary>Makes the module agree with one legacy case. Public for the backfill, which is the same operation run over every existing case.</summary>
    public async Task<bool> ApplyAsync(long legacyLitigationCaseId, DateTime observedUtc, CancellationToken ct)
    {
        var legacy = await db.LitigationCases.AsNoTracking().Where(c => c.LitigationCaseId == legacyLitigationCaseId)
            .Select(c => new { c.LitigationCaseId, c.RequestId, c.LastSeenUtc }).FirstOrDefaultAsync(ct);
        if (legacy is null) return false;   // deleted since the event was written: nothing to mirror

        var store = new LitigationModuleStore(db, _time);
        var scope = await EnsureRequestScopeAsync(store, legacy.RequestId, ct);
        var moduleCase = await store.EnsureCaseForLegacyAsync(legacy.LitigationCaseId, ct);
        var link = await store.AddCaseToScopeAsync(scope.ScopeId, moduleCase.CaseId, null, ct);

        var seen = legacy.LastSeenUtc > observedUtc ? legacy.LastSeenUtc : observedUtc;
        if (link.FeedLastSeenUtc is null || link.FeedLastSeenUtc < seen)
        {
            await db.Set<ModuleScopeCase>().Where(x => x.ScopeCaseId == link.ScopeCaseId && (x.FeedLastSeenUtc == null || x.FeedLastSeenUtc < seen))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FeedLastSeenUtc, seen), ct);
        }
        return true;
    }

    private async Task<ModuleScope> EnsureRequestScopeAsync(LitigationModuleStore store, long requestId, CancellationToken ct)
    {
        var existing = await db.Set<ModuleScope>().AsNoTracking().FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Request && s.ExternalId == requestId, ct);
        if (existing is not null) return existing;
        var request = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => new { r.CompanyName, r.Cin }).FirstAsync(ct);
        var identifiers = request.Cin is null ? null : System.Text.Json.JsonSerializer.Serialize(new { cin = request.Cin });
        var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, request.CompanyName ?? $"Request {requestId}", identifiers, ct);
        return await store.EnsureScopeAsync(ModuleScopeKind.Request, requestId, subject.SubjectId, ct);
    }

    public async Task<BridgeLag> GetLagAsync(CancellationToken ct)
    {
        var pending = db.Set<ModuleBridgeOutbox>().AsNoTracking().Where(o => o.ProcessedUtc == null && o.DeadLetteredUtc == null);
        var count = await pending.CountAsync(ct);
        var oldest = count == 0 ? null : await pending.MinAsync(o => (DateTime?)o.CreatedUtc, ct);
        var dead = await db.Set<ModuleBridgeOutbox>().AsNoTracking().CountAsync(o => o.DeadLetteredUtc != null, ct);
        return new BridgeLag(count, dead, oldest is null ? null : _time.GetUtcNow().UtcDateTime - oldest.Value);
    }

    /// <summary>Whether the request's scope has a backlog above the threshold, i.e. should be served from legacy rather than from half-synced module data.</summary>
    public async Task<bool> IsSyncingAsync(long requestId, CancellationToken ct) =>
        await db.Set<ModuleBridgeOutbox>().AsNoTracking()
            .CountAsync(o => o.RequestId == requestId && o.ProcessedUtc == null && o.DeadLetteredUtc == null, ct) > options.SyncingBacklogThreshold;
}

public sealed record BackfillResult(int Visited, int Applied, long? LastLegacyCaseId);

/// <summary>The one-time, resumable backfill (plan section 12.5): every existing legacy case gets its module case and scope association. It is the bridge's apply operation
/// run over every row, in id order, so it is safe to run again: a second run changes nothing. It merges nothing. Resume by passing the last id of the previous run.</summary>
public static class LitigationBridgeBackfill
{
    public static async Task<BackfillResult> RunAsync(AppDbContext db, LitigationBridgeOptions options, long? afterLegacyCaseId, int maxCases, CancellationToken ct)
    {
        var processor = new LitigationBridgeProcessor(db, options);
        var ids = await db.LitigationCases.AsNoTracking().Where(c => afterLegacyCaseId == null || c.LitigationCaseId > afterLegacyCaseId)
            .OrderBy(c => c.LitigationCaseId).Select(c => c.LitigationCaseId).Take(maxCases).ToListAsync(ct);
        int applied = 0;
        foreach (var id in ids)
        {
            if (await processor.ApplyAsync(id, DateTime.MinValue, ct)) applied++;
        }
        return new BackfillResult(ids.Count, applied, ids.Count == 0 ? afterLegacyCaseId : ids[^1]);
    }
}
