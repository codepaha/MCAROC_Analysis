using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Persists an immutable CalculationLedgerEntry per MetricResult for one analysis snapshot.
/// Called once from AnalysisOrchestrator.RunAnalysisAsync, right after the AnalysisRun itself is saved as
/// Completed/CompletedWithErrors — a crash after that point still leaves a durable AnalysisRun the next
/// request to this request can build on, matching the save-before-further-work discipline already used
/// for the rule-engine findings.
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
        var mode = ParseMode(config["CalculationAssurance:Mode"]);
        if (mode == CalculationAssuranceMode.Off)
            return;

        var existing = await db.CalculationAuditSnapshots
            .AnyAsync(s => s.RequestId == requestId && s.IngestionRunId == ingestionRunId && s.AnalysisRunId == analysisRunId, ct);
        if (existing)
        {
            // Ledger rows are write-once — a correction always produces a new (IngestionRunId,
            // AnalysisRunId) tuple, so an existing snapshot for this exact tuple means this method
            // already ran for it (e.g. a retried call after a partial failure further down the pipeline).
            logger.LogInformation(
                "Calculation-assurance snapshot already exists for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) — skipping.",
                requestId, ingestionRunId, analysisRunId);
            return;
        }

        var model = await assembler.BuildAsync(requestId, ct);
        if (model is null)
        {
            logger.LogWarning(
                "Calculation-assurance ledger requested for request {RequestId} but DossierAssembler returned no model — skipping.",
                requestId);
            return;
        }

        var companyProfile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == ingestionRunId, ct);

        var snapshot = new CalculationAuditSnapshot
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            AnalysisRunId = analysisRunId,
            CreatedUtc = DateTime.UtcNow
        };
        db.CalculationAuditSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct); // need CalculationAuditSnapshotId for the ledger rows below

        var groups = new (string KeyPrefix, MetricGroup Group)[]
        {
            ("FinancialTrend", DossierComputations.FinancialTrendMetrics(model)),
            ("CapitalReconciliation", DossierComputations.CapitalReconciliationMetrics(model)),
            ("ChargeRegister", DossierComputations.ChargeRegisterMetrics(model))
        };

        var entries = new List<CalculationLedgerEntry>();
        foreach (var (keyPrefix, group) in groups)
        foreach (var metric in group.Metrics)
            entries.Add(BuildEntry(keyPrefix, metric, snapshot.CalculationAuditSnapshotId, model, companyProfile));

        db.CalculationLedgerEntries.AddRange(entries);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Persisted {Count} calculation-ledger entries for request {RequestId} run ({IngestionRunId},{AnalysisRunId}).",
            entries.Count, requestId, ingestionRunId, analysisRunId);
    }

    private static CalculationLedgerEntry BuildEntry(
        string keyPrefix, MetricResult metric, long snapshotId, DossierModel model, CompanyProfile? companyProfile)
    {
        var sourceRefs = CalculationSourceRowRefResolver.Resolve(metric.Inputs, model, companyProfile);
        // A metric that produced a real value but resolved to zero source rows is untraceable — flagged
        // for PR2's ProvenanceCompleteness check, never silently treated as fine. A metric that is itself
        // Insufficient (no value) is not "unresolved provenance" — there is nothing to trace.
        var hasUnresolvedProvenance = metric.Value is not null && sourceRefs.Count == 0;

        var calculationKey = $"{keyPrefix}.{Slugify(metric.Label)}";
        var inputsJson = JsonSerializer.Serialize(metric.Inputs);
        var sourceRefsJson = JsonSerializer.Serialize(sourceRefs);
        var outputFingerprint = $"{calculationKey}|{metric.Period}|{metric.Value}|{metric.TextValue}";

        return new CalculationLedgerEntry
        {
            CalculationAuditSnapshotId = snapshotId,
            CalculationKey = calculationKey,
            CalcVersion = "1.0",
            MetricLabel = metric.Label,
            Period = metric.Period,
            Unit = metric.Unit,
            ValueNumeric = metric.Value,
            ValueText = metric.TextValue,
            InsufficiencyReason = metric.InsufficiencyReason,
            InputsJson = inputsJson,
            InputHash = ComputeHash(inputsJson),
            OutputHash = ComputeHash(outputFingerprint),
            SourceRowRefsJson = sourceRefsJson,
            HasUnresolvedProvenance = hasUnresolvedProvenance,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static string ComputeHash(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    /// <summary>Derives a stable-ish machine key from a metric's human label, e.g. "Net debt / EBITDA"
    /// -&gt; "NetDebtEbitda". Good enough for v1: these labels change only when someone deliberately edits
    /// DossierComputations.Metrics.cs, at which point CalcVersion is the intended place to record an
    /// intentional formula change anyway.</summary>
    private static string Slugify(string label)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(label, "[A-Za-z0-9]+"))
            sb.Append(char.ToUpperInvariant(m.Value[0])).Append(m.Value[1..].ToLowerInvariant());
        return sb.Length > 0 ? sb.ToString() : "Metric";
    }

    private static CalculationAssuranceMode ParseMode(string? raw) =>
        Enum.TryParse<CalculationAssuranceMode>(raw, ignoreCase: true, out var mode) ? mode : CalculationAssuranceMode.Off;
}
