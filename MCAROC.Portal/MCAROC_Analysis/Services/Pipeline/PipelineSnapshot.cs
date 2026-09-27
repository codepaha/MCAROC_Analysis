using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Immutable facts about one request, read from the domain tables that already own them (never from
/// the pipeline's own tables) — the only input <see cref="PipelineDecider"/> gets besides policy. Every field
/// is plain data so the decider can be table-tested without a database.</summary>
public sealed record PipelineSnapshot
{
    public long RequestId { get; init; }
    public string? Cin { get; init; }
    public string? Llpin { get; init; }
    public RequestStatus RequestStatus { get; init; }
    public bool IsManualReviewRequired { get; init; }
    public string? ManualReviewReason { get; init; }
    public string? RequestFailureReason { get; init; }

    /// <summary>The request's most recent identity resolution (#294/#295), applied or not — what the Resolve stage
    /// reports while the request has no identifier, and a false accept that reopened it.</summary>
    public IdentityFacts? LatestResolution { get; init; }
    /// <summary>The most recent resolution that wrote the request's identifier — the input to the spend trust ladder.</summary>
    public IdentityFacts? AppliedResolution { get; init; }
    /// <summary><c>Resolve:SpendThreshold</c> at read time.</summary>
    public double SpendThreshold { get; init; } = new ResolverOptions().SpendThreshold;

    /// <summary>Null for a manual-upload request.</summary>
    public AutoFetchFacts? AutoFetch { get; init; }

    /// <summary>Whether auto-fetch gates exports on the unlock/refresh lifecycle (#229).</summary>
    public bool RefreshGateEnabled { get; init; }
    /// <summary>The company's unlock/refresh lifecycle, when auto-fetch has evaluated it.</summary>
    public LifecycleFacts? Lifecycle { get; init; }

    public long? LatestCompletedIngestionRunId { get; init; }
    public bool HasIngestionWarnings { get; init; }
    public bool IngestionRunning { get; init; }
    public IngestionRunStatus? LatestIngestionStatus { get; init; }

    /// <summary>The latest analysis run for <see cref="LatestCompletedIngestionRunId"/> — the same lineage
    /// rule the dossier uses.</summary>
    public RunFacts<AnalysisRunStatus>? Analysis { get; init; }

    public CalculationAssuranceMode CalcAssuranceMode { get; init; }
    public long? CalcAuditSnapshotId { get; init; }
    public CalculationAiAuditRunStatus? CalcAiAuditStatus { get; init; }
    /// <summary>True when an active artifact hold applies to the default dossier variant.</summary>
    public bool DossierHoldActive { get; init; }

    public FilingFacts? Filings { get; init; }

    public bool LitigationConfigured { get; init; }
    public LitigationSearchFacts? LitigationSearch { get; init; }
    public RunFacts<LitigationAiAnalysisRunStatus>? LitigationAnalysis { get; init; }
}

/// <param name="HeartbeatUtc">The worker's last progress signal while the job is claimed — updated
/// repeatedly through a download, not just once at claim time. Plan §6.3's stall signal for Fetch (and,
/// while filings download after the main export, for Filings too — see <see cref="PipelineDecider"/>).</param>
public sealed record AutoFetchFacts(
    long JobId, AutoFetchJobStatus Status, long? RocDocumentId, bool IncludeFilings, long? FilingBatchId, string? FailureReason,
    DateTime? HeartbeatUtc = null)
{
    public bool IsTerminal => Status is AutoFetchJobStatus.Completed or AutoFetchJobStatus.CompletedWithWarnings or AutoFetchJobStatus.Failed;
}

public sealed record LifecycleFacts(CompanyReportLifecycleState State, DateTime? UnlockedUtc, bool RefreshActive);

/// <param name="StartedUtc">Plan §6.3's stall signal for Analysis (AnalysisRun.StartedDate) — a fixed start
/// time, not a renewing heartbeat: the orchestrator is a single synchronous-ish pass with no progress
/// column, so "running longer than the threshold" is itself the stall signal.</param>
/// <param name="LeaseExpiresUtc">Plan §6.3's stall signal for Litigation/LitigationAnalysis: a fenced lease
/// (same pattern as <see cref="PipelineReconciler"/>'s own) that expired while the row still reads as
/// actively running means its holder died without finishing and without releasing it.</param>
public sealed record RunFacts<TStatus>(long Id, TStatus Status, string? FailureReason, DateTime? StartedUtc = null, DateTime? LeaseExpiresUtc = null)
    where TStatus : struct, Enum;

/// <param name="OutstandingChunks">Chunk-eligible documents (non-duplicate, processing completed) still
/// Pending or InProgress — the same eligibility rule the chunking orchestrator uses.</param>
public sealed record FilingFacts(long BatchId, FilingBatchStatus Status, string? FailureReason, int OutstandingChunks, int FailedChunks);

/// <param name="OrdersFullyProcessed">True once every order document for the request's cases has finished
/// downloading (terminal: Downloaded/Expired) and, where text was actually retrieved, finished chunking
/// (terminal: Chunked/Failed) — an Expired document never gets text, so chunking is trivially done for it.
/// True (nothing to wait for) when the request has no orders at all. Gates litigation-analysis auto-start
/// (plan §4.2); a manual start is never gated by this.</param>
/// <param name="HasStalledOrderDownload">An order document still <c>Pending</c> five or more days after it
/// was first recorded — plan §4.2's <c>ORDER_DOWNLOAD_STALLED</c> signal.</param>
/// <param name="LeaseExpiresUtc">Plan §6.3's runtime-stall signal for the search job itself — see
/// <see cref="RunFacts{TStatus}"/>'s own doc for why an expired lease on a nominally-running row means the
/// worker died mid-task.</param>
/// <param name="SnapshotLeaseExpiresUtc">The same signal for the import that follows the search job — a
/// separate crash-safe unit of work with its own lease on <c>LitigationReportSnapshot</c>. The job's own
/// lease (above) is already released by the time the import claims this one, so it says nothing about
/// whether the import itself is still alive.</param>
public sealed record LitigationSearchFacts(
    long JobId, LitigationSearchJobStatus Status, string? FailureReason, long? SnapshotId, LitigationReportSnapshotStatus? SnapshotStatus,
    bool OrdersFullyProcessed = true, bool HasStalledOrderDownload = false, DateTime? LeaseExpiresUtc = null, DateTime? SnapshotLeaseExpiresUtc = null);
