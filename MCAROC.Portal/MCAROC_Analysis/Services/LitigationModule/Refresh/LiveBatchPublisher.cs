using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

public sealed record LiveBatchOptions
{
    public const string SectionName = "LitigationModule:LivePublisher";

    /// <summary>Off by default; the fetch agents are off by default too, and nothing else leaves projections waiting.</summary>
    public bool Enabled { get; init; }
    /// <summary>A request's batch goes as soon as this many cases are waiting (plan 4.7: about 100).</summary>
    public int MaxCases { get; init; } = 100;
    /// <summary>...or as soon as the oldest waiting change is this old (plan 4.7: about 2 minutes).</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>How often the worker looks for due batches.</summary>
    public int PollSeconds { get; init; } = 20;
}

/// <summary>The cases of one request whose verified projection is newer than what the request last received.</summary>
public sealed record PendingPublication(long RequestId, int Cases, DateTime? OldestChangeUtc);

public sealed record LiveBatchResult(long RequestId, int Considered, int Published, int Unchanged, int NotPublished, long? NewLiveDataVersion);

/// <summary>Publishes refreshed cases into their requests in batches (plan 4.7 "Progressive publication"). The fetch agents only change a case's projection; this moves the changes
/// into the request's case columns, at most <see cref="LiveBatchOptions.MaxCases"/> at a time and at least every <see cref="LiveBatchOptions.MaxWait"/>, in one transaction with
/// <b>one</b> live-data version bump per batch, so caches and page fences are invalidated once per batch rather than once per case for hours.
/// <para>A case is waiting when its request link is Verified and the projection's version is newer than the version the link last published. Each case goes through
/// <see cref="LiveCasePublisher"/> unchanged (per-case lock, link re-read under it, never backwards, missing never erases); a case whose values did not change only moves its
/// marker and costs no bump.</para></summary>
public sealed class LiveBatchPublisher(AppDbContext db, LiveBatchOptions? options = null, TimeProvider? time = null)
{
    private readonly LiveBatchOptions _opts = options ?? new LiveBatchOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>A member-initialised class rather than a positional record, so EF can keep filtering and grouping on it in SQL.</summary>
    private sealed class Waiting
    {
        public long RequestId { get; init; }
        public long LegacyCaseId { get; init; }
        public DateTime ChangedUtc { get; init; }
    }

    /// <summary>The waiting cases: a verified link, a newer projection, and the request's own (not merged-away) case row to publish into.</summary>
    private IQueryable<Waiting> WaitingCases()
    {
        var verified = (int)PartyMatchOutcome.Verified;
        return from link in db.Set<ModuleRequestLink>().AsNoTracking()
               where link.Verdict == verified
               join live in db.Set<ModuleCaseLive>().AsNoTracking() on link.CaseId equals live.CaseId
               where link.PublishedProjectionVersion == null || live.Version > link.PublishedProjectionVersion
               join mc in db.Set<ModuleCase>().AsNoTracking() on link.CaseId equals mc.CaseId
               join lc in db.LitigationCases.AsNoTracking() on mc.LegacyLitigationCaseId equals (long?)lc.LitigationCaseId
               where lc.RequestId == link.RequestId && lc.MergedIntoCaseId == null
               select new Waiting { RequestId = link.RequestId, LegacyCaseId = lc.LitigationCaseId, ChangedUtc = live.UpdatedUtc };
    }

    public async Task<IReadOnlyList<PendingPublication>> PendingAsync(CancellationToken ct) =>
        (await WaitingCases().GroupBy(w => w.RequestId).Select(g => new { g.Key, N = g.Count(), Oldest = g.Min(w => w.ChangedUtc) }).ToListAsync(ct))
            .Select(x => new PendingPublication(x.Key, x.N, x.Oldest)).ToList();

    public Task<int> PendingCountAsync(long requestId, CancellationToken ct) => WaitingCases().CountAsync(w => w.RequestId == requestId, ct);

    /// <summary>A batch is due when enough cases wait, or the oldest waiting change is old enough. <paramref name="force"/> (the end of the live stage) publishes whatever waits.</summary>
    public bool IsDue(PendingPublication p, bool force = false) =>
        p.Cases > 0 && (force || p.Cases >= _opts.MaxCases || (p.OldestChangeUtc is { } t && Now - t >= _opts.MaxWait));

    /// <summary>Publishes one batch for the request: the oldest waiting changes first, at most <see cref="LiveBatchOptions.MaxCases"/>, one transaction, one version bump if anything
    /// changed. Safe to run twice or concurrently with itself: each case is published under its own lock and never backwards.</summary>
    public async Task<LiveBatchResult> PublishBatchAsync(long requestId, CancellationToken ct)
    {
        var batch = await WaitingCases().Where(w => w.RequestId == requestId).OrderBy(w => w.ChangedUtc).ThenBy(w => w.LegacyCaseId)
            .Select(w => w.LegacyCaseId).Take(Math.Max(1, _opts.MaxCases)).ToListAsync(ct);
        if (batch.Count == 0) return new LiveBatchResult(requestId, 0, 0, 0, 0, null);

        int published = 0, unchanged = 0, notPublished = 0;
        long? version = null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var legacyCaseId in batch)
        {
            var r = await LiveCasePublisher.PublishAsync(db, requestId, legacyCaseId, ct, bumpLiveDataVersion: false);
            switch (r.Outcome)
            {
                case PublishToRequestOutcome.Published: published++; break;
                case PublishToRequestOutcome.NothingToChange: unchanged++; break;
                default: notPublished++; break;   // the link changed under its lock since the batch was chosen: it is no longer ours to publish
            }
        }
        if (published > 0) version = await RequestLiveData.BumpAsync(db, requestId, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new LiveBatchResult(requestId, batch.Count, published, unchanged, notPublished, version);
    }

    /// <summary>One pass: every request whose batch is due gets one batch.</summary>
    public async Task<IReadOnlyList<LiveBatchResult>> PublishDueAsync(CancellationToken ct)
    {
        var results = new List<LiveBatchResult>();
        foreach (var p in await PendingAsync(ct))
            if (IsDue(p)) results.Add(await PublishBatchAsync(p.RequestId, ct));
        return results;
    }
}
