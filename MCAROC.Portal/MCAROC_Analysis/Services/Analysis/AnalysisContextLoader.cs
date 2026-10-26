using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Loads everything one rule-engine pass reads for a request's ingestion run. Shared by the analysis and by the litigation
/// findings refresh, so both evaluate exactly the same inputs.</summary>
public static class AnalysisContextLoader
{
    public static async Task<AnalysisContext> LoadAsync(AppDbContext db, LitigationReaderGate? readerGate, McaRequest request, long ingestionRunId, CancellationToken ct)
    {
        var companyProfile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == ingestionRunId, ct);
        var directors = await db.Directors.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var directorAssociations = await db.DirectorAssociations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var shareholdings = await db.Shareholdings.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        // The rule engine is standalone-only (Phase 6 added Consolidated rows to the same table).
        var financialYears = await db.FinancialYearData
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Standalone).ToListAsync(ct);
        // AsSplitQuery on both parent+children loads: as one JOIN, every parent row is repeated for each child (a GST registration for each of
        // its filings: up to ~1,600 per company in the 41-company sample), which made this load exceed the 30 s command timeout under load.
        var charges = await db.RocCharges.AsSplitQuery().Include(c => c.Events).Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var msmePayments = await db.MsmePayments.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var gstRegistrations = await db.GstRegistrations.AsSplitQuery().Include(g => g.Filings).Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var epfoContributions = await db.EpfoContributions.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var epfoEstablishments = await db.EpfoEstablishments.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        var auditorObservations = await db.AuditorObservations
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Standalone).ToListAsync(ct);
        var litigation = await LitigationEvidence.LoadAsync(db, readerGate, request.RequestId, request.CompanyName, ingestionRunId, ct);
        var propertyExtractions = await PropertyParticulars.PropertyParticularsExtractionService.LoadCompletedAsync(
            db, charges.SelectMany(c => c.Events), ct);

        return AnalysisContext.Build(
            request, companyProfile, directors, directorAssociations, shareholdings, financialYears, charges,
            msmePayments, gstRegistrations, epfoContributions, auditorObservations, DateTime.UtcNow,
            epfoEstablishments, propertyExtractions, litigation);
    }
}
