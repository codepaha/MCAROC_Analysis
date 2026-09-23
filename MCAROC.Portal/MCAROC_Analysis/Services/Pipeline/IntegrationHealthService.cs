using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Reads and updates <see cref="IntegrationHealth"/> — see docs/pipeline-automation-plan.md §5.4
/// for the exact concurrency contract this implements. Every write below is a single parameterised
/// <c>UPDATE</c> statement (never read-modify-write): the engine evaluates the whole CASE/JOIN against one
/// consistent snapshot of the row under its own lock, so two callers racing to report a failure/success for
/// the same integration can never observe or produce an inconsistent intermediate state. Registered scoped
/// (needs the request/job-scoped <see cref="AppDbContext"/>) — callers such as <see
/// cref="ReferenceToolHealthProbe"/> that are singletons create their own scope per tick.</summary>
public interface IIntegrationHealthService
{
    /// <summary>Records a failed call. <paramref name="callStartUtc"/> is when the call was *issued*, not
    /// when it failed — used for stale-result fencing (§5.4): a failure from a call that started before the
    /// row's last transition is ignored, so a slow request that failed under old conditions can never
    /// re-open a breaker a probe already closed. <paramref name="authRejected"/> opens the breaker
    /// immediately regardless of <paramref name="threshold"/> — bad credentials should never be retried
    /// `threshold` times against the account.</summary>
    Task ReportFailureAsync(IntegrationName name, DateTime callStartUtc, string? error, bool authRejected, int threshold, TimeSpan probeLease, CancellationToken ct);

    /// <summary>Records a successful call. Resets <see cref="IntegrationHealth.ConsecutiveFailures"/> and
    /// moves <see cref="IntegrationHealthState.Degraded"/> back to <see cref="IntegrationHealthState.Healthy"/>
    /// — but never touches a row that is <see cref="IntegrationHealthState.Open"/>: per §5.4, an ordinary
    /// in-flight caller's success must never close the breaker, only <see cref="TryCloseAfterProbeAsync"/> may.</summary>
    Task ReportSuccessAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct);

    /// <summary>Half-open claim: true if this caller is the one that gets to probe right now (one winner —
    /// the same statement that checks eligibility also stakes the claim by pushing <see
    /// cref="IntegrationHealth.NextProbeUtc"/> out by <paramref name="probeLease"/>, so a second concurrent
    /// caller sees the row as not-yet-eligible and returns false instead of also probing).</summary>
    Task<bool> TryClaimHalfOpenProbeAsync(IntegrationName name, TimeSpan probeLease, CancellationToken ct);

    /// <summary>The only path that moves a row out of <see cref="IntegrationHealthState.Open"/> — called by
    /// <see cref="ReferenceToolHealthProbe"/> after a probe call it won via <see
    /// cref="TryClaimHalfOpenProbeAsync"/> succeeds.</summary>
    Task TryCloseAfterProbeAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct);

    Task<IntegrationHealth?> GetAsync(IntegrationName name, CancellationToken ct);
    Task<bool> IsOpenAsync(IntegrationName name, CancellationToken ct);
}

