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
    private int MaxAttempts => config.GetValue("CalculationAssurance:AiAuditMaxAttempts", 3);
    private int MaxLedgerRowsPerCall => config.GetValue("CalculationAssurance:AiAuditMaxLedgerRowsPerCall", 150);

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
    /// AttemptCount, so a retry after a mid-call crash is counted the same as a caught exception), the
    /// Vertex AI call, deterministic validation, and persistence.</summary>
    public async Task RunAuditAsync(long auditRunId, CancellationToken ct)
    {
        var claimed = await db.CalculationAiAuditRuns
            .Where(r => r.CalculationAiAuditRunId == auditRunId && r.Status == CalculationAiAuditRunStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, CalculationAiAuditRunStatus.InProgress)
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                .SetProperty(r => r.StartedUtc, DateTime.UtcNow), ct);
        if (claimed == 0)
            return;

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
            run.ValidatedCandidateCountAccepted = validation.Accepted.Count;
            run.RejectedCandidatesJson = validation.Rejected.Count > 0 ? JsonSerializer.Serialize(validation.Rejected) : null;

            foreach (var candidate in validation.Accepted)
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
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "AI audit run {RunId} completed for snapshot {SnapshotId}: {Accepted} candidate(s) accepted, {Rejected} rejected.",
                auditRunId, run.CalculationAuditSnapshotId, validation.Accepted.Count, validation.Rejected.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI audit call failed for run {RunId} (attempt {Attempt})", auditRunId, run.AttemptCount);

            if (run.AttemptCount < MaxAttempts)
            {
                await db.CalculationAiAuditRuns.Where(r => r.CalculationAiAuditRunId == auditRunId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, CalculationAiAuditRunStatus.Pending)
                        .SetProperty(r => r.FailureReason, ex.Message), ct);
                queue.Enqueue(auditRunId);
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

    /// <summary>Re-enqueues every Pending/InProgress row on startup. Unlike AnalysisOrchestrator's
    /// recovery (which only needs to catch a genuine mid-run crash), this also has to cover the ordinary
    /// case of a Pending row whose in-memory queue entry was simply lost to a restart — the channel never
    /// persists, so "was queued" and "crashed mid-run" are indistinguishable from the database's point of
    /// view and are recovered identically.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        await db.CalculationAiAuditRuns
            .Where(r => r.Status == CalculationAiAuditRunStatus.InProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, CalculationAiAuditRunStatus.Pending), ct);

        var pendingIds = await db.CalculationAiAuditRuns
            .Where(r => r.Status == CalculationAiAuditRunStatus.Pending)
            .Select(r => r.CalculationAiAuditRunId)
            .ToListAsync(ct);

        foreach (var id in pendingIds)
            queue.Enqueue(id);

        return pendingIds.Count;
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
