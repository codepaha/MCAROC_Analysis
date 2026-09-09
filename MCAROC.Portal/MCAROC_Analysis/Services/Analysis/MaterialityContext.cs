using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Built once per AnalysisContext from the latest available FinancialYearData row. Every field is
/// nullable — a missing denominator means the dependent ratio rule reports NotEvaluated, never a false 0
/// or an implicit adverse finding.</summary>
public record MaterialityContext(decimal? Revenue, decimal? NetWorth, decimal? Ebitda, decimal? TotalDebt, decimal? TradePayables)
{
    public static MaterialityContext FromLatestYear(FinancialYearData? latest) =>
        latest is null
            ? new MaterialityContext(null, null, null, null, null)
            : new MaterialityContext(latest.Revenue, latest.NetWorth, latest.Ebitda, latest.TotalDebt, latest.TradePayables);
}
