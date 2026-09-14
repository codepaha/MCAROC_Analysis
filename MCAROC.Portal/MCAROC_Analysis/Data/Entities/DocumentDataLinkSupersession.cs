namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// First-class schema contract for audit preservation when late deduplication merges
/// two active document data links into a single canonical identity.
/// </summary>
public class DocumentDataLinkSupersession
{
    public long DocumentDataLinkSupersessionId { get; set; }

    /// <summary>Mandatory request scope and authorization boundary.</summary>
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    /// <summary>Mandatory source-data version; the run scope.</summary>
    public long IngestionRunId { get; set; }
    public IngestionRun? IngestionRun { get; set; }

    /// <summary>The link that has been marked SupersededByDuplicate.</summary>
    public long SupersededLinkId { get; set; }
    public DocumentDataLink? SupersededLink { get; set; }

    /// <summary>The surviving active link under the canonical identity.</summary>
    public long SurvivingLinkId { get; set; }
    public DocumentDataLink? SurvivingLink { get; set; }

    /// <summary>Exact UTC timestamp when supersession occurred.</summary>
    public DateTime SupersededUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Actor triggering the supersession (system or reviewer username).</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Business rationale or canonicalization trigger.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Immutable snapshot of the superseded link's EvidenceJson and review state.</summary>
    public string EvidencePreservationJson { get; set; } = "{}";
}
