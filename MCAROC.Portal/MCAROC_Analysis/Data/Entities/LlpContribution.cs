namespace MCAROC_Analysis.Data.Entities;

public enum LlpContributorKind { Partner, BodyCorporate }

/// <summary>One contributor of an LLP's "Structure" sheet (#539): a partner from the CONTRIBUTION BY PARTNERS grid (name, designation) or a body
/// corporate from CONTRIBUTION BY BODY CORPORATES (name, its nominee), with the contribution it is obliged to make and what it has paid in — an
/// LLP's equivalent of the shareholding a company's Structure sheet lists. Amounts are ₹ crore, as the sheet gives them.</summary>
public sealed class LlpContribution : ExtractedEntityBase
{
    public long LlpContributionId { get; set; }

    public LlpContributorKind Kind { get; set; }

    /// <summary>The "as on" date from the grid header ("CONTRIBUTION BY PARTNERS - 31 Mar, 2026").</summary>
    public DateOnly? AsOnDate { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A partner's designation ("Designated Partner", "Partner"); null for a body corporate.</summary>
    public string? Designation { get; set; }

    /// <summary>The individual a body corporate nominates; null for a partner.</summary>
    public string? NomineeName { get; set; }

    public decimal? ObligationOfContributionCrore { get; set; }
    public decimal? ContributionReceivedCrore { get; set; }

    /// <summary>1-based position within its grid, in sheet order.</summary>
    public int DisplayOrder { get; set; }
}