public sealed class IntegrationHealthService(AppDbContext db) : IIntegrationHealthService
{
    /// <summary>Distant-past sentinel used to seed a new row's <c>LastTransitionUtc</c> — never <see
    /// cref="DateTime.UtcNow"/>. The stale-result fencing below rejects any write whose <c>callStartUtc</c>
    /// is before the row's current <c>LastTransitionUtc</c>, and a call's <c>callStartUtc</c> is always
    /// earlier than the moment <see cref="EnsureRowExistsAsync"/> actually runs; seeding with "now" would
    /// make a brand-new row's very first write reject itself as stale.</summary>
    private static readonly DateTime EpochSentinel = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Lazily creates the row for <paramref name="name"/> the first time anything reports
    /// against it — avoids relying on EF migration data-seeding for a table with a database-computed
    /// <c>rowversion</c> column (HasData cannot supply that column's value). Race-safe: a second concurrent
    /// caller's INSERT hits the unique index on <see cref="IntegrationHealth.Name"/> and is swallowed here,
    /// since by then the row it wanted already exists.</summary>
    private async Task EnsureRowExistsAsync(IntegrationName name, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
IF NOT EXISTS (SELECT 1 FROM IntegrationHealths WHERE Name = {name.ToString()})
INSERT INTO IntegrationHealths (Name, State, ConsecutiveFailures, LastSuccessUtc, LastError, OpenedUtc, LastTransitionUtc, NextProbeUtc)
VALUES ({name.ToString()}, 'Healthy', 0, NULL, NULL, NULL, {EpochSentinel}, NULL)", ct);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            // Lost the race to another concurrent caller inserting the same row — fine, it exists now.
        }
    }

    public async Task ReportFailureAsync(IntegrationName name, DateTime callStartUtc, string? error, bool authRejected, int threshold, TimeSpan probeLease, CancellationToken ct)
    {
        await EnsureRowExistsAsync(name, ct);
        var now = DateTime.UtcNow;
        var probeAt = now.Add(probeLease);
        var errorText = string.IsNullOrEmpty(error) ? null : (error.Length > 2000 ? error[..2000] : error);
        var nameText = name.ToString();

        await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE h
SET
    h.ConsecutiveFailures = h.ConsecutiveFailures + 1,
    h.LastError = {errorText},
    h.State = x.NewState,
    h.OpenedUtc = CASE WHEN h.State <> 'Open' AND x.NewState = 'Open' THEN {now} ELSE h.OpenedUtc END,
    h.LastTransitionUtc = CASE WHEN h.State <> x.NewState THEN {now} ELSE h.LastTransitionUtc END,
    h.NextProbeUtc = CASE WHEN x.NewState = 'Open' AND h.State <> 'Open' THEN {probeAt} ELSE h.NextProbeUtc END
FROM IntegrationHealths h
CROSS APPLY (SELECT CASE WHEN {authRejected} = 1 OR h.ConsecutiveFailures + 1 >= {threshold} THEN 'Open' ELSE 'Degraded' END AS NewState) x
WHERE h.Name = {nameText} AND h.LastTransitionUtc <= {callStartUtc}", ct);
    }

    public async Task ReportSuccessAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct)
    {
        await EnsureRowExistsAsync(name, ct);
        var now = DateTime.UtcNow;
        var nameText = name.ToString();

        await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE IntegrationHealths
SET
    ConsecutiveFailures = 0,
    LastSuccessUtc = {now},
    LastTransitionUtc = CASE WHEN State = 'Degraded' THEN {now} ELSE LastTransitionUtc END,
    State = 'Healthy'
WHERE Name = {nameText} AND State <> 'Open' AND LastTransitionUtc <= {callStartUtc}", ct);
    }

    public async Task<bool> TryClaimHalfOpenProbeAsync(IntegrationName name, TimeSpan probeLease, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var probeAt = now.Add(probeLease);
        var nameText = name.ToString();

        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE IntegrationHealths
SET NextProbeUtc = {probeAt}
WHERE Name = {nameText} AND State = 'Open' AND (NextProbeUtc IS NULL OR NextProbeUtc <= {now})", ct);
        return rows > 0;
    }

    public async Task TryCloseAfterProbeAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nameText = name.ToString();

        await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE IntegrationHealths
SET
    State = 'Healthy',
    ConsecutiveFailures = 0,
    LastSuccessUtc = {now},
    OpenedUtc = NULL,
    LastTransitionUtc = {now},
    NextProbeUtc = NULL
WHERE Name = {nameText} AND State = 'Open' AND LastTransitionUtc <= {callStartUtc}", ct);
    }

    public Task<IntegrationHealth?> GetAsync(IntegrationName name, CancellationToken ct) =>
        db.IntegrationHealths.AsNoTracking().FirstOrDefaultAsync(h => h.Name == name, ct);

    public async Task<bool> IsOpenAsync(IntegrationName name, CancellationToken ct)
    {
        var state = await db.IntegrationHealths.AsNoTracking()
            .Where(h => h.Name == name)
            .Select(h => (IntegrationHealthState?)h.State)
            .FirstOrDefaultAsync(ct);
        return state == IntegrationHealthState.Open;
    }
}
