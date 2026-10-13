using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MCAROC_Analysis.Services.LitigationModule.Fetching;

public enum FetchWorkKind { StatusFetch = 1, OrdersFetch = 2, PartySearch = 3 }

public enum FetchHost { Drt = 1, Consumer = 2 }

public enum FetchWorkStatus { Queued = 1, Claimed = 2, Succeeded = 3, Failed = 4, Cancelled = 5 }

/// <summary>A claimed item, with the token that fences every later write to it.</summary>
public sealed record ClaimedWork(long WorkItemId, FetchWorkKind Kind, FetchHost Host, long? CaseId, long? RunId, string? PayloadJson, int AttemptCount, Guid LeaseToken)
{
    /// <summary>The outcome and note the previous attempt left on the item (for example an unconfirmed not-found and the exit it came through).</summary>
    public string? PreviousOutcome { get; init; }
    public string? PreviousNote { get; init; }
}

public sealed record FetchQueueOptions
{
    /// <summary>Attempts before an item is Failed. The cool-down after attempt n is <c>Cooldowns[n-1]</c> (plan 4.9: about 30 min, 2 h, 8 h, 24 h).</summary>
    public int MaxAttempts { get; init; } = 5;
    public IReadOnlyList<TimeSpan> Cooldowns { get; init; } = [TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromHours(24)];
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>A Failed item becomes eligible again at the next scheduled refresh after this long.</summary>
    public TimeSpan FailedEligibleAfter { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>The queue the fetch agents work from (plan sections 4.4 and 4.9). Items are claimed under a lease token, so two agents never fetch the same item; a crashed agent's item returns
/// to the queue when its lease expires (the first expiry is free, later ones count as attempts); a failed attempt cools down before the next; a deferral (breaker open) consumes nothing.
/// Every write to a claimed item is conditional on its lease token, so a slow agent that lost its lease cannot overwrite the outcome of the one that took over.</summary>
public sealed class FetchWorkQueue(AppDbContext db, FetchQueueOptions? options = null, TimeProvider? time = null)
{
    private readonly FetchQueueOptions _opts = options ?? new FetchQueueOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Adds an item unless an active (queued or claimed) one already exists for the same kind, host and case; either way the item is linked to <paramref name="runId"/>, so a
    /// second run that needs work already queued for the first counts it too. Only a confirmed uniqueness conflict means "already queued"; any other failure is raised.
    /// Party searches (no case) are not de-duplicated here: the caller decides what a search is.</summary>
    public async Task<EnqueueResult> EnqueueAsync(FetchWorkKind kind, FetchHost host, long? caseId, long? runId, string? payloadJson, int priority, CancellationToken ct)
    {
        if (caseId is not null && await FindActiveAsync(kind, host, caseId.Value, ct) is { } existing)
        {
            await LinkRunAsync(existing, runId, ct);
            return new EnqueueResult(existing, false);
        }
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var item = new ModuleFetchWorkItem
        {
            Kind = (int)kind, Host = (int)host, CaseId = caseId, RunId = runId, PayloadJson = payloadJson, Priority = priority,
            Status = (int)FetchWorkStatus.Queued, CreatedUtc = Now
        };
        db.Set<ModuleFetchWorkItem>().Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            if (runId is { } rid)
            {
                db.Set<ModuleFetchWorkItemRun>().Add(new ModuleFetchWorkItemRun { WorkItemId = item.WorkItemId, RunId = rid });
                await db.SaveChangesAsync(ct);
            }
            if (tx is not null) await tx.CommitAsync(ct);
            return new EnqueueResult(item.WorkItemId, true);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && caseId is not null)
        {
            // Two enqueuers raced: the unique index let one win. Join the winner's item.
            db.ChangeTracker.Clear();
            if (tx is not null) await tx.RollbackAsync(ct);
            var winner = await FindActiveAsync(kind, host, caseId.Value, ct)
                ?? throw new InvalidOperationException("A uniqueness conflict was reported but no active item was found.", ex);
            await LinkRunAsync(winner, runId, ct);
            return new EnqueueResult(winner, false);
        }
    }

    private async Task<long?> FindActiveAsync(FetchWorkKind kind, FetchHost host, long caseId, CancellationToken ct) =>
        await db.Set<ModuleFetchWorkItem>().AsNoTracking().Where(w => w.Kind == (int)kind && w.Host == (int)host && w.CaseId == caseId
                && (w.Status == (int)FetchWorkStatus.Queued || w.Status == (int)FetchWorkStatus.Claimed))
            .Select(w => (long?)w.WorkItemId).FirstOrDefaultAsync(ct);

    private async Task LinkRunAsync(long workItemId, long? runId, CancellationToken ct)
    {
        if (runId is not { } rid) return;
        if (await db.Set<ModuleFetchWorkItemRun>().AsNoTracking().AnyAsync(l => l.WorkItemId == workItemId && l.RunId == rid, ct)) return;
        db.Set<ModuleFetchWorkItemRun>().Add(new ModuleFetchWorkItemRun { WorkItemId = workItemId, RunId = rid });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();   // another caller linked the same run first: that is the intended outcome
        }
    }

