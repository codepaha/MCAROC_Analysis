namespace MCAROC_Analysis.Data.Entities;

public enum DocumentDerivativeType { SanitizedExcel = 1 }
public enum DocumentDerivativeStatus { Pending = 0, Ready = 1, Failed = 2 }

public class RequestDocumentDerivative
{
    public long DerivativeId { get; set; }
    public long DocumentId { get; set; }
    public RequestDocument? Document { get; set; }
    public long RequestId { get; set; }
    public DocumentDerivativeType DerivativeType { get; set; } = DocumentDerivativeType.SanitizedExcel;
    public int SanitizerVersion { get; set; } = 1;
    public string RawFileHash { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public DocumentDerivativeStatus Status { get; set; } = DocumentDerivativeStatus.Pending;
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

