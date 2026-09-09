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
        var analysis = await db.AnalysisRuns.Where(a => a.RequestId == requestId)
            .OrderByDescending(a => a.RunNumber).FirstOrDefaultAsync(ct);

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

        // ── Litigation ──
        var litigations = await db.Litigations.Where(x => x.IngestionRunId == runId).ToListAsync(ct);

        // ── Findings ──
        var findings = new List<AnalysisFinding>();
        ExecutiveSummary? execSummary = null;
        if (analysis is not null)
        {
            var raw = await db.AnalysisFindings.Where(f => f.AnalysisRunId == analysis.AnalysisRunId).ToListAsync(ct);
            findings = raw
                .OrderByDescending(f => f.Severity).ThenByDescending(f => f.DisplayPriority).ThenByDescending(f => f.ObservationDate)
                .ToList();
            if (analysis.ExecutiveSummaryJson is { } sj)
            {
                try { execSummary = JsonSerializer.Deserialize<ExecutiveSummary>(sj); }
                catch (JsonException) { }
            }
        }

        var roles = DossierComputations.LitigationRoles(litigations, findings);

        return new DossierModel(
            requestId, runId, analysis?.AnalysisRunId,
            new DossierCover(
                request.CompanyName, request.Cin ?? profile?.Cin, request.Pan ?? profile?.Pan,
                profile?.IncorporationDate, profile?.CompanyStatus,
                request.Client?.ClientName ?? "", DateTime.UtcNow, run?.CompletedDate),
            new DossierCorporate(directors, officers, shareholders, related, allotments, desigHistory, otherDirectorships, structure),
            new DossierFinancials(standalone, consolidated, facts, parameters, auditors, peers),
            new DossierCharges(
                charges,
                DossierComputations.OpenChargesByAmount(charges),
                DossierComputations.SatisfiedChargesBySatisfaction(charges),
                DossierComputations.LenderConcentration(charges),
                findings.Count(f => f.Code == ChargeRules.MaterialEnhancementCode)),
            new DossierCompliance(compliance, msme, gst, epfo, DossierDeduplicator.SummariseSuitFiled(compliance)),
            new DossierLitigation(litigations, DossierDeduplicator.ThreadLitigation(litigations), roles),
            new DossierExecSummary(
                analysis?.OverallReviewPriority,
                analysis?.CriticalFindingsCount ?? 0, analysis?.ReviewFindingsCount ?? 0,
                analysis?.WatchFindingsCount ?? 0, analysis?.PositiveFindingsCount ?? 0,
                findings, execSummary));
    }
}
