namespace MCAROC_Analysis.Data.Entities;

public class RequestDocument
{
    public long DocumentId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    public DocumentType DocumentType { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;

    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;

    public DocumentUploadStatus UploadStatus { get; set; } = DocumentUploadStatus.Uploaded;

    /// <summary>Set when UploadStatus = Quarantined, e.g. "CIN does not match ROC report".</summary>
    public string? QuarantineReason { get; set; }

    public string? UploadedBy { get; set; }
    public DateTime UploadedDate { get; set; }

    /// <summary>True if this document is the authoritative source for this RequestId and DocumentType.</summary>
    public bool IsActiveSource { get; set; }

    /// <summary>Self-reference linking to the document that superseded this one.</summary>
    public long? SupersededByDocumentId { get; set; }
    public RequestDocument? SupersededByDocument { get; set; }

    /// <summary>Upload session ID if created via resumable large archive upload.</summary>
    public Guid? UploadSessionId { get; set; }
}
