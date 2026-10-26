using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Tests;

public static class AnalysisTestHelpers
{
    public static readonly DateTime AnalysisDate = new(2026, 9, 9);

    public static McaRequest Request() => new()
    {
        RequestId = 1, RequestNumber = "MCA-TEST-000001", ClientId = 1, CompanyName = "Test Co", CreatedDate = DateTime.UtcNow
    };

    public static AnalysisContext BuildContext(
        CompanyProfile? companyProfile = null,
        List<Director>? directors = null,
        List<DirectorAssociation>? directorAssociations = null,
        List<Shareholding>? shareholdings = null,
        List<FinancialYearData>? financialYears = null,
        List<RocCharge>? charges = null,
        List<MsmePayment>? msmePayments = null,
        List<GstRegistration>? gstRegistrations = null,
        List<EpfoContribution>? epfoContributions = null,
        List<AuditorObservation>? auditorObservations = null,
        List<LitigationCase>? litigationCases = null,
        List<string>? litigationCompanyNames = null,
        DateTime? analysisDate = null,
        Client? client = null,
        List<EpfoEstablishment>? epfoEstablishments = null,
        Dictionary<string, PropertyParticularsExtraction>? propertyExtractions = null)
    {
        var request = Request();
        request.Client = client;
        return AnalysisContext.Build(
            request, companyProfile, directors ?? [], directorAssociations ?? [], shareholdings ?? [],
            financialYears ?? [], charges ?? [], msmePayments ?? [], gstRegistrations ?? [],
            epfoContributions ?? [], auditorObservations ?? [], analysisDate ?? AnalysisDate,
            epfoEstablishments, propertyExtractions,
            // Cases given = a completed search returned them; none given = no search has completed.
            litigationCases is null ? null
                : new LitigationEvidence(true, litigationCases, litigationCompanyNames ?? [companyProfile?.CompanyName ?? request.CompanyName]));
    }

    /// <summary>A litigation-service case for rule tests: <paramref name="parties"/> is "petitioner vs. respondent".</summary>
    public static LitigationCase Case(long id, string number, string? status, string parties, string? direction = null)
    {
        var sides = System.Text.RegularExpressions.Regex.Split(parties, @"\s+vs\.?\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static string Json(string? name) => name is null ? "[]" : System.Text.Json.JsonSerializer.Serialize(new[] { name.Trim() });
        return new LitigationCase
        {
            LitigationCaseId = id, RequestId = 1, CaseNumber = number, CaseStatus = status, Direction = direction,
            PetitionersJson = Json(sides.ElementAtOrDefault(0)), RespondentsJson = Json(sides.ElementAtOrDefault(1)),
            AttributionOutcome = LitigationAttributionOutcome.Attributed
        };
    }

    public static FinancialYearData Fy(
        int year, decimal? revenue = null, decimal? ebitda = null, decimal? ebit = null, decimal? pat = null,
        decimal? netWorth = null, decimal? currentAssets = null, decimal? currentLiabilities = null,
        decimal? tradeReceivables = null, decimal? tradePayables = null, decimal? totalDebt = null,
        decimal? financeCost = null, decimal? cfo = null, bool cashFlowYearInferred = false) => new()
    {
        FinancialYear = year, Revenue = revenue, Ebitda = ebitda, Ebit = ebit, Pat = pat, NetWorth = netWorth,
        CurrentAssets = currentAssets, CurrentLiabilities = currentLiabilities, TradeReceivables = tradeReceivables,
        TradePayables = tradePayables, TotalDebt = totalDebt, FinanceCost = financeCost, Cfo = cfo,
        CashFlowYearInferred = cashFlowYearInferred
    };
}
