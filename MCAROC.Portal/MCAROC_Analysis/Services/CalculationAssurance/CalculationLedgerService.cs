using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Persists an immutable CalculationLedgerEntry per MetricResult for one analysis snapshot.
/// Called from AnalysisOrchestrator.RunAnalysisAsync right after the rule engine's own findings/counts
/// save — <em>before</em> the AI cross-section call — via
/// <see cref="DossierAssembler.BuildForInFlightAnalysisAsync"/> (the AnalysisRun is still `Running` at
/// that point; ledger persistence must not depend on it already being `Completed`, or an AI timeout/crash
/// would leave a "completed" analysis with no audit ledger at all — PR #170 review round 3).
///
/// PR1 covers exactly the three MetricGroups the deterministic checks (built in PR2) need first:
/// FinancialTrend, CapitalReconciliation, ChargeRegister. Full coverage of every MetricGroup is a
/// follow-up, not this issue's v1 (see the plan's open questions).
///
/// A no-op entirely when CalculationAssurance:Mode is Off — no DB query at all, so this has zero
/// behavioral or performance impact until an environment explicitly turns the feature on.</summary>
public class CalculationLedgerService(
    AppDbContext db,
    DossierAssembler assembler,
    IConfiguration config,
    ILogger<CalculationLedgerService> logger)
{
    public async Task PersistSnapshotAsync(long requestId, long ingestionRunId, long analysisRunId, CancellationToken ct)
    {
        var mode = CalculationAssuranceConfig.ParseMode(config);
        if (mode == CalculationAssuranceMode.Off)
            return;

        var snapshot = await db.CalculationAuditSnapshots.FirstOrDefaultAsync(
            s => s.RequestId == requestId && s.IngestionRunId == ingestionRunId && s.AnalysisRunId == analysisRunId, ct);

        if (snapshot is not null)
        {
            // A snapshot row existing on its own does not mean the ledger is complete — under the old
            // two-save implementation a crash between them could leave an empty snapshot forever
            // "already there" to a check like this. Completeness is judged by whether it has ledger
            // entries; an incomplete one is finished here, not skipped (PR #170 review round 3, point 2).
            var hasEntries = await db.CalculationLedgerEntries.AnyAsync(e => e.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId, ct);
            if (hasEntries)
            {
                logger.LogInformation(
                    "Calculation-assurance snapshot already complete for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) — skipping.",
                    requestId, ingestionRunId, analysisRunId);
                return;
            }

            logger.LogWarning(
                "Calculation-assurance snapshot {SnapshotId} exists with no ledger entries — a prior attempt was interrupted before completing. Completing it now.",
                snapshot.CalculationAuditSnapshotId);
        }

        var model = await assembler.BuildForInFlightAnalysisAsync(requestId, ingestionRunId, analysisRunId, ct);
        if (model is null)
        {
            logger.LogWarning(
                "Calculation-assurance ledger requested for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) but no matching in-flight analysis was found — skipping.",
                requestId, ingestionRunId, analysisRunId);
            return;
        }

        var companyProfile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == ingestionRunId, ct);

        var isNewSnapshot = snapshot is null;
        snapshot ??= new CalculationAuditSnapshot
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            AnalysisRunId = analysisRunId,
            CreatedUtc = DateTime.UtcNow
        };

        var groups = new (string KeyPrefix, MetricGroup Group)[]
        {
            ("FinancialTrend", DossierComputations.FinancialTrendMetrics(model)),
            ("CapitalReconciliation", DossierComputations.CapitalReconciliationMetrics(model)),
            ("ChargeRegister", DossierComputations.ChargeRegisterMetrics(model))
        };

        var entries = new List<CalculationLedgerEntry>();
        foreach (var (keyPrefix, group) in groups)
        foreach (var metric in group.Metrics)
            entries.Add(BuildEntry(keyPrefix, metric, snapshot, model, companyProfile));

        // One SaveChangesAsync call for the whole graph (snapshot + every entry, linked by navigation
        // rather than a pre-known id) — EF Core wraps this in a single transaction, so either the entire
        // ledger commits or none of it does. This makes the earlier two-step "snapshot, then entries"
        // failure mode structurally impossible rather than merely unlikely: there is no window left where
        // a snapshot row can exist without its entries (PR #170 review round 3, point 2).
        if (isNewSnapshot)
            db.CalculationAuditSnapshots.Add(snapshot);
        db.CalculationLedgerEntries.AddRange(entries);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Lost a race with a concurrent attempt at the exact same snapshot tuple (or, on the resume
            // path, the exact same ledger-entry keys) — its SaveChangesAsync is just as atomic as ours,
            // so its ledger is complete and ours rolled back cleanly. Nothing to do.
            logger.LogInformation(
                "Lost a race persisting the calculation-assurance ledger for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) — another attempt already completed it.",
                requestId, ingestionRunId, analysisRunId);
            return;
        }

        logger.LogInformation(
            "Persisted {Count} calculation-ledger entries for request {RequestId} run ({IngestionRunId},{AnalysisRunId}).",
            entries.Count, requestId, ingestionRunId, analysisRunId);
    }

    private static CalculationLedgerEntry BuildEntry(
        string keyPrefix, MetricResult metric, CalculationAuditSnapshot snapshot, DossierModel model, CompanyProfile? companyProfile)
    {
        var sourceRefs = CalculationSourceRowRefResolver.Resolve(metric.Inputs, model, companyProfile);
        // A metric that produced a real value but has ANY known-source-entity input that failed to
        // resolve is untraceable — flagged for PR2's ProvenanceCompleteness check, never silently treated
        // as fine. This is deliberately "any", not "all zero": a metric mixing one resolved input (e.g.
        // FinancialYearData.Revenue) with one genuinely-unresolved one (e.g. a FinancialParameter that
        // isn't on file this year) must not read as fully traceable just because part of it resolved —
        // sourceRefs being non-empty from the resolved half must never mask the unresolved half. An input
        // naming something that was never a source-row concept to begin with (a derived/computed
        // reference like "DossierComputations.SecurityTypeLabels") is excluded from this check entirely,
        // not counted as a gap merely because it was never resolvable. A metric that is itself
        // Insufficient (no value) is not "unresolved provenance" — there is nothing to trace.
        var hasUnresolvedProvenance = metric.Value is not null
            && !CalculationInputResolver.AllKnownInputsResolved(metric.Inputs, model, companyProfile);

        const string calcVersion = "1.0";
        var calculationKey = CalculationKeySlug.For(keyPrefix, metric.Label);
        var inputsJson = JsonSerializer.Serialize(metric.Inputs);
        var sourceRefsJson = JsonSerializer.Serialize(sourceRefs);

        // Hashes the actual resolved VALUES behind the input names (and the full output shape), not just
        // which fields were used — two different companies' revenue figures computed via the same
        // formula must never collide on InputHash (PR #170 review round 3, point 1).
        var canonicalInputPayload = CalculationInputCanonicalizer.BuildCanonicalInputPayload(metric.Inputs, model, companyProfile);
        var canonicalOutputPayload = string.Join('|',
            calculationKey, calcVersion, metric.Period, metric.Unit.ToString(),
            metric.Value?.ToString(CultureInfo.InvariantCulture) ?? "null",
            metric.TextValue ?? "null",
            metric.InsufficiencyReason ?? "null");

        return new CalculationLedgerEntry
        {
            Snapshot = snapshot,
            CalculationKey = calculationKey,
            CalcVersion = calcVersion,
            MetricLabel = metric.Label,
            Period = metric.Period,
            Unit = metric.Unit,
            ValueNumeric = metric.Value,
            ValueText = metric.TextValue,
            InsufficiencyReason = metric.InsufficiencyReason,
            InputsJson = inputsJson,
            InputHash = ComputeHash(canonicalInputPayload),
            OutputHash = ComputeHash(canonicalOutputPayload),
            SourceRowRefsJson = sourceRefsJson,
            HasUnresolvedProvenance = hasUnresolvedProvenance,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static string ComputeHash(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
