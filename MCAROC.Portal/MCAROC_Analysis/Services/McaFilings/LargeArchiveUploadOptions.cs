namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>
/// Configuration options for resumable large MCA filings archive upload and storage admission.
/// Bound from configuration section "LargeArchiveUpload".
/// </summary>
public class LargeArchiveUploadOptions
{
    public const string SectionName = "LargeArchiveUpload";

    /// <summary>Feature flag controlling large archive upload capability. Defaulted to false for phased rollout.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Maximum allowed declared archive size in bytes. Default is 5 GiB (5,368,709,120 bytes).</summary>
    public long MaxArchiveSizeBytes { get; set; } = 5_368_709_120L;

    /// <summary>Chunk size in bytes. Default is 64 MiB (67,108,864 bytes).</summary>
    public int ChunkSizeBytes { get; set; } = 67_108_864;

    /// <summary>Maximum allowed request body size for chunk endpoint including framing overhead (65 MiB).</summary>
    public const long MaxChunkRequestSizeBytes = 68_157_440L;

    /// <summary>Maximum concurrent active large uploads permitted globally. Enforced by
    /// OperationalSlotLeaseService's LargeUpload slot — raising it lets that many resumable large-archive
    /// uploads finalize at once instead of one queuing behind another.</summary>
    public int MaxConcurrentUploads { get; set; } = 1;

    /// <summary>Maximum concurrent active large unpacks permitted globally. Enforced by
    /// OperationalSlotLeaseService's LargeUnpack slot (FilingBatchProcessor.UnpackBatchAsync) — raising it
    /// lets that many requests' MCA filings archives unpack at once instead of every batch but one queuing
    /// behind whichever unpack started first. Also sizes FilingProcessingWorker's own unpack-dispatch
    /// concurrency, so the two stay consistent with each other.</summary>
    public int MaxConcurrentUnpacks { get; set; } = 1;

    /// <summary>Maximum concurrent MCA filing documents undergoing OCR/text-extraction and classification
    /// at once (FilingProcessingWorker) — CPU/disk-bound, so this is safe to raise on a machine with spare
    /// cores independent of MaxConcurrentUnpacks.</summary>
    public int MaxConcurrentDocumentProcessing { get; set; } = 4;

    /// <summary>Maximum concurrent Gemini structured-extraction calls at once (FilingProcessingWorker) —
    /// externally rate-limited by the Vertex AI project's own quota. Raise only after checking that quota;
    /// raising this past what the quota actually allows just trades faster local dispatch for more 429s.</summary>
    public int MaxConcurrentAiExtraction { get; set; } = 2;

    /// <summary>Maximum concurrent document-chunk embedding calls at once (DocumentChunkingWorker) — same
    /// externally-rate-limited consideration as MaxConcurrentAiExtraction, against the embedding model's
    /// own quota rather than the extraction model's.</summary>
    public int MaxConcurrentChunking { get; set; } = 4;

    /// <summary>Maximum cumulative uncompressed size in bytes across all extracted PDFs (guards zip bombs).</summary>
    public long MaxUncompressedSizeBytes { get; set; } = 21_474_836_480L; // 20 GiB

    /// <summary>Maximum cumulative count of PDFs across the batch.</summary>
    public int MaxPdfCount { get; set; } = 10_000;

    /// <summary>Maximum individual PDF file size in bytes.</summary>
    public long MaxIndividualPdfSizeBytes { get; set; } = 250_000_000L; // 250 MB

    /// <summary>Estimated maximum temporary nested zip extraction buffer in bytes.</summary>
    public long MaxNestedTempBytes { get; set; } = 1_073_741_824L; // 1 GiB

    /// <summary>Minimum free disk headroom in bytes required on the destination volume after all reservations.</summary>
    public long MinFreeDiskHeadroomBytes { get; set; } = 10_737_418_240L; // 10 GiB

    /// <summary>Upload session lifetime before expiry.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Heartbeat / sliding renewal lease interval.</summary>
    public TimeSpan SlotLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Heartbeat renewal interval for active workers / uploaders.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Base delay before re-enqueueing an unpack batch when the operational slot is busy.</summary>
    public TimeSpan SlotRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum capped delay before re-enqueueing an unpack batch during slot contention.</summary>
    public TimeSpan MaxSlotRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Calculates the worst-case space budget required on the destination volume for an archive of the given declared size.</summary>
    public long CalculateDestinationVolumeWorstCaseBytes(long declaredArchiveSizeBytes)
    {
        // Declared archive + worst-case uncompressed PDFs ceiling + nested temp buffer + required free headroom
        return declaredArchiveSizeBytes + MaxUncompressedSizeBytes + MaxNestedTempBytes + MinFreeDiskHeadroomBytes;
    }

    /// <summary>Calculates the space budget required on the staging volume for an archive of the given declared size.</summary>
    public long CalculateStagingVolumeBytes(long declaredArchiveSizeBytes)
    {
        // Declared archive + single chunk buffer
        return declaredArchiveSizeBytes + ChunkSizeBytes;
    }
}
