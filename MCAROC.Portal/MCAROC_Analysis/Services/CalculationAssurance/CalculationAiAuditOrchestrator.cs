using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Drives the #164 AI second-line review: get-or-create the one CalculationAiAuditRun per
/// snapshot and enqueue it (called from AnalysisOrchestrator, after ledger + deterministic checks have
/// already persisted synchronously), then — on the worker side — atomic claim, the bounded Vertex AI
/// call, deterministic validation, and persistence of surviving candidates as Open/Severity=null
/// CalculationDiscrepancy rows. Mirrors AnalysisOrchestrator's claim/recovery shape and
/// FilingBatchProcessor's retry-to-a-max-then-terminal pattern.
///
/// No candidate accepted here is ever written with a non-null Severity, and no status this orchestrator
/// can set (including SkippedAiUnavailable) ever creates or extends a CalculationArtifactHold by itself —
/// the AI worker is a candidate generator only, never an approver.</summary>
public class CalculationAiAuditOrchestrator(
    AppDbContext db,
    CalculationAiAuditService aiService,
    CalculationAiAuditQueue queue,
    IConfiguration config,
    ILogger<CalculationAiAuditOrchestrator> logger)
{
    /// <summary>Identifies who currently holds a claim — diagnostic only, logged alongside lease expiry;
    /// the expiry timestamp is what actually enforces correctness, not this string.</summary>
    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private int MaxAttempts => config.GetValue("CalculationAssurance:AiAuditMaxAttempts", 3);
    private int MaxLedgerRowsPerCall => config.GetValue("CalculationAssurance:AiAuditMaxLedgerRowsPerCall", 150);

    /// <summary>Generous margin beyond AiAuditTimeoutSeconds — the lease must comfortably outlive one
    /// genuine (slow) Vertex AI call, since a lease reclaimed too early is exactly the duplicate-call bug
    /// this field exists to prevent.</summary>
    private int LeaseSeconds => config.GetValue("CalculationAssurance:AiAuditLeaseSeconds", 300);

    /// <summary>Get-or-create the single CalculationAiAuditRun for this snapshot and enqueue it. A no-op
    /// with zero DB queries when Mode is Off or AiAuditEnabled is false, matching the sibling
    /// ledger/checks services' discipline exactly.</summary>
    public async Task EnqueueForSnapshotAsync(long requestId, long ingestionRunId, long analysisRunId, CancellationToken ct)
    {
        var mode = CalculationAssuranceConfig.ParseMode(config);
        if (mode == CalculationAssuranceMode.Off)
            return;
        if (!config.GetValue("CalculationAssurance:AiAuditEnabled", true))
            return;

        var snapshotId = await db.CalculationAuditSnapshots
            .Where(s => s.RequestId == requestId && s.IngestionRunId == ingestionRunId && s.AnalysisRunId == analysisRunId)
            .Select(s => (long?)s.CalculationAuditSnapshotId)
            .FirstOrDefaultAsync(ct);
        if (snapshotId is null)
        {
            logger.LogWarning(
                "AI audit requested for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) but no ledger snapshot exists yet — skipping.",
                requestId, ingestionRunId, analysisRunId);
            return;
        }

        // Idempotency: the unique index on CalculationAuditSnapshotId guarantees at most one row. An
        // existing row (Pending/InProgress/terminal) is never touched here — Pending/InProgress ones are
        // re-enqueued by RecoverStaleWorkAsync, not re-created.
        var alreadyExists = await db.CalculationAiAuditRuns.AnyAsync(r => r.CalculationAuditSnapshotId == snapshotId, ct);
        if (alreadyExists)
            return;

        var run = new CalculationAiAuditRun
        {
            CalculationAuditSnapshotId = snapshotId.Value,
            Status = CalculationAiAuditRunStatus.Pending,
            ModelId = CalculationAiAuditService.ModelId,
            PromptVersion = CalculationAiAuditPromptBuilder.PromptVersion
        };
        db.CalculationAiAuditRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            logger.LogInformation(
                "Lost a race creating the AI audit run for snapshot {SnapshotId} — another attempt already created it.", snapshotId);
            return;
        }

        queue.Enqueue(run.CalculationAiAuditRunId);
    }

    /// <summary>Processes one queued AI audit run: atomic Pending→InProgress claim (also bumping
    /// AttemptCount and taking out a durable lease, so a retry after a mid-call crash is counted the same
    /// as a caught exception), the Vertex AI call, deterministic validation, and persistence. The claim
    /// also requires NextAttemptUtc to have passed — a backoff-scheduled retry that somehow reaches the
    /// queue early (e.g. a duplicate enqueue) is declined here rather than processed ahead of schedule.</summary>
    public async Task RunAuditAsync(long auditRunId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimed = await db.CalculationAiAuditRuns
            .Where(r => r.CalculationAiAuditRunId == auditRunId && r.Status == CalculationAiAuditRunStatus.Pending
                && (r.NextAttemptUtc == null || r.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, CalculationAiAuditRunStatus.InProgress)
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                .SetProperty(r => r.LeaseOwner, LeaseOwnerId)
                .SetProperty(r => r.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds))
                .SetProperty(r => r.StartedUtc, now), ct);
        if (claimed == 0)
        {
            // Not claimed because it's already taken (fine, nothing to do) OR because it's Pending but not
            // due yet (claim predicate excludes it) — in the latter case it must be rescheduled, or a
            // backoff-window item that reached the queue too early would be silently dropped forever.
            var current = await db.CalculationAiAuditRuns.Where(r => r.CalculationAiAuditRunId == auditRunId)
                .Select(r => new { r.Status, r.NextAttemptUtc }).FirstOrDefaultAsync(ct);
            if (current is { Status: CalculationAiAuditRunStatus.Pending, NextAttemptUtc: { } next } && next > now)
                ScheduleRetry(auditRunId, next, ct);
            return;
        }

        var run = await db.CalculationAiAuditRuns.FirstAsync(r => r.CalculationAiAuditRunId == auditRunId, ct);

        try
        {
            var ledgerEntries = await db.CalculationLedgerEntries
                .Where(e => e.CalculationAuditSnapshotId == run.CalculationAuditSnapshotId)
                .ToListAsync(ct);

            var prompt = CalculationAiAuditPromptBuilder.Build(ledgerEntries, MaxLedgerRowsPerCall);
            var callResult = await aiService.CallAsync(prompt.PromptText, ct);
            if (!callResult.Success)
                throw new InvalidOperationException(callResult.FailureReason ?? "Vertex AI call failed with no reason given.");

            var validation = CalculationAiAuditValidator.Validate(callResult.RawResponse, prompt.TagMap);

            run.RawResponseJson = callResult.RawResponse;
            run.ResponseHash = ComputeHash(callResult.RawResponse);
            run.LedgerEntryCountSent = prompt.TagMap.Count;

            if (!validation.ResponseWasValid)
            {
                run.Status = CalculationAiAuditRunStatus.CompletedWithErrors;
                run.FailureReason = validation.ResponseRejectReason;
                run.RawCandidateCountReturned = 0;
                run.ValidatedCandidateCountAccepted = 0;
                run.CompletedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            run.RawCandidateCountReturned = validation.Accepted.Count + validation.Rejected.Count;
            run.RejectedCandidatesJson = validation.Rejected.Count > 0 ? JsonSerializer.Serialize(validation.Rejected) : null;

            // One candidate per ledger entry per run, even if a single valid response raised more than one
            // distinct claim about the same row — this keeps normal operation from ever hitting the
            // (AiAuditRunId, PrimaryLedgerEntryId) unique index below, which exists purely as a last-resort
            // guard against a genuine double-execution race, not as the everyday dedup mechanism.
            var deduped = validation.Accepted
                .GroupBy(c => c.LedgerEntryId)
                .Select(g => g.First())
                .ToList();
            run.ValidatedCandidateCountAccepted = deduped.Count;

            foreach (var candidate in deduped)
            {
                var discrepancy = new CalculationDiscrepancy
                {
                    CalculationAuditSnapshotId = run.CalculationAuditSnapshotId,
                    SourceType = CalculationDiscrepancySourceType.AiCandidate,
                    AiAuditRunId = run.CalculationAiAuditRunId,
                    PrimaryLedgerEntryId = candidate.LedgerEntryId,
                    ClaimSummary = BuildClaimSummary(candidate),
                    ClaimedExpectedValue = candidate.ExpectedValue,
                    ClaimedActualValue = candidate.ActualValue,
                    Status = CalculationDiscrepancyStatus.Open,
                    Severity = null, // never set from an AI suggestion directly — mechanically enforces "AI never imposes a hold"
                    CreatedUtc = DateTime.UtcNow,
                    LastUpdatedUtc = DateTime.UtcNow
                };
                db.CalculationDiscrepancies.Add(discrepancy);

                foreach (var relatedId in candidate.RelatedLedgerEntryIds.Where(id => id != candidate.LedgerEntryId).Distinct())
                {
                    db.CalculationDiscrepancyLedgerLinks.Add(new CalculationDiscrepancyLedgerLink
                    {
                        CalculationAuditSnapshotId = run.CalculationAuditSnapshotId,
                        Discrepancy = discrepancy,
                        CalculationLedgerEntryId = relatedId
                    });
                }
            }

            run.Status = CalculationAiAuditRunStatus.Completed;
            run.CompletedUtc = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Lost a genuine double-execution race on the (AiAuditRunId, PrimaryLedgerEntryId) index —
                // the winner already persisted these same candidates and marked the run Completed. Nothing
                // to do; must not fall through to the outer catch's retry logic, which would misread this
                // as a failure and needlessly re-run a call that already succeeded elsewhere.
                logger.LogInformation(
                    "Lost a race persisting AI audit candidates for run {RunId} — another attempt already completed it.", auditRunId);
                return;
            }

            logger.LogInformation(
                "AI audit run {RunId} completed for snapshot {SnapshotId}: {Accepted} candidate(s) accepted, {Rejected} rejected.",
                auditRunId, run.CalculationAuditSnapshotId, deduped.Count, validation.Rejected.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI audit call failed for run {RunId} (attempt {Attempt})", auditRunId, run.AttemptCount);

            if (run.AttemptCount < MaxAttempts)
            {
                var nextAttemptUtc = DateTime.UtcNow + BackoffDelay(run.AttemptCount);
                await db.CalculationAiAuditRuns.Where(r => r.CalculationAiAuditRunId == auditRunId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, CalculationAiAuditRunStatus.Pending)
                        .SetProperty(r => r.NextAttemptUtc, nextAttemptUtc)
                        .SetProperty(r => r.FailureReason, ex.Message), ct);
                ScheduleRetry(auditRunId, nextAttemptUtc, ct);
            }
            else
            {
                await db.CalculationAiAuditRuns.Where(r => r.CalculationAiAuditRunId == auditRunId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, CalculationAiAuditRunStatus.SkippedAiUnavailable)
                        .SetProperty(r => r.FailureReason, ex.Message)
                        .SetProperty(r => r.CompletedUtc, DateTime.UtcNow), ct);
                logger.LogError(ex, "AI audit run {RunId} skipped as unavailable after {Attempts} attempts", auditRunId, run.AttemptCount);
            }
        }
    }

    /// <summary>Exponential backoff, capped at 5 minutes: 5s, 15s, 45s for the default 3-attempt policy.
    /// Prevents the tight-loop failure mode where all attempts fire back-to-back within milliseconds.</summary>
    internal static TimeSpan BackoffDelay(int attemptCount) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(3, Math.Max(0, attemptCount - 1))));

    /// <summary>Enqueues auditRunId after the given time — immediately if it's already due. The delay task
    /// only closes over the singleton queue, never the scoped db context, so it safely outlives this
    /// orchestrator instance's scope; cancellation on shutdown just drops it; the DB's own NextAttemptUtc is
    /// what the next startup's recovery sweep uses to pick it back up.</summary>
    private void ScheduleRetry(long auditRunId, DateTime nextAttemptUtc, CancellationToken ct)
    {
        var delay = nextAttemptUtc - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            queue.Enqueue(auditRunId);
            return;
        }

        var capturedQueue = queue;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                capturedQueue.Enqueue(auditRunId);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — the row stays Pending with NextAttemptUtc set; the next startup's
                // recovery sweep re-schedules or re-enqueues it.
            }
        }, ct);
    }

    /// <summary>Re-enqueues eligible Pending/InProgress rows on startup. Unlike AnalysisOrchestrator's
    /// recovery (which only needs to catch a genuine mid-run crash), this also has to cover the ordinary
    /// case of a Pending row whose in-memory queue entry was simply lost to a restart — the channel never
    /// persists, so "was queued" and "crashed mid-run" are indistinguishable from the database's point of
    /// view and are recovered identically. Critically, an InProgress row is only reset if its lease has
    /// actually expired (or predates this column) — a still-genuinely-running call is left alone rather
    /// than requeued into a duplicate Vertex AI call for the same run. A Pending row whose backoff window
    /// hasn't elapsed yet is rescheduled for its remaining delay, not enqueued immediately.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await db.CalculationAiAuditRuns
            .Where(r => r.Status == CalculationAiAuditRunStatus.InProgress && (r.LeaseExpiresUtc == null || r.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, CalculationAiAuditRunStatus.Pending), ct);

        var pending = await db.CalculationAiAuditRuns
            .Where(r => r.Status == CalculationAiAuditRunStatus.Pending)
            .Select(r => new { r.CalculationAiAuditRunId, r.NextAttemptUtc })
            .ToListAsync(ct);

        foreach (var p in pending)
        {
            if (p.NextAttemptUtc is { } next && next > now)
                ScheduleRetry(p.CalculationAiAuditRunId, next, ct);
            else
                queue.Enqueue(p.CalculationAiAuditRunId);
        }

        return pending.Count;
    }

    private static string BuildClaimSummary(CalculationAiAuditValidator.ValidatedCandidate c) =>
        c.SuggestedSeverity is { } severity
            ? $"AI candidate ({c.ClaimType}): {c.Explanation} [AI-suggested severity: {severity}]"
            : $"AI candidate ({c.ClaimType}): {c.Explanation}";

    private static string ComputeHash(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
