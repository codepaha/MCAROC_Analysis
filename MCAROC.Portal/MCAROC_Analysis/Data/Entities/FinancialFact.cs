namespace MCAROC_Analysis.Data.Entities;

/// <summary>Which stacked section of the "Standalone / Consolidated Financial Data" sheet a
/// <see cref="FinancialFact"/> came from.</summary>
public enum FinancialStatementSection
{
    BalanceSheet,
    ProfitAndLoss,
    CashFlow,
    Ratios,
    Other
}

/// <summary>Catch-all for every value-bearing line item × year on the "Standalone / Consolidated
/// Financial Data" sheet that is <em>not</em> mapped to a typed <see cref="FinancialYearData"/> column
/// (reserves, fixed assets, expenses, margins, leverage / liquidity ratios, cash-flow adjustments — the
/// sheet carries ~100 labels; the typed model covers ~20). Nothing on the sheet is silently dropped:
/// what isn't typed lands here with its raw value and, where it parses, a number.</summary>
public class FinancialFact : ExtractedEntityBase
{
    public long FinancialFactId { get; set; }

    public FinancialBasis Basis { get; set; } = FinancialBasis.Standalone;
    public FinancialStatementSection Section { get; set; } = FinancialStatementSection.Other;

    public string Label { get; set; } = string.Empty;
    public int? FinancialYear { get; set; }

    public string RawValue { get; set; } = string.Empty;
    public decimal? NumericValue { get; set; }

    /// <summary>True when the year was inferred by column position because the source section has no
    /// year header of its own (the cash-flow section). Such values must not drive automated findings.</summary>
    public bool YearInferred { get; set; }
}
