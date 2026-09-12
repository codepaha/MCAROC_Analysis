using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Loads one analyzed request's full entity graph, de-duplicates it (<see cref="DossierDeduplicator"/>),
/// and computes the shared derived values (<see cref="DossierComputations"/>) into a <see cref="DossierModel"/>.
/// The same instance backs both the client PDF and the restyled company page — go through
/// <see cref="DossierCache"/> so a page view and a PDF download for one run assemble only once.</summary>
public class DossierAssembler(AppDbContext db)
{
    public async Task<DossierModel?> BuildAsync(long requestId, CancellationToken ct = default)
    {
        var request = await db.Requests.Include(r => r.Client).FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request?.LatestCompletedIngestionRunId is not { } runId) return null;

        var run = await db.IngestionRuns.FirstOrDefaultAsync(x => x.IngestionRunId == runId, ct);

        // The analysis MUST belong to the ingestion run we are about to render — otherwise a fresh
        // re-ingest paired with a not-yet-re-run analysis would present stale findings over new data.
        // No terminal analysis for this exact ingestion run ⇒ the dossier is not ready (controller → 409).
        var analysis = await db.AnalysisRuns
            .Where(a => a.RequestId == requestId && a.IngestionRunId == runId
                && (a.Status == AnalysisRunStatus.Completed || a.Status == AnalysisRunStatus.CompletedWithErrors))
            .OrderByDescending(a => a.RunNumber)
            .FirstOrDefaultAsync(ct);
        if (analysis is null) return null;

