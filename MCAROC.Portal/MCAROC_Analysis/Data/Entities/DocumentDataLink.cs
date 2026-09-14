namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// Represents an evidence link connecting an MCA filing document (PDF) to an ingested entity
/// (e.g. RocCharge, RocChargeEvent, FinancialFact) within a request and ingestion run.
/// </summary>
public class DocumentDataLink
{
    public long DocumentDataLinkId { get; set; }

    /// <summary>Mandatory request scope and authorization boundary.</summary>
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    /// <summary>Mandatory source-data version; the run whose entity is being evidenced.</summary>
    public long IngestionRunId { get; set; }
    public IngestionRun? IngestionRun { get; set; }

    /// <summary>Immutable actual McaFilingDocument containing the evidence.</summary>
    public long FilingDocumentId { get; set; }
    public McaFilingDocument? FilingDocument { get; set; }

    /// <summary>Current canonical PDF identity used for deduplicated display and active-link uniqueness.</summary>
    public long CanonicalFilingDocumentId { get; set; }
    public McaFilingDocument? CanonicalFilingDocument { get; set; }

    /// <summary>Polymorphic target entity discriminator (e.g. "RocCharge", "RocChargeEvent").</summary>
    public string TargetEntityType { get; set; } = string.Empty;

    /// <summary>Primary key ID of the target entity.</summary>
    public long TargetEntityId { get; set; }

    /// <summary>Nullable field/property identifier; null means entity-level evidence.</summary>
    public string? TargetField { get; set; }

    public DocumentLinkKind LinkKind { get; set; } = DocumentLinkKind.Supports;
    public DocumentDataLinkStatus Status { get; set; } = DocumentDataLinkStatus.PendingReview;
    public DocumentLinkMatchMethod MatchMethod { get; set; } = DocumentLinkMatchMethod.DirectProvenance;
    public DocumentLinkConfidence Confidence { get; set; } = DocumentLinkConfidence.High;

    /// <summary>Versioned evidence payload: identifiers, normalized values, PDF page/text quote, and source workbook sheet/row.</summary>
    public string EvidenceJson { get; set; } = "{}";

    /// <summary>Rule engine version producing the link candidate.</summary>
    public string RuleVersion { get; set; } = "1.0.0";

    /// <summary>Hash of input extraction and target entity data to ensure idempotency and detect changes.</summary>
    public string InputHash { get; set; } = string.Empty;

    // Audit fields
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedUtc { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewReason { get; set; }

    // Navigation properties for supersession audit
    public List<DocumentDataLinkSupersession> SupersededByLinks { get; set; } = [];
    public List<DocumentDataLinkSupersession> SupersedesLinks { get; set; } = [];
}
