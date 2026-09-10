namespace MCAROC_Analysis.Data.Entities;

/// <summary>One contact email from the "About the Company" sheet. The source lists several and flags
/// the ones it could not reach ("* Our attempt to reach this email was not successful.") — we keep
/// every address plus that reachability signal rather than collapsing to one string.</summary>
public sealed class CompanyEmail : ExtractedEntityBase
{
    public long CompanyEmailId { get; set; }

    public string EmailAddress { get; set; } = string.Empty;

    /// <summary>null = not asserted by the source; false = the source marked it unreachable.</summary>
    public bool? IsReachable { get; set; }
}
