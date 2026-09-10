namespace MCAROC_Analysis.Data.Entities;

/// <summary>Per-establishment header from the "EPFO Establishments" sheet — the metadata the monthly
/// "Annexure - EPFO Establishments" rows (<see cref="EpfoContribution"/>) do not carry: city, date of
/// setup, principal business activities, address, exemption status, flags. One row per
/// <see cref="EstablishmentId"/>; <see cref="EpfoContribution"/> joins by that id.</summary>
public class EpfoEstablishment : ExtractedEntityBase
{
    public long EpfoEstablishmentId { get; set; }

    public string EstablishmentId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? City { get; set; }
    public DateOnly? DateOfSetup { get; set; }
    public string? PrincipalBusinessActivities { get; set; }
    public string? Address { get; set; }
    public string? ExemptionStatus { get; set; }
    public string? WorkingStatus { get; set; }
    public string? Flags { get; set; }

    /// <summary>Verbatim "LATEST WAGE MONTH" cell (e.g. "May, 2026" or "-") — the monthly history in
    /// <see cref="EpfoContribution"/> is authoritative; this is kept only as the summary sheet reported it.</summary>
    public string? LatestWageMonth { get; set; }
}
