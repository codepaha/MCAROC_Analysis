namespace MCAROC_Analysis.Data.Entities;

/// <summary>One reference-tool search for a requested name, kept whole (#497): who had the name before, who has it today, and whether the search was
/// read to its end. A later request by the same name is decided from exactly what this search showed, conflicts included, until it is
/// <see cref="Services.CompanyMaster.FormerNameLookupService.MaxAge"/> old; then the search runs again. A search that was not read to its end is kept as
/// such, and never lets a company be chosen automatically.</summary>
public sealed class CompanyNameSearch
{
    public long CompanyNameSearchId { get; set; }

    /// <summary>The searched name, normalised (<see cref="Services.CompanyMaster.CompanyNameNormalizer"/>).</summary>
    public string NameNormalized { get; set; } = string.Empty;

    public DateTime SearchedUtc { get; set; }

    /// <summary>Whether the search was read to its end.</summary>
    public bool Complete { get; set; }

    /// <summary>The former and current holders the search reported, as JSON.</summary>
    public string ResultJson { get; set; } = "{}";
}
