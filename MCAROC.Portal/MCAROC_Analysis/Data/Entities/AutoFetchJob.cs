namespace MCAROC_Analysis.Data.Entities;

/// <summary>Stages of one auto-fetch run, in order. Everything before <see cref="Completed"/> /
/// <see cref="CompletedWithWarnings"/> / <see cref="Failed"/> is non-terminal and is reset to
/// <see cref="Queued"/> by the worker's startup recovery sweep.</summary>
public enum AutoFetchJobStatus
{
    Queued,
    /// <summary>Parked (holding no worker slot) until the reference tool finishes refreshing the company's
    /// data; <c>CompanyRefreshWorker</c> re-queues it. Nothing has been exported yet.</summary>
    WaitingForRefresh,
    /// <summary>Parked because the company is locked in the reference tool: waiting for a human to approve the
    /// 1-credit unlock (#266). Nothing has been exported yet.</summary>
    WaitingForUnlock,
    CheckingSession,
    FetchingWorkbooks,
    Ingesting,
    FetchingRegistry,
    DownloadingFilings,
    Packaging,
    Completed,
    CompletedWithWarnings,
    Failed
}

/// <summary>One "give me everything for this CIN" run against the reference tool for a request: the two
/// workbook exports → ingestion (+ analysis), then every filing PDF → a per-filing nested-zip archive →
/// the MCA filings batch (OCR, Gemini extraction, chunking/embedding all follow from there as usual).
/// Progress is written here so the Details page can poll it; a request has at most one job, re-run in
/// place by the Retry action.</summary>
public sealed class AutoFetchJob
{
    public long AutoFetchJobId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    public string Cin { get; set; } = string.Empty;
    /// <summary>The tool's business id — <c>sha256(upper(CIN))</c>, stored so the job never recomputes it.</summary>
    public string Bid { get; set; } = string.Empty;

    /// <summary>Durable correlation ID initiated by the HTTP request and propagated to created batches and worker events.</summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    public AutoFetchJobStatus Status { get; set; } = AutoFetchJobStatus.Queued;
    public int ProgressPercent { get; set; }
    /// <summary>Short human-readable line for the current stage, e.g. "Downloading filings 412 / 1,830".</summary>
    public string? StatusMessage { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>JSON array of non-fatal problems (a charge workbook that could not be exported, PDFs that
    /// failed after every retry, a registry that listed fewer documents than it reported…).</summary>
    public string WarningsJson { get; set; } = "[]";

    public bool IncludeFilings { get; set; } = true;
    /// <summary>0 = every document the registry lists for each section.</summary>
    public int MaxDocumentsPerSection { get; set; }

    // Registry / download counters (filled in as the stages run)
    public int RegistryTotalCount { get; set; }
    public int RegistryListedCount { get; set; }
    public int FilesTotal { get; set; }
    public int FilesDownloaded { get; set; }
    public int FilesFailed { get; set; }
    public long BytesDownloaded { get; set; }

    // Stage checkpoints — a resumed job skips every stage whose output already exists.
    public long? RocDocumentId { get; set; }
    public long? ChargeDocumentId { get; set; }
    public long? IngestionRunId { get; set; }
    public long? FilingsDocumentId { get; set; }
    public long? FilingBatchId { get; set; }

    public int AttemptCount { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? HeartbeatUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public bool IsTerminal => Status is AutoFetchJobStatus.Completed or AutoFetchJobStatus.CompletedWithWarnings or AutoFetchJobStatus.Failed;
}
