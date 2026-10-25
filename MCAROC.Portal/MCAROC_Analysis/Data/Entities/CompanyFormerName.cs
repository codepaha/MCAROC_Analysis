namespace MCAROC_Analysis.Data.Entities;

/// <summary>A name a company was registered under before (#497), learned from the reference tool's search, where a hit's <c>context</c> is
/// <c>oldName</c>. Our master register holds current names only, so a client asking for a company by a name it no longer has would otherwise find
/// nothing, or find the different company that holds that name today. Kept so a later request by the same former name resolves without a search.</summary>
public sealed class CompanyFormerName
{
    public long CompanyFormerNameId { get; set; }

    /// <summary>The company's CIN/LLPIN.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>The former name as the source wrote it.</summary>
    public string FormerName { get; set; } = string.Empty;

    /// <summary><see cref="Services.CompanyMaster.CompanyNameNormalizer"/>'s normalised form of <see cref="FormerName"/>: what a request is matched on.</summary>
    public string FormerNameNormalized { get; set; } = string.Empty;

    /// <summary>The company's legal name when the former name was seen, for the record; the current name is read from the master by identifier.</summary>
    public string? CurrentNameWhenSeen { get; set; }

    /// <summary>Where it was learned, e.g. <c>reference-tool-search</c>.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}
