using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Fetching;

/// <summary>How a portal call went, as far as the host's health is concerned. The class is decided from the response, not the exception type (plan 4.9 table).</summary>
public enum HostCallOutcome
{
    /// <summary>The portal answered: data, an empty "not found", a 400 "not available", even a body that does not parse. The host is up.</summary>
    Answered = 1,
    /// <summary>HTTP 429: the host asks us to slow down. Halves the host's concurrency and counts toward the breaker.</summary>
    Throttled = 2,
    /// <summary>HTTP 5xx, a timeout or a refused connection: counts toward the breaker.</summary>
    Unavailable = 3,
    /// <summary>Nothing reached the host (cancelled before the call, a local error): no effect on its health.</summary>
    NotSent = 4
}

public enum HostEntryRefusal { None = 0, BreakerOpen = 1, AtCapacity = 2 }

/// <summary>Permission to send calls to a host for one work item. Release it when the item is done, whatever happened.</summary>
public sealed record HostPermit(FetchHost Host, string HolderId, bool IsProbe);

/// <summary>The gate's answer. On refusal, <see cref="RetryAtUtc"/> is when trying again makes sense: the end of the open period for a broken host, a short pause for a full one.
/// A refused item is deferred, never failed, and its attempt is not consumed (plan 4.9 §5).</summary>
public sealed record HostEntry(HostPermit? Permit, HostEntryRefusal Refusal, DateTime? RetryAtUtc)
{
    public bool Admitted => Permit is not null;
}

/// <summary>What the banner and the status endpoint show about a host.</summary>
public sealed record HostTrafficSnapshot(FetchHost Host, bool BreakerOpen, DateTime? OpenUntilUtc, int Concurrency, int MaxConcurrency, int ConsecutiveFailures, DateTime? LastSuccessUtc, DateTime? LastFailureUtc, string? LastFailure);

public sealed record HostTrafficOptions
{
    public const string SectionName = "LitigationPortals:Traffic";

    /// <summary>The agreed total concurrency for the DRT portal, across every agent and process.</summary>
    public int DrtMaxConcurrency { get; init; } = 2;
    /// <summary>The agreed total concurrency for the consumer-commission portal, across every agent and process.</summary>
    public int ConsumerMaxConcurrency { get; init; } = 2;
    /// <summary>The floor a 429 can halve concurrency to.</summary>
    public int MinConcurrency { get; init; } = 1;
    /// <summary>How much concurrency comes back per answered call after a 429 (additive increase): 0.1 means ten good answers per extra slot.</summary>
    public double AdditiveIncrease { get; init; } = 0.1;
    /// <summary>Consecutive 5xx / 429 / timeouts across all agents before the host's breaker opens (plan 4.9 proposes 8).</summary>
    public int FailureThreshold { get; init; } = 8;
    /// <summary>How long an open breaker keeps everyone away before one probe is allowed.</summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>How long one probe may hold the half-open breaker before another agent may probe instead (the prober may have died).</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>The pause suggested to an agent refused because the host is at its concurrency.</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How long a permit lasts without renewal; a dead agent's permit frees itself after this.</summary>
    public TimeSpan PermitDuration { get; init; } = TimeSpan.FromMinutes(5);

    public int MaxConcurrencyFor(FetchHost host) => Math.Max(MinConcurrency, host == FetchHost.Drt ? DrtMaxConcurrency : ConsumerMaxConcurrency);
}

/// <summary>The traffic control every fetch agent goes through before it calls a court portal (plan 4.4 flow 5 and 4.9 §5). It is shared through the database, not held per agent or per
/// process, so adding agents can never multiply the load on a portal:
/// <list type="bullet">
/// <item>a <b>global per-host cap</b>: each permit is a holder of the multi-holder operational lease for the host, whose capacity is the host's current concurrency;</item>
/// <item><b>additive increase, multiplicative decrease</b>: a 429 halves the current concurrency (never below the floor), each answered call adds a fraction back up to the agreed maximum;</item>
/// <item>a <b>shared circuit breaker</b>: after N consecutive 5xx/429/timeouts across all agents it opens; while open nobody is admitted; after the open period exactly one agent is
/// admitted as a probe (half-open), and its result closes the breaker or opens it again.</item>
/// </list>
/// Every state change is one conditional UPDATE on the host's row, so concurrent agents cannot lose each other's reports.</summary>
public sealed class HostTrafficGate(AppDbContext db, IOperationalSlotLeaseService slots, HostTrafficOptions? options = null, TimeProvider? time = null)
{
    private readonly HostTrafficOptions _opts = options ?? new HostTrafficOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public static string SlotType(FetchHost host) => "LitigationHost_" + host;

