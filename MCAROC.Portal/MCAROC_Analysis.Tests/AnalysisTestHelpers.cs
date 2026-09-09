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
        List<Litigation>? litigations = null,
        DateTime? analysisDate = null) =>
        AnalysisContext.Build(
            Request(), companyProfile, directors ?? [], directorAssociations ?? [], shareholdings ?? [],
            financialYears ?? [], charges ?? [], msmePayments ?? [], gstRegistrations ?? [],
            epfoContributions ?? [], auditorObservations ?? [], litigations ?? [], analysisDate ?? AnalysisDate);

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
