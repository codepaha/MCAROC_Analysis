using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Everything one rule-engine pass needs for a request: every Phase 1 entity list (already
/// filtered to the request's LatestCompletedIngestionRunId — see AnalysisOrchestrator), materiality, and
/// financial-data recency. Built once per AnalysisRun, passed by reference into every domain rule's
/// Evaluate method — no rule queries the database itself.</summary>
public class AnalysisContext
{
    public required McaRequest Request { get; init; }
    public CompanyProfile? CompanyProfile { get; init; }
    public required IReadOnlyList<Director> Directors { get; init; }
    public required IReadOnlyList<DirectorAssociation> DirectorAssociations { get; init; }
    public required IReadOnlyList<Shareholding> Shareholdings { get; init; }
    public required IReadOnlyList<FinancialYearData> FinancialYears { get; init; }
    public required IReadOnlyList<RocCharge> Charges { get; init; }
    public required IReadOnlyList<MsmePayment> MsmePayments { get; init; }
    public required IReadOnlyList<GstRegistration> GstRegistrations { get; init; }
    public required IReadOnlyList<EpfoContribution> EpfoContributions { get; init; }
    public required IReadOnlyList<AuditorObservation> AuditorObservations { get; init; }
    public required IReadOnlyList<Litigation> Litigations { get; init; }

    public required DateTime AnalysisDate { get; init; }
    public required MaterialityContext Materiality { get; init; }

    public FinancialYearData? LatestFinancialYear { get; init; }
    public FinancialYearData? PriorFinancialYear { get; init; }
    public FinancialYearData? SecondPriorFinancialYear { get; init; }

    /// <summary>Months between the latest FinancialYearData's assumed FY-end and AnalysisDate. Null when
    /// no financial year data exists at all. Convention (load-bearing here, not on the Phase 1 entity
    /// itself — see the plan's boundary decision): FinancialYear is the ENDING year of the financial year,
    /// e.g. FinancialYear=2025 means FY2024-25, ending 31 March 2025.</summary>
    public int? FinancialDataAgeMonths { get; init; }

    public static AnalysisContext Build(
        McaRequest request, CompanyProfile? companyProfile, IReadOnlyList<Director> directors,
        IReadOnlyList<DirectorAssociation> directorAssociations, IReadOnlyList<Shareholding> shareholdings,
        IReadOnlyList<FinancialYearData> financialYears, IReadOnlyList<RocCharge> charges,
        IReadOnlyList<MsmePayment> msmePayments, IReadOnlyList<GstRegistration> gstRegistrations,
        IReadOnlyList<EpfoContribution> epfoContributions, IReadOnlyList<AuditorObservation> auditorObservations,
        IReadOnlyList<Litigation> litigations, DateTime analysisDate)
    {
        var orderedYears = financialYears.OrderByDescending(f => f.FinancialYear).ToList();
        var latest = orderedYears.ElementAtOrDefault(0);
        var prior = orderedYears.ElementAtOrDefault(1);
        var secondPrior = orderedYears.ElementAtOrDefault(2);

        int? ageMonths = null;
        if (latest is not null)
        {
            var fyEnd = new DateOnly(latest.FinancialYear, 3, 31);
            var months = (analysisDate.Year - fyEnd.Year) * 12 + (analysisDate.Month - fyEnd.Month);
            if (analysisDate.Day < fyEnd.Day) months--; // don't round a partial month up
            ageMonths = Math.Max(0, months);
        }

        return new AnalysisContext
        {
            Request = request,
            CompanyProfile = companyProfile,
            Directors = directors,
            DirectorAssociations = directorAssociations,
            Shareholdings = shareholdings,
            FinancialYears = orderedYears,
            Charges = charges,
            MsmePayments = msmePayments,
            GstRegistrations = gstRegistrations,
            EpfoContributions = epfoContributions,
            AuditorObservations = auditorObservations,
            Litigations = litigations,
            AnalysisDate = analysisDate,
            Materiality = MaterialityContext.FromLatestYear(latest),
            LatestFinancialYear = latest,
            PriorFinancialYear = prior,
            SecondPriorFinancialYear = secondPrior,
            FinancialDataAgeMonths = ageMonths
        };
    }
}
