using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Runs the deterministic check registry over a snapshot's already-persisted ledger and wires
/// severity to discrepancies/holds. Called from AnalysisOrchestrator.RunAnalysisAsync right after
/// CalculationLedgerService.PersistSnapshotAsync — same "before the AI call, isolated in its own
/// try/catch" discipline.
///
/// A Triggered outcome auto-creates a CONFIRMED CalculationDiscrepancy immediately: a deterministic check
/// is the authoritative confirmation the issue describes, so it never sits in a human-triage queue the
/// way an AI candidate does. Critical/Material additionally create a CalculationArtifactHold; Minor is
/// recorded only (policy default). A no-op when CalculationAssurance:Mode is Off.</summary>
public class CalculationCheckRunnerService(AppDbContext db, DossierAssembler assembler, IConfiguration config, ILogger<CalculationCheckRunnerService> logger)
{
    public async Task RunChecksAsync(long requestId, long ingestionRunId, long analysisRunId, CancellationToken ct)
    {
        var mode = CalculationAssuranceConfig.ParseMode(config);
        if (mode == CalculationAssuranceMode.Off)
            return;

        var snapshot = await db.CalculationAuditSnapshots.FirstOrDefaultAsync(
            s => s.RequestId == requestId && s.IngestionRunId == ingestionRunId && s.AnalysisRunId == analysisRunId, ct);
        if (snapshot is null)
        {
            logger.LogWarning(
                "Deterministic checks requested for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) but no ledger snapshot exists yet — skipping.",
                requestId, ingestionRunId, analysisRunId);
            return;
        }

        var alreadyRan = await db.CalculationCheckResults.AnyAsync(c => c.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId, ct);
        if (alreadyRan)
        {
            logger.LogInformation("Deterministic checks already ran for snapshot {SnapshotId} — skipping.", snapshot.CalculationAuditSnapshotId);
            return;
        }

        var ledgerEntries = await db.CalculationLedgerEntries
            .Where(e => e.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId)
            .ToListAsync(ct);

        var model = await assembler.BuildForInFlightAnalysisAsync(requestId, ingestionRunId, analysisRunId, ct);
        if (model is null)
        {
            logger.LogWarning(
                "Deterministic checks requested for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) but no matching in-flight analysis was found — skipping.",
                requestId, ingestionRunId, analysisRunId);
            return;
        }

        var context = new CalculationCheckContext(model, ledgerEntries);
        var outcomes = CalculationCheckRegistry.RunAll(context);

        var checkResults = new List<CalculationCheckResult>();
        var links = new List<CalculationCheckResultLedgerLink>();
        var discrepancies = new List<CalculationDiscrepancy>();
        var holds = new List<CalculationArtifactHold>();

        foreach (var outcome in outcomes)
        {
            var checkResult = new CalculationCheckResult
            {
                Snapshot = snapshot,
                CheckKey = outcome.CheckKey,
                CheckVersion = "1.0",
                Status = outcome.Status,
                Severity = outcome.Severity,
                DetailJson = outcome.DetailJson,
                NotEvaluatedReason = outcome.NotEvaluatedReason,
                RanUtc = DateTime.UtcNow
            };
            checkResults.Add(checkResult);

            foreach (var ledgerEntryId in outcome.RelatedLedgerEntryIds)
            {
                links.Add(new CalculationCheckResultLedgerLink
                {
                    CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
                    CheckResult = checkResult,
                    CalculationLedgerEntryId = ledgerEntryId
                });
            }

            if (outcome.Status != CalculationCheckStatus.Triggered || outcome.Severity is not { } severity)
                continue;

            // CalculationCheckOutcome.Triggered's own constructor guards this: it never allows a
            // Triggered outcome with zero related ledger entries, so RelatedLedgerEntryIds[0] is always
            // present here.
            var discrepancy = new CalculationDiscrepancy
            {
                Snapshot = snapshot,
                SourceType = CalculationDiscrepancySourceType.Deterministic,
                OriginCheckKey = outcome.CheckKey,
                PrimaryLedgerEntry = ledgerEntries.First(e => e.CalculationLedgerEntryId == outcome.RelatedLedgerEntryIds[0]),
                ClaimSummary = $"Deterministic check \"{outcome.CheckKey}\" triggered ({severity}).",
                Status = CalculationDiscrepancyStatus.Confirmed,
                Severity = severity,
                CreatedUtc = DateTime.UtcNow,
                LastUpdatedUtc = DateTime.UtcNow
            };
            discrepancies.Add(discrepancy);

            if (severity is CalculationDiscrepancySeverity.Critical or CalculationDiscrepancySeverity.Material)
            {
                holds.Add(new CalculationArtifactHold
                {
                    Snapshot = snapshot,
                    HoldReason = severity == CalculationDiscrepancySeverity.Critical
                        ? CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy
                        : CalculationArtifactHoldReason.ConfirmedMaterialDiscrepancyNoException,
                    IsActive = true,
                    SourceDiscrepancy = discrepancy,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            // Minor: discrepancy recorded, no hold — matches the issue's policy default.
        }

        db.CalculationCheckResults.AddRange(checkResults);
        db.CalculationCheckResultLedgerLinks.AddRange(links);
        db.CalculationDiscrepancies.AddRange(discrepancies);
        db.CalculationArtifactHolds.AddRange(holds);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent attempt already ran and persisted checks for this exact snapshot — same
            // atomic-race handling as CalculationLedgerService.
            logger.LogInformation(
                "Lost a race persisting deterministic check results for snapshot {SnapshotId} — another attempt already completed it.",
                snapshot.CalculationAuditSnapshotId);
            return;
        }

        logger.LogInformation(
            "Ran {CheckCount} deterministic checks for snapshot {SnapshotId}: {TriggeredCount} triggered, {HoldCount} holds created.",
            outcomes.Count, snapshot.CalculationAuditSnapshotId, discrepancies.Count, holds.Count);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
