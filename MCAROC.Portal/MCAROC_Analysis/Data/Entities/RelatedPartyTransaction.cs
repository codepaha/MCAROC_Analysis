namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Related Party Transactions" sheet — transaction-level RPT disclosure
/// (revenue/expense/other transactions with a related party for a financial year), distinct from the
/// "Related Corporates" sheet's ownership/group-structure listing.</summary>
public class RelatedPartyTransaction : ExtractedEntityBase
{
    public long RelatedPartyTransactionId { get; set; }

    public DateOnly? FinancialYearEnding { get; set; }

    public string? EntityType { get; set; }
    public string EntityNameRaw { get; set; } = string.Empty;
    public string EntityNameNormalized { get; set; } = string.Empty;

    public string? RelationshipRaw { get; set; }
    public string? TransactionType { get; set; }
    public decimal? AmountCrore { get; set; }
}
