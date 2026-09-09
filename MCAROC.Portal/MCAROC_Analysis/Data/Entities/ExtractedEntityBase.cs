namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// Common lineage fields for every row produced by Excel ingestion.
/// RequestId is denormalized for simple queries; IngestionRunId is the precise lineage key
/// (a request can have multiple runs over time, e.g. after a file replace).
/// </summary>
public abstract class ExtractedEntityBase
{
    public long RequestId { get; set; }
    public long IngestionRunId { get; set; }
    public long? SourceDocumentId { get; set; }
    public string? SourceSheetName { get; set; }
    public int? SourceRowNumber { get; set; }
}