    /// <summary>SQL Server error 2601 / 2627: a unique index or primary key was violated. Nothing else is a duplicate.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    /// <summary>Takes the best due item of a host for this agent, or null when there is none. First hands back any claim whose lease has run out, then claims atomically
    /// (<c>UPDLOCK, READPAST</c>), so any number of agents can call this at once and each item goes to exactly one of them.</summary>
    public async Task<ClaimedWork?> ClaimNextAsync(FetchHost host, string agentId, CancellationToken ct)
    {
        await RecoverExpiredLeasesAsync(host, ct);
        // READPAST skips rows another claimer is locking at that instant, so a claim can come back empty while due items remain (they are being claimed by the others). Null must
        // mean "nothing is due", so an empty result is double-checked and retried, with a short growing random pause, for as long as due work remains.
        for (var attempt = 0; ; attempt++)
        {
            var work = await TryClaimOnceAsync(host, agentId, ct);
            if (work is not null) return work;
            var now = Now;
            var anyDue = await db.Set<ModuleFetchWorkItem>().AsNoTracking().AnyAsync(w => w.Host == (int)host && w.Status == (int)FetchWorkStatus.Queued
                && (w.NextAttemptUtc == null || w.NextAttemptUtc <= now), ct);
            if (!anyDue) return null;   // due work remains (others are claiming it right now): keep trying, so null always means nothing is due
            await Task.Delay(Random.Shared.Next(5, 30 + Math.Min(attempt, 10) * 20), ct);
        }
    }

