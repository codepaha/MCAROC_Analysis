namespace MCAROC_Analysis.Data.Entities;

public class Litigation : ExtractedEntityBase
{
    public long LitigationId { get; set; }

    public string? CaseType { get; set; }
    public string? CaseStatus { get; set; }
    public string? CaseCategory { get; set; }
    public string? Court { get; set; }
    public string? Litigants { get; set; }
    public string? CaseNumber { get; set; }
    public DateOnly? LastHearingDate { get; set; }

    /// <summary>Legal History is pre-resolved to this company by the data vendor, so Phase 1 sets Confirmed.
    /// The enum exists for a future PDF-based extraction phase that will need real probable-match logic.</summary>
    public LitigationMatchStatus MatchStatus { get; set; } = LitigationMatchStatus.Confirmed;

    /// <summary>Provenance. Every Phase 1 row is <see cref="LitigationSource.RocReport"/>; a later manual
    /// data-lake pull will add <see cref="LitigationSource.DataLake"/> rows alongside.</summary>
    public LitigationSource Source { get; set; } = LitigationSource.RocReport;
}
