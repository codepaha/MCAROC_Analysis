namespace MCAROC_Analysis.Data.Entities;

public enum LargeArchiveUploadSessionStatus
{
    Uploading,
    Verifying,
    ArchiveMoved,
    DocumentCommitted,
    BatchCreated,
    Queued,
    Completed,
    Failed,
    Expired,
    Aborted
}

/// <summary>
/// Durable state for a resumable MCA-filings archive upload session.
/// </summary>
public class LargeArchiveUploadSession
{
    public Guid SessionId { get; set; }

    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    /// <summary>SHA-256 hash of the client's opaque capability token. Token is never stored in plaintext.</summary>
    public string HashedCapabilityToken { get; set; } = string.Empty;

    /// <summary>Durable correlation ID initiated by the HTTP request and propagated to created batches and worker events.</summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    public string OriginalFileName { get; set; } = string.Empty;
    public long TotalExpectedSizeBytes { get; set; }
    public long NextExpectedOffset { get; set; }

    public string ExpectedFullSha256 { get; set; } = string.Empty;

    public LargeArchiveUploadSessionStatus Status { get; set; } = LargeArchiveUploadSessionStatus.Uploading;

    /// <summary>Absolute path to the staging archive.part file on disk.</summary>
    public string StagingFilePath { get; set; } = string.Empty;

    /// <summary>Pre-allocated destination path under App_Data/Uploads/{requestId}/original/.</summary>
    public string DestinationStoragePath { get; set; } = string.Empty;

    /// <summary>Current active write attempt ID holding the write lease for chunk append.</summary>
    public Guid? ActiveWriteAttemptId { get; set; }
    public long? ActiveWriteOffset { get; set; }
    public DateTime? ActiveWriteExpiresUtc { get; set; }

    /// <summary>Exclusive lease attempt ID for finalization (Verifying -> ArchiveMoved -> DocumentCommitted -> BatchCreated -> Completed).</summary>
    public Guid? FinalizationAttemptId { get; set; }
    public DateTime? ActiveFinalizationExpiresUtc { get; set; }

    /// <summary>Resulting RequestDocument ID after finalization.</summary>
    public long? CreatedDocumentId { get; set; }

    /// <summary>Resulting McaFilingBatch ID after finalization.</summary>
    public long? CreatedBatchId { get; set; }

    /// <summary>Internal audit failure reason. Never exposed to customer-facing reports or views.</summary>
    public string? FailureReason { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public byte[]? RowVersion { get; set; }
}
