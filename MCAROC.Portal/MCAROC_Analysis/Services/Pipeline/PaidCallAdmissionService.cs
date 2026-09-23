using System.Security.Cryptography;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Pipeline;

public enum AdmissionDenial
{
    /// <summary>Another admission for the same scope is still Reserved.</summary>
    InFlight,
    /// <summary>The scope was committed inside its freshness window (auto only).</summary>
    Fresh,
    /// <summary>The day's auto cap for this kind is used up (auto only).</summary>
    CostCapReached
}

public sealed record PaidCallAdmissionRequest(
    PaidCallKind Kind, string ScopeKey, PaidCallTrigger Trigger, long RequestId, long? ClientId = null, string? CorrelationId = null);

public sealed record AdmissionResult(long? AdmissionId, AdmissionDenial? Denial, DateTime ReservedUtc)
{
    public bool Admitted => AdmissionId is not null;
}

public enum AdmissionResolution { StillReserved, Committed, Released, AlreadyResolved }

/// <summary>The database-backed authority for every paid (or rate-limited) call — see
/// docs/pipeline-automation-plan.md §4.0. An in-memory check can pass on two instances at once; this cannot.</summary>
public interface IPaidCallAdmission
{
    Task<AdmissionResult> TryAdmitAsync(PaidCallAdmissionRequest request, CancellationToken ct);
    Task SetReferenceAsync(long admissionId, long referenceId, CancellationToken ct);
    Task<bool> CommitAsync(long admissionId, DateTime committedUtc, CancellationToken ct);
    Task<bool> ReleaseAsync(long admissionId, CancellationToken ct);
    Task<AdmissionResolution> ResolveAsync(long admissionId, CancellationToken ct);
    /// <summary>Resolves every still-Reserved admission matching the filters; returns how many left Reserved state.</summary>
    Task<int> ResolveOutstandingAsync(PaidCallKind? kind, long? requestId, CancellationToken ct);
}

public static class PaidCallScopeKeys
{
    public static string CanonicalIdentifier(McaRequest request) =>
        (request.Cin ?? request.Llpin)?.Trim().ToUpperInvariant() is { Length: > 0 } id ? id : $"REQUEST-{request.RequestId}";

    /// <summary>Order- and case-insensitive: the same keyword set always hashes to the same scope.</summary>
    public static string LitigationSearch(string canonicalIdentifier, IEnumerable<string> keywordValues)
    {
        var normalized = string.Join("\n", keywordValues
            .Select(k => k.Trim().ToUpperInvariant())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"search|{canonicalIdentifier}|{hash}";
    }

    /// <summary>Scoped to the snapshot being analysed; a request with no completed snapshot yet falls back to
    /// the request itself so a click is still deduped while in flight.</summary>
    public static string LitigationAnalysis(long? originSnapshotId, long requestId) =>
        originSnapshotId is { } s ? $"analysis|{s}" : $"analysis|request:{requestId}";

    public static string ReferenceUnlock(string canonicalIdentifier) => $"unlock|{canonicalIdentifier}";
}

/// <summary>Admission is one transaction with a fixed lock order — ledger insert → scope → counter — shared
/// by commit and release, so concurrent admissions and resolutions cannot deadlock. The scope claim and the
/// counter slot are each a single conditional UPDATE: SQL Server serialises writers on the row and re-checks
/// the predicate after the first writer commits, which is what makes "exactly one winner" hold across
/// processes and instances.</summary>
public sealed class PaidCallAdmissionService(
    AppDbContext db, IOptionsMonitor<PipelineOptions> options, TimeProvider time, ILogger<PaidCallAdmissionService> logger) : IPaidCallAdmission
{
    public async Task<AdmissionResult> TryAdmitAsync(PaidCallAdmissionRequest r, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;
        var dayKey = DayKeyFor(now, opts.CapTimeZone);
        var manual = r.Trigger == PaidCallTrigger.Manual;
        var cap = CapFor(r.Kind, opts.Caps);
        var cutoff = FreshnessCutoff(r.Kind, now);

        await EnsureScopeAsync(r.Kind, r.ScopeKey, ct);
        await EnsureCounterAsync(r.Kind, dayKey, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var admission = new PaidCallAdmission
        {
            Kind = r.Kind, ScopeKey = r.ScopeKey, DayKey = dayKey, Trigger = r.Trigger,
            RequestId = r.RequestId, ClientId = r.ClientId, State = PaidCallAdmissionState.Reserved,
            ReservedUtc = now, CorrelationId = r.CorrelationId
        };
        db.PaidCallAdmissions.Add(admission);
        await db.SaveChangesAsync(ct);
        var id = admission.PaidCallAdmissionId;
        db.Entry(admission).State = EntityState.Detached;

        var claimed = await db.SpendScopes
            .Where(s => s.Kind == r.Kind && s.ScopeKey == r.ScopeKey && s.ActiveAdmissionId == null
                && (manual || s.LastCommittedUtc == null || s.LastCommittedUtc < cutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActiveAdmissionId, id), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            var inFlight = await db.SpendScopes.AnyAsync(s => s.Kind == r.Kind && s.ScopeKey == r.ScopeKey && s.ActiveAdmissionId != null, ct);
            return new AdmissionResult(null, inFlight ? AdmissionDenial.InFlight : AdmissionDenial.Fresh, now);
        }

        // Manual is counted but never blocked by the cap (owner decision, plan §4.0).
        var counted = await db.SpendCounters
            .Where(c => c.Kind == r.Kind && c.DayKey == dayKey && (manual || c.Used < cap))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Used, c => c.Used + 1), ct);
        if (counted == 0)
        {
            await tx.RollbackAsync(ct);
            return new AdmissionResult(null, AdmissionDenial.CostCapReached, now);
        }

        await tx.CommitAsync(ct);
        logger.LogInformation("Admitted {Trigger} {Kind} call {AdmissionId} for scope {ScopeKey}", r.Trigger, r.Kind, id, r.ScopeKey);
        return new AdmissionResult(id, null, now);
    }

