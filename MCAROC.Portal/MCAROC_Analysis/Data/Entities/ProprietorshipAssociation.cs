namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Proprietorship" sheet — proprietorship / sole-trader businesses a
/// director is (or was) associated with.</summary>
public class ProprietorshipAssociation : ExtractedEntityBase
{
    public long ProprietorshipAssociationId { get; set; }

    public string DirectorDin { get; set; } = string.Empty;
    public string DirectorNameRaw { get; set; } = string.Empty;

    public string? LegalName { get; set; }
    public string? BusinessNames { get; set; }
    public string? Pan { get; set; }
    public string? Status { get; set; }
}
