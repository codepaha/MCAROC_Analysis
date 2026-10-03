namespace MCAROC_Analysis.Data.Entities;

/// <summary>#377: how a filing document was tied to a charge. Every method is exact; nothing is inferred.</summary>
public enum ChargeDocumentLinkMethod
{
    /// <summary>The e-form states the charge ID (Form 8 / CHG-1 modification, Form 17 / CHG-4 satisfaction).</summary>
    FormChargeId,
    /// <summary>A creation form (filed type CRTN, before the Registrar issues an ID): exactly one charge has a creation
    /// event on the instrument date for the same amount.</summary>
    CreationDateAndAmount,
    /// <summary>The document is byte-identical to a file embedded in a form linked to the charge (one of its attachments).</summary>
    EmbeddedAttachment,
    /// <summary>The file name states the charge ID ("…ChargeId-10215822…") and the document's own form data doesn't name
    /// a different charge.</summary>
    FileName
}

/// <summary>#377: one filing document tied to one charge of the request's register — open or satisfied. A document can
/// belong to several charges (an agreement attached to several charges' forms). Keyed by the register's charge number,
/// not the <see cref="RocCharge"/> row, which a re-ingest replaces. Rebuilt per batch by <c>ChargeDocumentLinkBuilder</c>.</summary>
public class ChargeDocumentLink
{
    public long ChargeDocumentLinkId { get; set; }
    public long RequestId { get; set; }
    public long BatchId { get; set; }

    /// <summary>The register's charge ID, leading zeros removed (as <c>RocCharge.RocChargeNumber</c> trimmed).</summary>
    public string RocChargeNumber { get; set; } = string.Empty;

    public long FilingDocumentId { get; set; }
    public McaFilingDocument? FilingDocument { get; set; }

    public ChargeDocumentLinkMethod Method { get; set; }

    /// <summary>For <see cref="ChargeDocumentLinkMethod.EmbeddedAttachment"/>: the form the document is attached to.</summary>
    public long? LinkedFromDocumentId { get; set; }
}
