namespace MCAROC_Analysis.Data.Entities;

/// <summary>One row per (financial year, basis), normalized from the "Standalone Financial Data" and
/// "Consolidated Financial Data" wide year-column layouts. All figures confirmed as Rs. Crore from
/// direct inspection of the source sheets. The rule engine and default RAG facts read Basis=Standalone
/// only; the Financials tab offers a Standalone/Consolidated toggle.</summary>
public class FinancialYearData : ExtractedEntityBase
{
    public long FinancialId { get; set; }

    public int FinancialYear { get; set; }

    public FinancialBasis Basis { get; set; } = FinancialBasis.Standalone;

    public decimal? Revenue { get; set; }
    public decimal? OtherIncome { get; set; }

    public decimal? Ebitda { get; set; }
    public decimal? Ebit { get; set; }
    public decimal? Pbt { get; set; }
    public decimal? Pat { get; set; }

    public decimal? NetWorth { get; set; }

    public decimal? CurrentAssets { get; set; }
    public decimal? CurrentLiabilities { get; set; }

    public decimal? Inventory { get; set; }
    public decimal? TradeReceivables { get; set; }
    public decimal? CashAndBank { get; set; }

    public decimal? TradePayables { get; set; }

    public decimal? LongTermBorrowings { get; set; }
    public decimal? ShortTermBorrowings { get; set; }
    public decimal? TotalDebt { get; set; }

    public decimal? FinanceCost { get; set; }

    public decimal? Cfo { get; set; }
    public decimal? Cfi { get; set; }
    public decimal? Cff { get; set; }

    public decimal? ShareCapital { get; set; }
}
