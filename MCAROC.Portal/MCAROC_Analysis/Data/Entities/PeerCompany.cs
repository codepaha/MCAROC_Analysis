namespace MCAROC_Analysis.Data.Entities;

/// <summary>One row of the "5 Closest Peers by Revenue" block at the foot of the "Peer Comparison"
/// sheet — the named companies whose revenue is closest to this company's, for the reference financial
/// year. The list includes the company itself; callers flag that by matching <see cref="Cin"/> to the
/// request, rather than the parser guessing.</summary>
public sealed class PeerCompany : ExtractedEntityBase
{
    public long PeerCompanyId { get; set; }

    /// <summary>The comparison's reference financial year (the sheet's "Financial Year" row, or the
    /// latest metrics-grid year). Null when the sheet carries neither — an incomplete peer block, not
    /// a "year zero".</summary>
    public int? FinancialYear { get; set; }

    /// <summary>1-based position in the list as the sheet orders it (by revenue, descending).</summary>
    public int Rank { get; set; }

    public string LegalName { get; set; } = string.Empty;
    public string? Cin { get; set; }
    public string? City { get; set; }
    public decimal? RevenueCrore { get; set; }

    public string? Industry { get; set; }
    public string? Segment { get; set; }
}
