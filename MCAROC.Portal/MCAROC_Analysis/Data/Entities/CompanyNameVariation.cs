namespace MCAROC_Analysis.Data.Entities;

/// <summary>A variation of a company's name that a person added on the Litigation tab, for when the cases are filed under a form the
/// generated keywords miss (owner, 2026-10-26). Kept per company (CIN/LLPIN), so every later request and refresh for it searches the variation
/// too. A party with exactly this name is the company's (attribution rule <c>exact-name-variation</c>); a party that only resembles it is judged
/// as usual. A removed variation stays for audit: it is no longer searched, and the cases it alone decided are assessed again.</summary>
public class CompanyNameVariation
{
    public long CompanyNameVariationId { get; set; }

    /// <summary>The company's CIN or LLPIN, upper case.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>The variation as entered, spaces collapsed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The whole-name canonical form (<c>WholeNamePartyMatcher.Canonical</c>), unique among the company's active variations.</summary>
    public string NameKey { get; set; } = string.Empty;

    public string AddedBy { get; set; } = string.Empty;
    public DateTime AddedUtc { get; set; }

    /// <summary>The request it was added on: that request's search is re-run with it.</summary>
    public long AddedOnRequestId { get; set; }

    public string? RemovedBy { get; set; }
    public DateTime? RemovedUtc { get; set; }

    public bool IsActive => RemovedUtc is null;
}
