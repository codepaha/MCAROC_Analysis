using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MCAROC_Analysis.Services;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Drives one <see cref="LitigationSearchJob"/> end to end: atomic claim, authenticate, register
/// (skipped if already confirmed registered), poll report/job/{id} until a terminal payload arrives or the
/// poll budget is exhausted, then retain the raw bytes for #242 to parse.
///
/// Every mutation after the initial claim is its own <c>ExecuteUpdateAsync</c> conditioned on
/// <c>(jobId, thisAttempt'sLeaseToken, LeaseExpiresUtc > now)</c> — not just the reusable
/// <c>LeaseOwner</c> string. If this attempt's lease has since been reclaimed (its own lease expired and
/// recovery reassigned the job to a newer claim), every one of its writes affects 0 rows and is treated as
/// "stop processing immediately" rather than silently clobbering whatever the newer claim has already
/// written — LeaseOwner alone cannot provide this, since the same process reuses it across every claim it
/// ever makes and carries no per-claim identity.
///
/// Registration is handled the same way <c>CalculationAiAuditOrchestrator</c> treats an external call that
/// might have partially succeeded: <see cref="LitigationSearchJob.RegistrationAttemptedUtc"/> is persisted
/// *before* the vendor call, and if a later attempt finds it set with no confirmed
/// <see cref="LitigationSearchJob.VendorJobId"/>, it fails closed instead of retrying — the confirmed BPR
/// contract has no idempotency key and no way to look up a prior registration, so guessing risks a duplicate
/// vendor-side search.</summary>
public sealed class LitigationSearchJobService(
    AppDbContext db,
    BprLitigationClient client,
    LitigationSearchQueue queue,
    LitigationCasePersistenceQueue casePersistenceQueue,
    LitigationCasePersistenceService casePersistenceService,
    IOptions<BprLitigationOptions> options,
    ILogger<LitigationSearchJobService> logger)
{
    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly BprLitigationOptions _opts = options.Value;

    /// <summary>Margin added on top of the configured poll timeout to get the lease duration — deliberately
    /// derived rather than independently configured, so the two numbers can't drift apart (same reasoning as
    /// CalculationAiAuditOrchestrator.ComputeLeaseSeconds).</summary>
    private const int LeaseMarginSeconds = 120;

    private int LeaseSeconds => _opts.PollTimeoutMinutes * 60 + LeaseMarginSeconds;

    // ── Creation ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a fresh job, or — an explicit, auditable rerun action, never an implicit side effect
    /// of some other flow — resets one that has reached a terminal state (<see cref="LitigationSearchJob.IsTerminal"/>)
    /// or was created but never touched BPR at all (Pending, no <see cref="LitigationSearchJob.VendorJobId"/>,
    /// no <see cref="LitigationSearchJob.RegistrationAttemptedUtc"/>). The caller enqueues the returned job —
    /// separated so a controller can save the request first. <paramref name="keywords"/> must already be the
    /// approved plan (see <see cref="LitigationKeywordPlanner"/>) — this service never invents search terms.
    ///
    /// Rejects resetting a job that is Authenticating/Registering/Polling, or Pending with evidence a vendor
    /// call may already be in flight (a set <c>VendorJobId</c> or <c>RegistrationAttemptedUtc</c>): clearing
    /// those fields while the original BPR search may still be running would let a subsequent worker register
    /// a second, duplicate vendor-side search for the same company under the same request.</summary>
    /// <exception cref="InvalidOperationException">The existing job is non-terminal and may still have an
    /// active BPR call in flight.</exception>
    public async Task<LitigationSearchJob> CreateOrResetJobAsync(
        long requestId, IReadOnlyList<LitigationKeyword> keywords, string entityType, string applicationCustomerId, CancellationToken ct)
    {
        if (keywords.Count == 0) throw new ArgumentException("At least one approved keyword is required.", nameof(keywords));

        var job = await db.LitigationSearchJobs.FirstOrDefaultAsync(j => j.RequestId == requestId, ct);
        if (job is not null && !CanReset(job))
            throw new InvalidOperationException(
                $"Litigation search job {job.LitigationSearchJobId} for request {requestId} is {job.Status} " +
                (job.VendorJobId is not null ? $"with vendor job {job.VendorJobId} " : "") +
                "and may still have a BPR search in flight — resetting now could register a duplicate " +
                "vendor-side search. Wait for it to reach a terminal state (Completed/Failed) before rerunning.");

        if (job is null)
        {
            job = new LitigationSearchJob { RequestId = requestId, CreatedUtc = DateTime.UtcNow };
            db.LitigationSearchJobs.Add(job);
        }

        job.EntityType = entityType;
        job.ApplicationCustomerId = applicationCustomerId;
        job.KeywordsJson = JsonSerializer.Serialize(keywords.Select(k => new { value = k.Value, source = k.Source.ToString() }));
        job.Status = LitigationSearchJobStatus.Pending;
        job.ProgressPercent = 0;
        job.StatusMessage = "Queued.";
        job.FailureReason = null;
        job.VendorJobId = null;
        job.RegisteredUtc = null;
        job.RegistrationAttemptedUtc = null;
        job.NextAttemptUtc = null;
        job.LeaseToken = null;
        job.CompletedUtc = null;
        await db.SaveChangesAsync(ct);
        return job;
    }

    /// <summary>True only when there is no plausible way BPR already has, or might still create, an active
    /// vendor-side search for this job: it is terminal, or it is Pending with neither a confirmed vendor job
    /// id nor an unresolved registration attempt.</summary>
    private static bool CanReset(LitigationSearchJob job) =>
        job.IsTerminal ||
        (job.Status == LitigationSearchJobStatus.Pending && job.VendorJobId is null && job.RegistrationAttemptedUtc is null);

    // ── Processing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Atomic Pending→Authenticating claim (bumping AttemptCount, minting a fresh
    /// <see cref="LitigationSearchJob.LeaseToken"/>, and taking a durable lease sized for the whole poll
    /// budget), then authenticate → register (unless already confirmed registered) → poll. The claim also
    /// requires NextAttemptUtc to have passed, so a backoff-scheduled retry that reaches the queue early is
    /// declined here instead of processed ahead of schedule.</summary>
    public async Task ProcessAsync(long jobId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseToken = Guid.NewGuid();
        var claimed = await db.LitigationSearchJobs
            .Where(j => j.LitigationSearchJobId == jobId && j.Status == LitigationSearchJobStatus.Pending
                && (j.NextAttemptUtc == null || j.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, LitigationSearchJobStatus.Authenticating)
                .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1)
                .SetProperty(j => j.LeaseOwner, LeaseOwnerId)
                .SetProperty(j => j.LeaseToken, leaseToken)
                .SetProperty(j => j.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds))
                .SetProperty(j => j.StartedUtc, now)
                .SetProperty(j => j.StatusMessage, "Authenticating with BPR."), ct);
        if (claimed == 0)
        {
            var current = await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == jobId)
                .Select(j => new { j.Status, j.NextAttemptUtc }).FirstOrDefaultAsync(ct);
            if (current is { Status: LitigationSearchJobStatus.Pending, NextAttemptUtc: { } next } && next > now)
                ScheduleRetry(jobId, next, ct);
            return;
        }

        // Loaded once, un-tracked: everything after this point is a read-only snapshot for this attempt's own
        // decisions. Every actual write to the row is its own lease-guarded ExecuteUpdateAsync below, never a
        // mutation of this object or a tracked SaveChangesAsync — that is what makes the fencing meaningful.
        var job = await db.LitigationSearchJobs.AsNoTracking().FirstAsync(j => j.LitigationSearchJobId == jobId, ct);

        try
        {
            if (job.VendorJobId is null && job.RegistrationAttemptedUtc is not null)
                throw new LitigationRegistrationAmbiguousException(
                    $"A previous registration attempt for job {jobId} at {Ist.Format(job.RegistrationAttemptedUtc, "d MMM yyyy HH:mm:ss")} never " +
                    "confirmed success or failure with BPR — the confirmed contract has no idempotency key and no " +
                    "way to look up a prior registration, so retrying risks a duplicate vendor-side search. Manual " +
                    "reconciliation required: check BPR directly, then set VendorJobId or clear RegistrationAttemptedUtc.");

            var token = await client.AuthenticateAsync(ct);

            var vendorJobId = job.VendorJobId;
            if (vendorJobId is null)
            {
                var claimedRegistering = await LeaseGuarded(jobId, leaseToken)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, LitigationSearchJobStatus.Registering)
                        .SetProperty(j => j.StatusMessage, "Registering search with BPR.")
                        .SetProperty(j => j.RegistrationAttemptedUtc, DateTime.UtcNow), ct);
                if (claimedRegistering == 0) throw new LitigationSearchJobLeaseLostException(jobId);

                var keywords = ParseKeywordValues(job.KeywordsJson);
                vendorJobId = await client.RegisterJobAsync(token, keywords, job.EntityType, job.ApplicationCustomerId, ct);

                // Persisted the instant BPR confirms success — this is the narrowest the crash window between
                // "vendor accepted the call" and "we know it" can be made without a vendor idempotency key.
                var registeredVendorJobId = vendorJobId;
                var claimedRegistered = await LeaseGuarded(jobId, leaseToken)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.VendorJobId, registeredVendorJobId)
                        .SetProperty(j => j.RegisteredUtc, DateTime.UtcNow), ct);
                if (claimedRegistered == 0) throw new LitigationSearchJobLeaseLostException(jobId);
            }

            var claimedPolling = await LeaseGuarded(jobId, leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, LitigationSearchJobStatus.Polling)
                    .SetProperty(j => j.StatusMessage, "Waiting for BPR to complete the search.")
                    .SetProperty(j => j.ProgressPercent, 25), ct);
            if (claimedPolling == 0) throw new LitigationSearchJobLeaseLostException(jobId);

            await PollUntilCompleteAsync(jobId, leaseToken, vendorJobId, job.RegisteredUtc, token, ct);
        }
        catch (LitigationSearchJobLeaseLostException)
        {
            // Another worker has since reclaimed this job (this attempt's lease expired and recovery
            // reassigned it) — we must stop touching the row entirely, including retry/backoff bookkeeping,
            // which would itself be an unguarded write racing the new owner.
            logger.LogWarning(
                "Litigation search job {JobId} lost its lease mid-processing (attempt {Attempt}) — another worker has taken over.",
                jobId, job.AttemptCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BPR litigation search failed for job {JobId} (attempt {Attempt})", jobId, job.AttemptCount);

            // An ambiguous-registration failure must never be auto-retried — see LitigationRegistrationAmbiguousException.
            if (ex is not LitigationRegistrationAmbiguousException && job.AttemptCount < _opts.MaxAttempts)
            {
                var nextAttemptUtc = DateTime.UtcNow + BackoffDelay(job.AttemptCount);
                var failureMessage = ex.Message;
                var claimedRetry = await LeaseGuarded(jobId, leaseToken)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, LitigationSearchJobStatus.Pending)
                        .SetProperty(j => j.NextAttemptUtc, nextAttemptUtc)
                        .SetProperty(j => j.StatusMessage, "Retrying after a failed attempt.")
                        .SetProperty(j => j.FailureReason, failureMessage), ct);
                if (claimedRetry > 0) ScheduleRetry(jobId, nextAttemptUtc, ct);
            }
            else
            {
                var failureMessage = ex.Message;
                await LeaseGuarded(jobId, leaseToken)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, LitigationSearchJobStatus.Failed)
                        .SetProperty(j => j.StatusMessage, "Failed.")
                        .SetProperty(j => j.FailureReason, failureMessage)
                        .SetProperty(j => j.CompletedUtc, DateTime.UtcNow), ct);
                logger.LogError(ex, "BPR litigation search job {JobId} failed permanently after {Attempts} attempts", jobId, job.AttemptCount);
            }
        }
    }

    private async Task PollUntilCompleteAsync(
        long jobId, Guid leaseToken, string vendorJobId, DateTime? registeredUtc, string token, CancellationToken ct)
    {
        var deadlineUtc = (registeredUtc ?? DateTime.UtcNow).AddMinutes(_opts.PollTimeoutMinutes);

        while (true)
        {
            var result = await client.GetReportAsync(token, vendorJobId, ct);

            switch (result.Status)
            {
                case BprReportPollStatus.Completed:
                {
                    var bytes = result.Bytes!;
                    var format = result.Format;
                    var hash = ComputeHash(bytes);
                    var retrievedUtc = DateTime.UtcNow;

                    // Snapshot admission and the job's own lease-guarded completion happen in ONE
                    // transaction, not as two independent commits. Without this, a worker whose lease
                    // expires between the two writes could still leave its snapshot committed and Pending
                    // even though its own completion update lost the race and threw
                    // LitigationSearchJobLeaseLostException below — LitigationCasePersistenceService's
                    // RecoverStaleWorkAsync finds and imports snapshots independent of the job's own status,
                    // so that orphaned snapshot would still get persisted into cases/provenance on a later
                    // sweep, publishing a report whose producing attempt was fenced out. Rolling both writes
                    // back together when the lease check fails means the snapshot never becomes visible to
                    // any other connection (READ COMMITTED) unless the completion that vouches for it also
                    // durably committed alongside it — a raw process crash mid-transaction gets the same
                    // all-or-nothing outcome for free (SQL Server rolls back an uncommitted transaction on
                    // its own), so this is strictly safer than the previous two-independent-writes shape for
                    // both the fenced-out-live-worker case and the crash case.
                    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

                    var snapshotId = await casePersistenceService.EnsureSnapshotAsync(jobId, hash, format, bytes, retrievedUtc, ct);

                    var statusMessage = $"Report received ({format}).";
                    var claimedCompleted = await LeaseGuarded(jobId, leaseToken)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, LitigationSearchJobStatus.Completed)
                            .SetProperty(j => j.ReportFormat, format)
                            .SetProperty(j => j.RawReportBytes, bytes)
                            .SetProperty(j => j.RawReportByteLength, bytes.LongLength)
                            .SetProperty(j => j.RawResponseHash, hash)
                            .SetProperty(j => j.ProgressPercent, 100)
                            .SetProperty(j => j.StatusMessage, statusMessage)
                            .SetProperty(j => j.FailureReason, (string?)null)
                            .SetProperty(j => j.CompletedUtc, DateTime.UtcNow), ct);
                    if (claimedCompleted == 0)
                    {
                        await tx.RollbackAsync(ct); // undoes the snapshot admission too — no orphan left behind
                        throw new LitigationSearchJobLeaseLostException(jobId);
                    }

                    await tx.CommitAsync(ct);
                    logger.LogInformation(
                        "BPR litigation search job {JobId} completed: {Format}, {ByteCount} bytes.", jobId, format, bytes.LongLength);
                    casePersistenceQueue.Enqueue(snapshotId);
                    return;
                }

                case BprReportPollStatus.Failed:
                    throw new BprLitigationException(result.Message ?? "BPR report retrieval failed with no reason given.");

                case BprReportPollStatus.Pending:
                default:
                    if (DateTime.UtcNow >= deadlineUtc)
                        throw new BprLitigationException(
                            $"BPR report for vendor job {vendorJobId} did not complete within {_opts.PollTimeoutMinutes} minutes.");

                    var message = result.Message ?? "Waiting for BPR to complete the search.";
                    var claimedProgress = await LeaseGuarded(jobId, leaseToken)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.StatusMessage, message), ct);
                    if (claimedProgress == 0) throw new LitigationSearchJobLeaseLostException(jobId);

                    await Task.Delay(TimeSpan.FromSeconds(_opts.PollIntervalSeconds), ct);
                    continue;
            }
        }
    }

    /// <summary>The query every write to an already-claimed job must go through: it requires the exact
    /// <see cref="Guid"/> this attempt was claimed with, and an unexpired lease — not just
    /// <c>LeaseOwner</c>, which is reused across every claim by this process and so cannot distinguish "still
    /// me" from "a stale attempt by me that lost its lease." An <c>ExecuteUpdateAsync</c> against this query
    /// returning 0 means the row no longer matches (fenced out); callers must treat that as "stop processing
    /// immediately," never as an ordinary failure to retry.</summary>
    private IQueryable<LitigationSearchJob> LeaseGuarded(long jobId, Guid leaseToken)
    {
        var now = DateTime.UtcNow;
        return db.LitigationSearchJobs
            .Where(j => j.LitigationSearchJobId == jobId && j.LeaseToken == leaseToken && j.LeaseExpiresUtc != null && j.LeaseExpiresUtc > now);
    }

    /// <summary>Exponential backoff, capped at 5 minutes — identical formula to
    /// <c>CalculationAiAuditOrchestrator.BackoffDelay</c>, kept local rather than shared because the two
    /// types have no other coupling and a shared helper would be a premature abstraction over one line of
    /// math.</summary>
    internal static TimeSpan BackoffDelay(int attemptCount) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(3, Math.Max(0, attemptCount - 1))));

    private void ScheduleRetry(long jobId, DateTime nextAttemptUtc, CancellationToken ct)
    {
        var delay = nextAttemptUtc - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            queue.Enqueue(jobId);
            return;
        }

        var capturedQueue = queue;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                capturedQueue.Enqueue(jobId);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — the row stays Pending with NextAttemptUtc set; the next startup's
                // recovery sweep re-schedules or re-enqueues it.
            }
        }, ct);
    }

    /// <summary>Re-enqueues eligible Pending/in-flight rows on startup. An Authenticating/Registering/Polling
    /// row is only reset if its lease has actually expired (or predates this column) — a still-genuinely-
    /// running search is left alone rather than requeued into a duplicate BPR registration. A fresh claim
    /// mints its own new LeaseToken, so this reset does not need to touch that column itself.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var inFlightStatuses = new[]
        {
            LitigationSearchJobStatus.Authenticating, LitigationSearchJobStatus.Registering, LitigationSearchJobStatus.Polling
        };

        await db.LitigationSearchJobs
            .Where(j => inFlightStatuses.Contains(j.Status) && (j.LeaseExpiresUtc == null || j.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, LitigationSearchJobStatus.Pending)
                .SetProperty(j => j.StatusMessage, "Resuming after an application restart."), ct);

        var pending = await db.LitigationSearchJobs
            .Where(j => j.Status == LitigationSearchJobStatus.Pending)
            .Select(j => new { j.LitigationSearchJobId, j.NextAttemptUtc })
            .ToListAsync(ct);

        foreach (var p in pending)
        {
            if (p.NextAttemptUtc is { } next && next > now)
                ScheduleRetry(p.LitigationSearchJobId, next, ct);
            else
                queue.Enqueue(p.LitigationSearchJobId);
        }

        return pending.Count;
    }

    private static IReadOnlyList<string> ParseKeywordValues(string keywordsJson)
    {
        using var document = JsonDocument.Parse(keywordsJson);
        return document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("value").GetString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
    }

    private static string ComputeHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>Raised when a guarded write finds this attempt's lease has been reclaimed — the row now belongs
/// to a newer claim and must not be touched further.</summary>
public sealed class LitigationSearchJobLeaseLostException(long jobId)
    : Exception($"Litigation search job {jobId} lost its lease mid-processing — another worker has taken over.");

/// <summary>Raised when a job's prior registration attempt never confirmed success or failure. Always
/// terminal — never auto-retried — because the confirmed BPR contract gives no safe way to tell whether
/// retrying would create a duplicate vendor-side search.</summary>
public sealed class LitigationRegistrationAmbiguousException(string message) : Exception(message);
