using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MCAROC_Analysis.Services.RequestDetails;

public sealed class WorkbookDetailsQuery(AppDbContext db, ILogger? logger = null)
{
    public async Task LoadAsync(RequestDetailsViewModel vm, IServiceProvider? services, CancellationToken ct)
    {
        var request = vm.Request;
        var id = request.RequestId;
        if (request.LatestCompletedIngestionRunId is { } runId)
        {
            vm.LatestRun = await db.IngestionRuns.AsNoTracking().FirstOrDefaultAsync(r => r.IngestionRunId == runId, ct);
            vm.SheetCoverage = SheetCoverage.From(vm.LatestRun, request.EntityType == EntityType.LLP);
            vm.Issues = await db.IngestionIssues.AsNoTracking().Where(i => i.IngestionRunId == runId).ToListAsync(ct);

            vm.CompanyProfile = await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.IngestionRunId == runId, ct);
            vm.CompanyEmails = await db.CompanyEmails.AsNoTracking().Where(x => x.IngestionRunId == runId).ToListAsync(ct);
            vm.Directors = await db.Directors.AsNoTracking().Where(x => x.IngestionRunId == runId).OrderBy(x => x.NameRaw).ToListAsync(ct);
            vm.DirectorAssociations = await db.DirectorAssociations.AsNoTracking().Where(x => x.IngestionRunId == runId).ToListAsync(ct);
            vm.Shareholdings = await db.Shareholdings.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.FinancialYear).ToListAsync(ct);
            vm.FinancialYears = await db.FinancialYearData.AsNoTracking()
                .Where(x => x.IngestionRunId == runId && x.Basis == FinancialBasis.Standalone)
                .OrderBy(x => x.FinancialYear).ToListAsync(ct);
            vm.ConsolidatedFinancialYears = await db.FinancialYearData.AsNoTracking()
                .Where(x => x.IngestionRunId == runId && x.Basis == FinancialBasis.Consolidated)
                .OrderBy(x => x.FinancialYear).ToListAsync(ct);
            vm.Charges = await db.RocCharges.AsNoTracking()
                .Include(c => c.Events).ThenInclude(e => e.SecurityComponents)
                .Where(x => x.IngestionRunId == runId).ToListAsync(ct);
            vm.PropertyExtractions = await PropertyParticularsExtractionService.LoadCompletedAsync(
                db, vm.Charges.SelectMany(c => c.Events), ct);
            vm.ChargeForms = await ChargeForms.LoadAsync(db, id, vm.Charges, ct,
                services?.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                services?.GetService<ChargeFormBackfill>());
            vm.ChargeDocuments = await ChargeDocumentLinker.LoadAsync(db, id, runId, ct,
                services?.GetService<ChargeDocumentLinkBuilder>());
            if (services?.GetService<ChargeInstrumentExtractionService>() is { IsActive: true } instruments)
            {
                try { await instruments.ScheduleForRequestAsync(id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger?.LogWarning(ex, "Scheduling charge instrument extraction failed for request {RequestId}", id); }
            }
            vm.InstrumentPassages = await ChargeInstrumentExtractionService.LoadAsync(db,
                vm.ChargeDocuments.Values.SelectMany(rows => rows.Select(r => r.FilingDocumentId)).Distinct().ToList(),
                ct);
            // Lazy backfill for requests ingested before the Gemini extraction existed: idempotent, a no-op once every
            // text has a row, and inert when extraction is disabled or Vertex AI is not configured.
            if (services?.GetService<PropertyParticularsExtractionService>() is { IsActive: true } propertyExtraction)
            {
                try { await propertyExtraction.ScheduleForRequestAsync(id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Scheduling property particulars extraction failed for request {RequestId}", id);
                }
            }
            vm.MsmePayments = await db.MsmePayments.AsNoTracking().Where(x => x.IngestionRunId == runId).ToListAsync(ct);
            vm.GstRegistrations = await db.GstRegistrations.AsNoTracking().Include(g => g.Filings)
                .Where(x => x.IngestionRunId == runId).ToListAsync(ct);
            vm.EpfoContributions = await db.EpfoContributions.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.WageMonth).ToListAsync(ct);
            vm.EpfoEstablishments = await db.EpfoEstablishments.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.Name).ToListAsync(ct);
            vm.AuditorObservations = await db.AuditorObservations.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.FinancialYear).ToListAsync(ct);

            // Phase 6 — the 12 additional workbook sheets.
            vm.Structure = await db.CompanyStructures.AsNoTracking().FirstOrDefaultAsync(x => x.IngestionRunId == runId, ct);
            vm.LlpContributions = request.EntityType == EntityType.LLP
                ? await db.LlpContributions.AsNoTracking().Where(x => x.IngestionRunId == runId).OrderBy(x => x.Kind).ThenBy(x => x.DisplayOrder).ToListAsync(ct)
                : [];
            vm.ShareholdingPattern = await db.ShareholdingPatternRows.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.HolderClass).ThenBy(x => x.DisplayOrder).ToListAsync(ct);
            vm.NameHistory = await db.CompanyNameHistories.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.DisplayOrder).ToListAsync(ct);
            // A certificate that cannot be read leaves the name history as it is; it never takes the page down.
            try
            {
                vm.NameChangeCertificates = await NameChangeCertificateLoader.LoadAsync(db, id, ct,
                    services?.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Reading the change-of-name certificates failed for request {RequestId}", id);
            }
            vm.PrincipalBusinessActivities = await db.PrincipalBusinessActivities.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.DisplayOrder).ToListAsync(ct);
            vm.RelatedCorporates = await db.RelatedCorporates.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.RelationshipType).ThenBy(x => x.EntityNameNormalized).ToListAsync(ct);
            vm.RelatedPartyTransactions = await db.RelatedPartyTransactions.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.FinancialYearEnding).ThenBy(x => x.EntityNameNormalized).ToListAsync(ct);
            vm.CreditRatings = await db.CreditRatings.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.Agency).ThenByDescending(x => x.RatingDate).ToListAsync(ct);
            vm.ComplianceRecords = await db.ComplianceRecords.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.RecordType).ThenByDescending(x => x.RecordDate).ToListAsync(ct);
            vm.FinancialParameters = await db.FinancialParameters.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.ParameterName).ThenByDescending(x => x.FinancialYear).ToListAsync(ct);
            vm.SecurityAllotments = await db.SecurityAllotments.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderByDescending(x => x.AllotmentDate).ToListAsync(ct);
            vm.ProprietorshipAssociations = await db.ProprietorshipAssociations.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.DirectorNameRaw).ToListAsync(ct);
            vm.DirectorAssignmentHistories = await db.DirectorAssignmentHistories.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.DirectorNameRaw).ThenByDescending(x => x.AppointmentDate).ToListAsync(ct);
            vm.PeerComparisonMetrics = await db.PeerComparisonMetrics.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.MetricName).ThenByDescending(x => x.FinancialYear).ToListAsync(ct);
            vm.PeerCompanies = await db.PeerCompanies.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.Rank).ToListAsync(ct);

            // Phase 7.0 completeness layer
            vm.CompanyOfficers = await db.CompanyOfficers.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.NameRaw).ToListAsync(ct);
            vm.FinancialFacts = await db.FinancialFacts.AsNoTracking().Where(x => x.IngestionRunId == runId)
                .OrderBy(x => x.Section).ThenBy(x => x.Label).ThenByDescending(x => x.FinancialYear).ToListAsync(ct);
        }

        // Only the analysis computed FROM the ingestion run this page is rendering. After a re-ingest
        // (see Reingest), an older analysis belongs to the previous ingestion run and must NOT be shown
        // against the new data — the AI tab falls back to its "analysis in progress / not started"
        // state until the matching new analysis exists. AnalysisRun.IngestionRunId is the lineage.
        vm.LatestAnalysisRun = request.LatestCompletedIngestionRunId is { } analysisIngestionRunId
            ? await db.AnalysisRuns.AsNoTracking()
                .Where(a => a.RequestId == id && a.IngestionRunId == analysisIngestionRunId)
                .OrderByDescending(a => a.RunNumber)
                .FirstOrDefaultAsync(ct)
            : null;
        if (vm.LatestAnalysisRun is not null)
        {
            // Severity/TemporalStatus are stored as strings (HasConversion<string>), so ordering by them
            // in SQL would sort alphabetically, not by enum severity — fetch then sort in memory instead.
            var findings = await db.AnalysisFindings.AsNoTracking().Where(f => f.AnalysisRunId == vm.LatestAnalysisRun.AnalysisRunId).ToListAsync(ct);
            vm.AnalysisFindings = findings
                .OrderByDescending(f => f.Severity).ThenByDescending(f => f.DisplayPriority).ThenByDescending(f => f.ObservationDate)
                .ToList();

            if (vm.LatestAnalysisRun.ExecutiveSummaryJson is { } summaryJson)
            {
                try { vm.ExecutiveSummary = System.Text.Json.JsonSerializer.Deserialize<ExecutiveSummary>(summaryJson); }
                catch (System.Text.Json.JsonException) { /* leave null — view shows findings without a summary */ }
            }
        }

    }
}
