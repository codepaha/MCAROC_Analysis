namespace MCAROC_Analysis.Data.Entities;

/// <summary>Lifecycle of retrieving and retaining one <see cref="LitigationCaseOrder"/>'s PDF (#243/LIT-03).
/// <see cref="Downloaded"/> and <see cref="Expired"/> are terminal; <see cref="Failed"/> is deliberately NOT
/// terminal — epic #239 requires "no case is falsely reported as complete when an order download fails," so
/// a failed attempt stays retryable (via the same lease/claim mechanism <see cref="LitigationReportSnapshot"/>
/// uses) until <see cref="LitigationOrderDocument.RetainedUntilUtc"/> passes, at which point recovery moves it
/// to <see cref="Expired"/> — an explicit, final "we can no longer get this order" state, never a silent gap.</summary>
public enum LitigationOrderDocumentStatus
{
    Pending,
    InProgress,
    Downloaded,
    Failed,
    Expired
}

/// <summary>One row per <see cref="LitigationCaseOrder"/> (1:1, unique on <see cref="LitigationCaseOrderId"/>),
/// tracking the crash-safe, concurrency-safe retrieval of that order's PDF from its vendor-hosted
/// <see cref="LitigationCaseOrder.PdfUrl"/>, the file's retention window, and the text extracted from it.
///
/// <b>Why retention is tracked here, not just "download and forget":</b> epic #239's confirmed product
/// decision is that the vendor's original PDF URL may expire seven days after it was surfaced — before that
/// deadline (<see cref="RetainedUntilUtc"/>, computed from the source report's own retrieval time, not from
/// whenever we happen to get around to downloading), a failed attempt is always worth retrying; after it, the
/// URL is presumed dead and the row moves to <see cref="LitigationOrderDocumentStatus.Expired"/> rather than
/// being retried forever. <see cref="ExtractedText"/> is retained independently of the raw PDF file's own
/// fate — extracting it is the whole point of retrieving the order before its window closes (see
/// docs/litigation-data-lake-integration.md's "New durable records").
///
/// <b>Refresh is explicit and auditable</b> (a second confirmed #239 requirement): the confirmed BPR contract
/// has no per-order refetch endpoint, so a "refresh" is always a full rerun of the parent search
/// (<c>LitigationSearchJobService.CreateOrResetJobAsync</c>) producing a fresh <see cref="LitigationReportSnapshot"/>
/// with — potentially — a fresh <see cref="LitigationCaseOrder.PdfUrl"/> for the same real-world order.
/// <see cref="RefreshCount"/>/<see cref="LastRefreshedUtc"/> record every time an already-<see
/// cref="LitigationOrderDocumentStatus.Failed"/> or <see cref="LitigationOrderDocumentStatus.Expired"/> row
/// gets a genuinely new retrieval attempt this way, distinguishing "never tried again" from "tried again and
/// still failed."
///
/// <b>Crash/concurrency safety</b> starts like <see cref="LitigationReportSnapshot"/>'s own lease pattern —
/// <see cref="LeaseOwner"/>/<see cref="LeaseToken"/>/<see cref="LeaseExpiresUtc"/> make claiming one document
/// for download atomic and reclaimable after a crash, with <see cref="RowVersion"/> protecting that CLAIM
/// step specifically — but goes further for every write after the claim: this row's download involves an
/// out-of-transaction side effect (a file write) that RowVersion alone cannot protect, so every subsequent
/// write is instead an explicit, lease-token-and-expiry-guarded <c>ExecuteUpdateAsync</c>, and the file itself
/// is written to a path keyed by the claiming attempt's own <see cref="LeaseToken"/> (see <see
/// cref="StoragePath"/>) rather than a fixed name — see <c>LitigationOrderDocumentService</c>'s own remarks
/// for the exact failure this closes: a stale worker whose lease has been superseded by a takeover finishing
/// its download late and corrupting the winner's file on disk even though the database row is otherwise
/// correctly protected.</summary>
public sealed class LitigationOrderDocument
{
    public long LitigationOrderDocumentId { get; set; }
    public long LitigationCaseOrderId { get; set; }
    public LitigationCaseOrder? Order { get; set; }

    public LitigationOrderDocumentStatus Status { get; set; } = LitigationOrderDocumentStatus.Pending;

    /// <summary>The deadline by which this order's vendor PDF URL is expected to still be retrievable —
    /// source report's <c>RetrievedUtc</c> plus <c>BprLitigationOptions.OrderRetentionDays</c> (default 7,
    /// matching epic #239's confirmed decision), computed once at row creation and never moved by a failed
    /// attempt. A rerun that re-surfaces this same order via a new snapshot gets an explicit refresh (see
    /// <c>LitigationCasePersistenceService.UpsertOrdersAsync</c>, which admits/refreshes this row inside the
    /// same transaction as the order it belongs to), which is how "refresh" effectively extends
    /// retrievability rather than this field being pushed forward in place.</summary>
    public DateTime RetainedUntilUtc { get; set; }

    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }

    /// <summary>Path to the retained PDF, named <c>{LitigationOrderDocumentId}-{LeaseToken:N}.pdf</c> — keyed
    /// by the WINNING attempt's own lease token, never a fixed/shared name, so a stale attempt's late file
    /// write can never land on the same path as the attempt that actually published this row. See
    /// <c>LitigationOrderDocumentService</c>'s own remarks for why a fixed path is unsafe here.</summary>
    public string? StoragePath { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileHash { get; set; }
    public string? ContentType { get; set; }
    public DateTime? DownloadedUtc { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>Retained independently of <see cref="StoragePath"/>'s own file — see this type's remarks.</summary>
    public string? ExtractedText { get; set; }

    /// <summary>Only ever one of the three values <c>PdfTextExtractor.ExtractAsync</c> can actually return
    /// (CorruptPdf, PasswordProtected, TextExtracted) — null means extraction has not been attempted yet.
    /// Reuses <c>Services.McaFilings.FilingDocumentProcessingStatus</c> rather than a parallel enum solely for
    /// that return type; this entity never uses any of its McaFiling-pipeline-specific members.</summary>
    public FilingDocumentProcessingStatus? TextExtractionStatus { get; set; }

    public TextExtractionMethod? TextExtractionMethod { get; set; }
    public DateTime? ExtractedUtc { get; set; }

    public int RefreshCount { get; set; }
    public DateTime? LastRefreshedUtc { get; set; }

    public byte[]? RowVersion { get; set; }

    public DateTime CreatedUtc { get; set; }

    public bool IsTerminal => Status is LitigationOrderDocumentStatus.Downloaded or LitigationOrderDocumentStatus.Expired;
}