    /// <summary>Asks to send calls to <paramref name="host"/> for one work item.</summary>
    public async Task<HostEntry> TryEnterAsync(FetchHost host, string agentId, CancellationToken ct)
    {
        var state = await EnsureStateAsync(host, ct);
        var now = Now;
        var holder = agentId + ":" + Guid.NewGuid().ToString("N");
        if (holder.Length > 100) holder = holder[^100..];

        var isProbe = false;
        if (state.OpenUntilUtc is { } openUntil)
        {
            if (openUntil > now) return new HostEntry(null, HostEntryRefusal.BreakerOpen, openUntil);
            // Half-open: one probe at a time. Taking the probe is a conditional update, so two agents cannot both become the prober.
            var probeUntil = now + _opts.ProbeTimeout;
            var took = await db.Set<ModuleHostTrafficState>().Where(s => s.Host == (int)host && s.OpenUntilUtc != null && s.OpenUntilUtc <= now
                    && (s.ProbeHolder == null || s.ProbeUntilUtc < now))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ProbeHolder, holder).SetProperty(s => s.ProbeUntilUtc, probeUntil), ct);
            if (took == 0)
            {
                // Someone else is probing, or the breaker closed meanwhile: look again rather than guess.
                var again = await ReadAsync(host, ct);
                if (again.OpenUntilUtc is not null)
                    return new HostEntry(null, HostEntryRefusal.BreakerOpen, again.OpenUntilUtc > now ? again.OpenUntilUtc : again.ProbeUntilUtc ?? now + _opts.BusyRetryDelay);
            }
            else isProbe = true;
        }

        // A probe goes alone; otherwise the host's current concurrency is the lease capacity.
        var capacity = isProbe ? int.MaxValue : CurrentConcurrency(state, host);
        var lease = await slots.TryAcquireSlotAsync(SlotType(host), holder, _opts.PermitDuration, capacity, ct);
        if (lease.Success) return new HostEntry(new HostPermit(host, holder, isProbe), HostEntryRefusal.None, null);

        if (isProbe) await ReleaseProbeAsync(host, holder, ct);
        return new HostEntry(null, HostEntryRefusal.AtCapacity, now + _opts.BusyRetryDelay);
    }

    /// <summary>Keeps a permit alive across a long item (a big download).</summary>
    public Task<bool> RenewAsync(HostPermit permit, CancellationToken ct) => slots.TryRenewSlotAsync(SlotType(permit.Host), permit.HolderId, _opts.PermitDuration, ct);

    /// <summary>Records how one call went. Call it once per portal call (an item may make several), before the permit is released.</summary>
    public async Task ReportAsync(HostPermit permit, HostCallOutcome outcome, string? detail, TimeSpan? retryAfter, CancellationToken ct)
    {
        if (outcome == HostCallOutcome.NotSent) return;
        await EnsureStateAsync(permit.Host, ct);   // a report must never be lost for want of the row
        var host = (int)permit.Host;
        var now = Now;
        var max = (double)_opts.MaxConcurrencyFor(permit.Host);
        var min = (double)Math.Max(1, _opts.MinConcurrency);
        var states = db.Set<ModuleHostTrafficState>().Where(s => s.Host == host);
        switch (outcome)
        {
            case HostCallOutcome.Answered:
            {
                var inc = _opts.AdditiveIncrease;
                await states.ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.ConsecutiveFailures, 0)
                    .SetProperty(s => s.OpenUntilUtc, (DateTime?)null)
                    .SetProperty(s => s.ProbeHolder, (string?)null)
                    .SetProperty(s => s.ProbeUntilUtc, (DateTime?)null)
                    .SetProperty(s => s.Concurrency, s => s.Concurrency + inc >= max ? max : s.Concurrency + inc)
                    .SetProperty(s => s.LastSuccessUtc, now), ct);
                break;
            }
            case HostCallOutcome.Throttled:
            case HostCallOutcome.Unavailable:
            {
                var throttled = outcome == HostCallOutcome.Throttled;
                var threshold = _opts.FailureThreshold;
                var openUntil = now + _opts.OpenDuration;
                // Retry-After is honoured as a minimum open period for everyone, not just the agent that read it.
                if (retryAfter is { } ra && now + ra > openUntil) openUntil = now + ra;
                var reason = Truncate((throttled ? "429" : "unavailable") + (detail is null ? "" : ": " + detail), 500);
                var probeHolder = permit.IsProbe ? permit.HolderId : null;
                var honourRetryAfter = retryAfter is not null;
                await states.ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.ConsecutiveFailures, s => s.ConsecutiveFailures + 1)
                    // A failed probe reopens at once; otherwise the breaker opens at the threshold, or on a Retry-After the host sent us.
                    .SetProperty(s => s.OpenUntilUtc, s => (probeHolder != null && s.ProbeHolder == probeHolder) || s.ConsecutiveFailures + 1 >= threshold || honourRetryAfter
                        ? (s.OpenUntilUtc != null && s.OpenUntilUtc > openUntil ? s.OpenUntilUtc : openUntil)
                        : s.OpenUntilUtc)
                    .SetProperty(s => s.ProbeHolder, s => probeHolder != null && s.ProbeHolder == probeHolder ? null : s.ProbeHolder)
                    .SetProperty(s => s.ProbeUntilUtc, s => probeHolder != null && s.ProbeHolder == probeHolder ? null : s.ProbeUntilUtc)
                    .SetProperty(s => s.Concurrency, s => throttled ? (s.Concurrency / 2 <= min ? min : s.Concurrency / 2) : s.Concurrency)
                    .SetProperty(s => s.LastFailureUtc, now)
                    .SetProperty(s => s.LastFailure, reason), ct);
                break;
            }
            case HostCallOutcome.NotSent:
                break;
        }
    }

    /// <summary>Gives the permit back. A probe that never reported frees the probe slot so another agent can probe.</summary>
    public async Task ReleaseAsync(HostPermit permit, CancellationToken ct)
    {
        await slots.ReleaseSlotAsync(SlotType(permit.Host), permit.HolderId, ct);
        if (permit.IsProbe) await ReleaseProbeAsync(permit.Host, permit.HolderId, ct);
    }

    public async Task<HostTrafficSnapshot> SnapshotAsync(FetchHost host, CancellationToken ct)
    {
        var s = await EnsureStateAsync(host, ct);
        var now = Now;
        return new HostTrafficSnapshot(host, s.OpenUntilUtc is { } u && u > now, s.OpenUntilUtc, CurrentConcurrency(s, host), _opts.MaxConcurrencyFor(host),
            s.ConsecutiveFailures, s.LastSuccessUtc, s.LastFailureUtc, s.LastFailure);
    }

    /// <summary>The whole number of concurrent permits the host takes now: the AIMD value rounded down, within the floor and the configured maximum (which may have been lowered since).</summary>
    private int CurrentConcurrency(ModuleHostTrafficState s, FetchHost host) =>
        Math.Clamp((int)Math.Floor(s.Concurrency), Math.Max(1, _opts.MinConcurrency), _opts.MaxConcurrencyFor(host));

    private Task ReleaseProbeAsync(FetchHost host, string holder, CancellationToken ct) =>
        db.Set<ModuleHostTrafficState>().Where(s => s.Host == (int)host && s.ProbeHolder == holder)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ProbeHolder, (string?)null).SetProperty(s => s.ProbeUntilUtc, (DateTime?)null), ct);

    private Task<ModuleHostTrafficState> ReadAsync(FetchHost host, CancellationToken ct) =>
        db.Set<ModuleHostTrafficState>().AsNoTracking().SingleAsync(s => s.Host == (int)host, ct);

    private async Task<ModuleHostTrafficState> EnsureStateAsync(FetchHost host, CancellationToken ct)
    {
        var existing = await db.Set<ModuleHostTrafficState>().AsNoTracking().FirstOrDefaultAsync(s => s.Host == (int)host, ct);
        if (existing is not null) return existing;
        db.Set<ModuleHostTrafficState>().Add(new ModuleHostTrafficState { Host = (int)host, Concurrency = _opts.MaxConcurrencyFor(host) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Another agent created the row first: theirs is the row.
        }
        db.ChangeTracker.Clear();
        return await ReadAsync(host, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