    private async Task<ClaimedWork?> TryClaimOnceAsync(FetchHost host, string agentId, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var now = Now;
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                ;WITH next AS (
                    SELECT TOP (1) * FROM [litigation].[FetchWorkItems] WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE [Host] = @host AND [Status] = 1 AND ([NextAttemptUtc] IS NULL OR [NextAttemptUtc] <= @now)
                    ORDER BY [Priority] DESC, [WorkItemId])
                UPDATE next SET [Status] = 2, [LeaseToken] = @token, [LeaseOwner] = @agent, [LeaseExpiresUtc] = @expires
                OUTPUT inserted.[WorkItemId], inserted.[Kind], inserted.[Host], inserted.[CaseId], inserted.[RunId], inserted.[PayloadJson], inserted.[AttemptCount], inserted.[LastOutcome], inserted.[LastError];
                """;
            Add(command, "@host", (int)host); Add(command, "@now", now); Add(command, "@token", token); Add(command, "@agent", agentId); Add(command, "@expires", now + _opts.LeaseDuration);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new ClaimedWork(reader.GetInt64(0), (FetchWorkKind)reader.GetInt32(1), (FetchHost)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6), token)
            {
                PreviousOutcome = reader.IsDBNull(7) ? null : reader.GetString(7),
                PreviousNote = reader.IsDBNull(8) ? null : reader.GetString(8)
            };
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    /// <summary>Extends the lease of work still being done. False when the lease was lost (taken over): the agent must stop and not write a result.</summary>
    public async Task<bool> RenewAsync(ClaimedWork work, CancellationToken ct) =>
        await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == work.WorkItemId && w.LeaseToken == work.LeaseToken && w.Status == (int)FetchWorkStatus.Claimed)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LeaseExpiresUtc, Now + _opts.LeaseDuration), ct) == 1;

    /// <summary>Records success. False when this agent no longer holds the lease (its result is discarded; the item belongs to whoever has it now).</summary>
    public async Task<bool> CompleteAsync(ClaimedWork work, string outcome, CancellationToken ct) =>
        await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == work.WorkItemId && w.LeaseToken == work.LeaseToken && w.Status == (int)FetchWorkStatus.Claimed)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, (int)FetchWorkStatus.Succeeded).SetProperty(w => w.CompletedUtc, Now).SetProperty(w => w.LastOutcome, outcome)
                .SetProperty(w => w.LastError, (string?)null).SetProperty(w => w.LeaseToken, (Guid?)null).SetProperty(w => w.LeaseExpiresUtc, (DateTime?)null), ct) == 1;

    /// <summary>A failed attempt (the call retries inside the attempt are already used up). The attempt is consumed; the item cools down and is queued again, or Failed after the last attempt.</summary>
    public async Task<FetchFailureResult> FailAttemptAsync(ClaimedWork work, string outcome, string error, CancellationToken ct)
    {
        var attempts = work.AttemptCount + 1;
        var terminal = attempts >= _opts.MaxAttempts;
        var cooldown = terminal ? (TimeSpan?)null : _opts.Cooldowns[Math.Min(attempts - 1, _opts.Cooldowns.Count - 1)];
        var now = Now;
        var changed = await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == work.WorkItemId && w.LeaseToken == work.LeaseToken && w.Status == (int)FetchWorkStatus.Claimed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.AttemptCount, attempts)
                .SetProperty(w => w.Status, terminal ? (int)FetchWorkStatus.Failed : (int)FetchWorkStatus.Queued)
                .SetProperty(w => w.NextAttemptUtc, cooldown == null ? (DateTime?)null : now + cooldown.Value)
                .SetProperty(w => w.CompletedUtc, terminal ? now : (DateTime?)null)
                .SetProperty(w => w.LastOutcome, outcome).SetProperty(w => w.LastError, Truncate(error))
                .SetProperty(w => w.LeaseToken, (Guid?)null).SetProperty(w => w.LeaseExpiresUtc, (DateTime?)null), ct);
        return new FetchFailureResult(changed == 1, terminal, attempts, cooldown is null ? null : now + cooldown.Value);
    }

    /// <summary>A final failure that no retry can change (a permanent 4xx, a case that cannot be looked up). The item is Failed now, with the reason; like any Failed item it becomes
    /// eligible again at the next scheduled refresh after the cool-down.</summary>
    public async Task<bool> FailPermanentlyAsync(ClaimedWork work, string outcome, string error, CancellationToken ct)
    {
        var now = Now;
        return await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == work.WorkItemId && w.LeaseToken == work.LeaseToken && w.Status == (int)FetchWorkStatus.Claimed)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, (int)FetchWorkStatus.Failed).SetProperty(w => w.CompletedUtc, now).SetProperty(w => w.AttemptCount, work.AttemptCount + 1)
                .SetProperty(w => w.LastOutcome, outcome).SetProperty(w => w.LastError, Truncate(error))
                .SetProperty(w => w.LeaseToken, (Guid?)null).SetProperty(w => w.LeaseExpiresUtc, (DateTime?)null), ct) == 1;
    }

    /// <summary>Puts a claimed item back without using an attempt, leaving a note for the next attempt (plan 4.9 §3: a not-found is final only after a second answer through another exit,
    /// so the first one's exit is kept here). The note must not contain a proxy address or credentials: callers store a hash.</summary>
    public async Task<bool> RequeueWithNoteAsync(ClaimedWork work, DateTime retryAtUtc, string outcome, string note, CancellationToken ct) =>
        await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == work.WorkItemId && w.LeaseToken == work.LeaseToken && w.Status == (int)FetchWorkStatus.Claimed)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, (int)FetchWorkStatus.Queued).SetProperty(w => w.NextAttemptUtc, retryAtUtc).SetProperty(w => w.LastOutcome, outcome)
                .SetProperty(w => w.LastError, Truncate(note)).SetProperty(w => w.LeaseToken, (Guid?)null).SetProperty(w => w.LeaseExpiresUtc, (DateTime?)null), ct) == 1;

    /// <summary>Puts a claimed item back without using an attempt: the shared breaker is open or the host is rate limiting. It is retried at <paramref name="retryAtUtc"/>.</summary>
    public async Task<bool> DeferAsync(ClaimedWork work, DateTime retryAtUtc, string reason, CancellationToken ct) =>
        await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == work.WorkItemId && w.LeaseToken == work.LeaseToken && w.Status == (int)FetchWorkStatus.Claimed)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, (int)FetchWorkStatus.Queued).SetProperty(w => w.NextAttemptUtc, retryAtUtc).SetProperty(w => w.LastOutcome, "Deferred")
                .SetProperty(w => w.LastError, Truncate(reason)).SetProperty(w => w.LeaseToken, (Guid?)null).SetProperty(w => w.LeaseExpiresUtc, (DateTime?)null), ct) == 1;

    /// <summary>Claims whose lease ran out (the agent crashed or stalled) return to the queue. The first expiry is free; from the second, each costs an attempt, and the last attempt fails the item.</summary>
    public async Task<int> RecoverExpiredLeasesAsync(FetchHost host, CancellationToken ct)
    {
        var now = Now;
        var expired = await db.Set<ModuleFetchWorkItem>().AsNoTracking()
            .Where(w => w.Host == (int)host && w.Status == (int)FetchWorkStatus.Claimed && w.LeaseExpiresUtc < now)
            .Select(w => new { w.WorkItemId, w.LeaseToken, w.AttemptCount, w.LeaseExpiryCount }).ToListAsync(ct);
        var recovered = 0;
        foreach (var item in expired)
        {
            var expiries = item.LeaseExpiryCount + 1;
            var attempts = expiries >= 2 ? item.AttemptCount + 1 : item.AttemptCount;
            var terminal = attempts >= _opts.MaxAttempts;
            recovered += await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == item.WorkItemId && w.LeaseToken == item.LeaseToken && w.Status == (int)FetchWorkStatus.Claimed && w.LeaseExpiresUtc < now)   // a renewal after the read keeps the claim
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.LeaseExpiryCount, expiries).SetProperty(w => w.AttemptCount, attempts)
                    .SetProperty(w => w.Status, terminal ? (int)FetchWorkStatus.Failed : (int)FetchWorkStatus.Queued)
                    .SetProperty(w => w.CompletedUtc, terminal ? now : (DateTime?)null)
                    .SetProperty(w => w.LastOutcome, "LeaseExpired").SetProperty(w => w.LastError, "the agent stopped reporting")
                    .SetProperty(w => w.LeaseToken, (Guid?)null).SetProperty(w => w.LeaseExpiresUtc, (DateTime?)null), ct);
        }
        return recovered;
    }

    /// <summary>Failed items older than the cool-down become eligible again at the next scheduled refresh: they are queued afresh unless an active item for the same work exists.</summary>
    public async Task<int> RequeueFailedAsync(CancellationToken ct)
    {
        var cutoff = Now - _opts.FailedEligibleAfter;
        var failed = await db.Set<ModuleFetchWorkItem>().AsNoTracking()
            .Where(w => w.Status == (int)FetchWorkStatus.Failed && w.CompletedUtc <= cutoff && w.CaseId != null)
            .Select(w => new { w.WorkItemId, w.Kind, w.Host, w.CaseId }).ToListAsync(ct);
        var requeued = 0;
        foreach (var f in failed)
        {
            if (await db.Set<ModuleFetchWorkItem>().AnyAsync(w => w.Kind == f.Kind && w.Host == f.Host && w.CaseId == f.CaseId
                    && (w.Status == (int)FetchWorkStatus.Queued || w.Status == (int)FetchWorkStatus.Claimed), ct)) continue;
            requeued += await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == f.WorkItemId && w.Status == (int)FetchWorkStatus.Failed)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, (int)FetchWorkStatus.Queued).SetProperty(w => w.AttemptCount, 0).SetProperty(w => w.LeaseExpiryCount, 0)
                    .SetProperty(w => w.NextAttemptUtc, (DateTime?)null).SetProperty(w => w.CompletedUtc, (DateTime?)null), ct);
        }
        return requeued;
    }

    /// <summary>The counts of the items a run depends on, by status, for the banner and the "not updated" panel. An item shared with another run counts for both.</summary>
    public async Task<IReadOnlyDictionary<FetchWorkStatus, int>> CountsForRunAsync(long runId, CancellationToken ct) =>
        (await (from link in db.Set<ModuleFetchWorkItemRun>().AsNoTracking()
                where link.RunId == runId
                join w in db.Set<ModuleFetchWorkItem>().AsNoTracking() on link.WorkItemId equals w.WorkItemId
                group w by w.Status into g
                select new { g.Key, N = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => (FetchWorkStatus)x.Key, x => x.N);

    private static string? Truncate(string? text) => text is { Length: > 1900 } t ? t[..1900] : text;

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name; p.Value = value;
        command.Parameters.Add(p);
    }
}

/// <param name="WorkItemId">The item the work is in: the new one, or the active one that already existed.</param>
/// <param name="Created">False when an active item for the same work already existed.</param>
public sealed record EnqueueResult(long WorkItemId, bool Created);

public sealed record FetchFailureResult(bool Recorded, bool Terminal, int AttemptCount, DateTime? NextAttemptUtc);