    public Task SetReferenceAsync(long admissionId, long referenceId, CancellationToken ct) =>
        db.PaidCallAdmissions
            .Where(a => a.PaidCallAdmissionId == admissionId && a.State == PaidCallAdmissionState.Reserved)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReferenceId, referenceId), ct);

    public async Task<bool> CommitAsync(long admissionId, DateTime committedUtc, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var moved = await db.PaidCallAdmissions
            .Where(a => a.PaidCallAdmissionId == admissionId && a.State == PaidCallAdmissionState.Reserved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, PaidCallAdmissionState.Committed)
                .SetProperty(a => a.ResolvedUtc, now), ct);
        if (moved == 0) { await tx.RollbackAsync(ct); return false; }

        var a = await db.PaidCallAdmissions.AsNoTracking()
            .Where(x => x.PaidCallAdmissionId == admissionId).Select(x => new { x.Kind, x.ScopeKey }).SingleAsync(ct);
        // LastCommittedUtc only ever moves forward, so an out-of-order commit can't shorten a cooldown.
        await db.SpendScopes.Where(s => s.Kind == a.Kind && s.ScopeKey == a.ScopeKey)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastCommittedUtc, x => x.LastCommittedUtc == null || x.LastCommittedUtc < committedUtc ? committedUtc : x.LastCommittedUtc)
                .SetProperty(x => x.ActiveAdmissionId, x => x.ActiveAdmissionId == admissionId ? null : x.ActiveAdmissionId), ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Committed paid-call admission {AdmissionId} ({Kind} {ScopeKey})", admissionId, a.Kind, a.ScopeKey);
        return true;
    }

    public async Task<bool> ReleaseAsync(long admissionId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var moved = await db.PaidCallAdmissions
            .Where(a => a.PaidCallAdmissionId == admissionId && a.State == PaidCallAdmissionState.Reserved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, PaidCallAdmissionState.Released)
                .SetProperty(a => a.ResolvedUtc, now), ct);
        if (moved == 0) { await tx.RollbackAsync(ct); return false; }

        var a = await db.PaidCallAdmissions.AsNoTracking()
            .Where(x => x.PaidCallAdmissionId == admissionId).Select(x => new { x.Kind, x.ScopeKey, x.DayKey }).SingleAsync(ct);
        await db.SpendScopes.Where(s => s.Kind == a.Kind && s.ScopeKey == a.ScopeKey && s.ActiveAdmissionId == admissionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActiveAdmissionId, (long?)null), ct);
        // The slot goes back to the admission's own day, never "today".
        await db.SpendCounters.Where(c => c.Kind == a.Kind && c.DayKey == a.DayKey)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Used, c => c.Used > 0 ? c.Used - 1 : 0), ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Released paid-call admission {AdmissionId} ({Kind} {ScopeKey})", admissionId, a.Kind, a.ScopeKey);
        return true;
    }

    public async Task<AdmissionResolution> ResolveAsync(long admissionId, CancellationToken ct)
    {
        var a = await db.PaidCallAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.PaidCallAdmissionId == admissionId, ct);
        if (a is null || a.State != PaidCallAdmissionState.Reserved) return AdmissionResolution.AlreadyResolved;

        var now = time.GetUtcNow().UtcDateTime;
        var ttlExpired = a.ReservedUtc < now.AddMinutes(-options.CurrentValue.ReservationTtlMinutes);
        var decision = a.Kind switch
        {
            PaidCallKind.LitigationSearch => await DecideSearchAsync(a, ttlExpired, ct),
            PaidCallKind.LitigationAnalysis => await DecideAnalysisAsync(a, now, ttlExpired, ct),
            // Unlock execution arrives with #266; until then only a never-linked reservation can be released.
            _ => a.ReferenceId is null && ttlExpired ? Decision.Release() : Decision.Wait()
        };

        return decision.Kind switch
        {
            DecisionKind.Commit => await CommitAsync(a.PaidCallAdmissionId, decision.At, ct) ? AdmissionResolution.Committed : AdmissionResolution.AlreadyResolved,
            DecisionKind.Release => await ReleaseAsync(a.PaidCallAdmissionId, ct) ? AdmissionResolution.Released : AdmissionResolution.AlreadyResolved,
            _ => AdmissionResolution.StillReserved
        };
    }

    public async Task<int> ResolveOutstandingAsync(PaidCallKind? kind, long? requestId, CancellationToken ct)
    {
        var ids = await db.PaidCallAdmissions.AsNoTracking()
            .Where(a => a.State == PaidCallAdmissionState.Reserved
                && (kind == null || a.Kind == kind) && (requestId == null || a.RequestId == requestId))
            .OrderBy(a => a.PaidCallAdmissionId)
            .Select(a => a.PaidCallAdmissionId)
            .ToListAsync(ct);
        var resolved = 0;
        foreach (var id in ids)
            if (await ResolveAsync(id, ct) is AdmissionResolution.Committed or AdmissionResolution.Released)
                resolved++;
        return resolved;
    }

    /// <summary>Fail closed: <see cref="LitigationSearchJob.RegistrationAttemptedUtc"/> is written before the
    /// vendor call, so once it is set the search may have happened — including the "attempted but no vendor
    /// job id" unknown outcome. Released only when provably nothing was attempted for this admission.</summary>
    private async Task<Decision> DecideSearchAsync(PaidCallAdmission a, bool ttlExpired, CancellationToken ct)
    {
        if (a.ReferenceId is { } jobId)
        {
            var job = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.LitigationSearchJobId == jobId)
                .Select(j => new { j.RegistrationAttemptedUtc, j.Status }).FirstOrDefaultAsync(ct);
            if (job?.RegistrationAttemptedUtc is { } attempted) return Decision.Commit(attempted);
            if (job is null || job.Status is LitigationSearchJobStatus.Completed or LitigationSearchJobStatus.Failed) return Decision.Release();
            return Decision.Wait();
        }

        // Never linked: a crash between creating/resetting the job and SetReferenceAsync. Find the job the
        // admission may have produced before concluding nothing was bought.
        var requestJob = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == a.RequestId)
            .Select(j => new { j.RegistrationAttemptedUtc, j.Status }).FirstOrDefaultAsync(ct);
        if (requestJob?.RegistrationAttemptedUtc is { } at && at >= a.ReservedUtc) return Decision.Commit(at);
        if (!ttlExpired) return Decision.Wait();
        if (requestJob is { Status: LitigationSearchJobStatus.Pending, RegistrationAttemptedUtc: null }) return Decision.Wait();
        return Decision.Release();
    }

    /// <summary>Nothing in the analysis pipeline records token counts, so the earliest durable evidence that a
    /// model call may have been made is the run's first claim (<c>AttemptCount &gt; 0</c>). Committing there is
    /// conservative: a run that turns out to make no call is still counted.</summary>
    private async Task<Decision> DecideAnalysisAsync(PaidCallAdmission a, DateTime now, bool ttlExpired, CancellationToken ct)
    {
        if (a.ReferenceId is { } runId)
        {
            var run = await db.LitigationAiAnalysisRuns.AsNoTracking().Where(r => r.LitigationAiAnalysisRunId == runId)
                .Select(r => new { r.AttemptCount, r.StartedUtc, r.Status }).FirstOrDefaultAsync(ct);
            if (run is { AttemptCount: > 0 }) return Decision.Commit(run.StartedUtc ?? now);
            if (run is null || IsTerminal(run.Status)) return Decision.Release();
            return Decision.Wait();
        }

        var produced = await db.LitigationAiAnalysisRuns.AsNoTracking()
            .Where(r => r.RequestId == a.RequestId && r.CreatedUtc >= a.ReservedUtc)
            .OrderByDescending(r => r.LitigationAiAnalysisRunId)
            .Select(r => new { r.AttemptCount, r.StartedUtc, r.Status }).FirstOrDefaultAsync(ct);
        if (produced is { AttemptCount: > 0 }) return Decision.Commit(produced.StartedUtc ?? now);
        if (!ttlExpired) return Decision.Wait();
        if (produced is not null && !IsTerminal(produced.Status)) return Decision.Wait();
        return Decision.Release();
    }

    private static bool IsTerminal(LitigationAiAnalysisRunStatus s) =>
        s is LitigationAiAnalysisRunStatus.Completed or LitigationAiAnalysisRunStatus.CompletedWithErrors or LitigationAiAnalysisRunStatus.Failed;

    public static DateOnly DayKeyFor(DateTime utcNow, string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz));
    }

    private static int CapFor(PaidCallKind kind, PipelineCapOptions caps) => kind switch
    {
        PaidCallKind.LitigationSearch => caps.LitigationSearchPerDay,
        PaidCallKind.LitigationAnalysis => caps.LitigationAnalysisPerDay,
        PaidCallKind.ReferenceUnlock => caps.UnlockPerDay,
        _ => 0
    };

    /// <summary>Rolling windows evaluated against LastCommittedUtc (plan §4.0 table) — never date buckets,
    /// which would let two purchases straddle a boundary. Analysis has no window: once committed, a scope is
    /// never auto-admitted again.</summary>
    private static DateTime FreshnessCutoff(PaidCallKind kind, DateTime now) => kind switch
    {
        PaidCallKind.LitigationSearch => now.AddDays(-7),
        PaidCallKind.ReferenceUnlock => now.AddMonths(-12),
        _ => DateTime.MinValue
    };

    private async Task EnsureScopeAsync(PaidCallKind kind, string scopeKey, CancellationToken ct)
    {
        var k = kind.ToString();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
IF NOT EXISTS (SELECT 1 FROM SpendScopes WHERE Kind = {k} AND ScopeKey = {scopeKey})
INSERT INTO SpendScopes (Kind, ScopeKey, ActiveAdmissionId, LastCommittedUtc) VALUES ({k}, {scopeKey}, NULL, NULL)", ct);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601) { /* a concurrent caller created it */ }
    }

    private async Task EnsureCounterAsync(PaidCallKind kind, DateOnly dayKey, CancellationToken ct)
    {
        var k = kind.ToString();
        var day = dayKey.ToDateTime(TimeOnly.MinValue);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
IF NOT EXISTS (SELECT 1 FROM SpendCounters WHERE Kind = {k} AND DayKey = CAST({day} AS date))
INSERT INTO SpendCounters (Kind, DayKey, Used) VALUES ({k}, CAST({day} AS date), 0)", ct);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601) { }
    }

    private enum DecisionKind { Wait, Commit, Release }

    private readonly record struct Decision(DecisionKind Kind, DateTime At)
    {
        public static Decision Wait() => new(DecisionKind.Wait, default);
        public static Decision Release() => new(DecisionKind.Release, default);
        public static Decision Commit(DateTime at) => new(DecisionKind.Commit, at);
    }
}