        // ── Corporate ──
        var directors = await db.Directors.Where(x => x.IngestionRunId == runId).OrderBy(x => x.NameRaw).ToListAsync(ct);
        var officers = await db.CompanyOfficers.Where(x => x.IngestionRunId == runId).OrderBy(x => x.NameRaw).ToListAsync(ct);
        var shareholders = DossierDeduplicator.MergeShareholders(
            await db.Shareholdings.Where(x => x.IngestionRunId == runId).ToListAsync(ct));
        var related = DossierDeduplicator.DistinctRelatedCorporates(
            await db.RelatedCorporates.Where(x => x.IngestionRunId == runId).ToListAsync(ct));
        var allotments = await db.SecurityAllotments.Where(x => x.IngestionRunId == runId)
            .OrderByDescending(x => x.AllotmentDate).ToListAsync(ct);
        var desigHistory = await db.DirectorAssignmentHistories.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var otherDirectorships = await db.DirectorAssociations.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var structure = await db.CompanyStructures.FirstOrDefaultAsync(x => x.IngestionRunId == runId, ct);
        var shareholdingPattern = await db.ShareholdingPatternRows.Where(x => x.IngestionRunId == runId)
            .OrderBy(x => x.HolderClass).ThenBy(x => x.AsOnDate).ThenBy(x => x.DisplayOrder).ToListAsync(ct);
        var profile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == runId, ct);

        // ── Financials ──
        var fyAll = await db.FinancialYearData.Where(x => x.IngestionRunId == runId)
            .OrderBy(x => x.FinancialYear).ToListAsync(ct);
        var standalone = fyAll.Where(f => f.Basis == FinancialBasis.Standalone).ToList();
        var consolidated = fyAll.Where(f => f.Basis == FinancialBasis.Consolidated).ToList();
        var facts = await db.FinancialFacts.Where(x => x.IngestionRunId == runId)
            .OrderBy(x => x.Section).ThenBy(x => x.Label).ThenByDescending(x => x.FinancialYear).ToListAsync(ct);
        var parameters = await db.FinancialParameters.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var auditors = await db.AuditorObservations.Where(x => x.IngestionRunId == runId)
            .OrderByDescending(x => x.FinancialYear).ToListAsync(ct);
        var peers = await db.PeerComparisonMetrics.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var peerCompanies = await db.PeerCompanies.Where(x => x.IngestionRunId == runId).OrderBy(x => x.Rank).ToListAsync(ct);

        // ── Charges ──
        var charges = await db.RocCharges.Include(c => c.Events).ThenInclude(e => e.SecurityComponents)
            .Where(x => x.IngestionRunId == runId).ToListAsync(ct);

        // ── Compliance ──
        var compliance = await db.ComplianceRecords.Where(x => x.IngestionRunId == runId)
            .OrderBy(x => x.RecordType).ThenByDescending(x => x.RecordDate).ToListAsync(ct);
        var msme = await db.MsmePayments.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var gst = await db.GstRegistrations.Include(g => g.Filings).Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var epfo = await db.EpfoContributions.Where(x => x.IngestionRunId == runId)
            .OrderByDescending(x => x.WageMonth).ToListAsync(ct);
        var epfoEstablishments = await db.EpfoEstablishments.Where(x => x.IngestionRunId == runId)
            .OrderBy(x => x.Name).ToListAsync(ct);

        // ── Litigation ──
        var litigations = await db.Litigations.Where(x => x.IngestionRunId == runId).ToListAsync(ct);

        // ── Source records (Layer 0) — the verbatim staging rows the "Full source" annexure renders from ──
        var sourceRows = await db.SourceRows.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var sourceSheets = BuildSourceSheets(sourceRows);

        // ── Findings ──
        ExecutiveSummary? execSummary = null;
        var raw = await db.AnalysisFindings.Where(f => f.AnalysisRunId == analysis.AnalysisRunId).ToListAsync(ct);
        var findings = raw
            .OrderByDescending(f => f.Severity).ThenByDescending(f => f.DisplayPriority).ThenByDescending(f => f.ObservationDate)
            .ToList();
        if (analysis.ExecutiveSummaryJson is { } sj)
        {
            try { execSummary = JsonSerializer.Deserialize<ExecutiveSummary>(sj); }
            catch (JsonException) { }
        }

        var notAssessed = DeserializeSufficiencyNotes(analysis.DataSufficiencyNotesJson);

        var roles = DossierComputations.LitigationRoles(litigations, findings);

        var model = new DossierModel(
            requestId, runId, analysis.AnalysisRunId,
            new DossierCover(
                request.CompanyName, request.Cin ?? profile?.Cin, request.Pan ?? profile?.Pan,
                profile?.IncorporationDate, profile?.CompanyStatus,
                request.Client?.ClientName ?? "", DateTime.UtcNow, run?.CompletedDate, run?.SourceSnapshotDate),
            new DossierCorporate(directors, officers, shareholders, related, allotments, desigHistory, otherDirectorships, structure, profile?.PaidUpCapital, shareholdingPattern),
            new DossierFinancials(standalone, consolidated, facts, parameters, auditors, peers, peerCompanies),
            new DossierCharges(
                charges,
                DossierComputations.OpenChargesByAmount(charges),
                DossierComputations.SatisfiedChargesBySatisfaction(charges),
                DossierComputations.LenderConcentration(charges),
                findings.Count(f => f.Code == ChargeRules.MaterialEnhancementCode)),
            new DossierCompliance(compliance, msme, gst, epfo, epfoEstablishments, DossierDeduplicator.SummariseSuitFiled(compliance)),
            new DossierLitigation(litigations, DossierDeduplicator.ThreadLitigation(litigations), roles),
            new DossierExecSummary(
                analysis.OverallReviewPriority,
                analysis.CriticalFindingsCount, analysis.ReviewFindingsCount,
                analysis.WatchFindingsCount, analysis.PositiveFindingsCount,
                findings, execSummary, notAssessed),
            sourceSheets,
            SheetCoverage.From(run),
            Metrics: []);

        // Metrics are derived from the fully-assembled model, then folded back in.
        return model with { Metrics = DossierComputations.BuildMetricGroups(model) };
    }

    /// <summary>Reads <see cref="AnalysisRun.DataSufficiencyNotesJson"/> — a <c>[{code, reason}]</c>
    /// array the rule engine writes for every check it could not run. Never throws, and only ever
    /// returns notes that honour the <c>(Code, Reason)</c> contract: unparseable JSON, a non-array
    /// root, non-object entries, non-string fields, and any entry missing a non-empty trimmed
    /// <c>code</c> OR <c>reason</c> are all skipped. A code-less note would render as "()" with no
    /// rule identity and is unauditable, so it is dropped.</summary>
    internal static IReadOnlyList<DataSufficiencyNote> DeserializeSufficiencyNotes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        static string StringProp(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return []; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var notes = new List<DataSufficiencyNote>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;          // "a string", 42, null, [] → skip
                var code = StringProp(entry, "code").Trim();
                var reason = StringProp(entry, "reason").Trim();
                if (code.Length == 0 || reason.Length == 0) continue;           // both are required by contract
                notes.Add(new DataSufficiencyNote(code, reason));
            }
            return notes;
        }
    }

    /// <summary>Groups the raw <see cref="SourceRow"/> set into per-worksheet blocks, in workbook →
    /// sheet → row order, deserializing each row's cell array verbatim. No de-duplication, no clipping —
    /// this is the system of record.</summary>
    private static IReadOnlyList<DossierSourceSheet> BuildSourceSheets(IReadOnlyList<SourceRow> rows)
    {
        static int WorkbookRank(string role) => role switch
        {
            "RocReport" => 0,
            "ChargeReport" => 1,
            _ => 2
        };

        static string WorkbookLabel(string role) => role switch
        {
            "RocReport" => "Company master report (ROC)",
            "ChargeReport" => "Index of charges report",
            _ => role
        };

        return rows
            .GroupBy(r => (r.WorkbookRole, r.SheetIndex, r.SheetName))
            .Select(g => new DossierSourceSheet(
                g.Key.WorkbookRole,
                WorkbookLabel(g.Key.WorkbookRole),
                g.Key.SheetIndex,
                g.Key.SheetName,
                g.OrderBy(r => r.RowNumber)
                    .Select(r => new DossierSourceRow(
                        r.RowNumber,
                        DeserializeCells(r.CellsJson)))
                    .ToList()))
            .OrderBy(s => WorkbookRank(s.WorkbookRole))
            .ThenBy(s => s.WorkbookRole, StringComparer.Ordinal)
            .ThenBy(s => s.SheetIndex)
            .ToList();
    }

    private static IReadOnlyList<string?> DeserializeCells(string cellsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string?>>(cellsJson) ?? [];
        }
        catch (JsonException)
        {
            return [cellsJson];
        }
    }
}
