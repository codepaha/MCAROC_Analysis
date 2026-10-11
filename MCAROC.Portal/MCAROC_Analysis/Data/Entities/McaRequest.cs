namespace MCAROC_Analysis.Data.Entities;

public class McaRequest
{
    public long RequestId { get; set; }

    /// <summary>Display identifier, e.g. "MCA-20260908-000125". Generated from RequestId after insert.</summary>
    public string RequestNumber { get; set; } = string.Empty;

    public long ClientId { get; set; }
    public Client? Client { get; set; }

    public EntityType EntityType { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? Cin { get; set; }
    public string? Llpin { get; set; }
    public string? Pan { get; set; }

    /// <summary>
    /// Canonical CIN/LLPIN used only by the AutoFetch entry point to make a company's request
    /// idempotent within its owning client. It is deliberately client-scoped: another client may
    /// have an independent request for the same company, but must never see or reuse this request.
    /// </summary>
    public string? AutoFetchCompanyIdentifier { get; set; }

    public RequestStatus RequestStatus { get; set; } = RequestStatus.Created;

    public string? CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedDate { get; set; }

    public DateTime? AnalysisStartedDate { get; set; }
    public DateTime? AnalysisCompletedDate { get; set; }

    public bool IsManualReviewRequired { get; set; }
    public string? ManualReviewReason { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>The most recent successfully-completed ingestion run. Details/current-state queries filter on this.</summary>
    public long? LatestCompletedIngestionRunId { get; set; }

    /// <summary>Changes whenever live court data for this request changes (a publication to its cases, a discovery, a merge or un-merge, a hide or un-hide, a new order or hearing).
    /// Incremented atomically by <see cref="MCAROC_Analysis.Services.LitigationModule.Refresh.RequestLiveData"/>; read by the page fence and every cache that is derived from litigation data.</summary>
    public long LiveDataVersion { get; set; }

    /// <summary>Set when a failed analysis is waiting out its automatic-retry delay: the earliest time (UTC) it may be queued again.
    /// Survives a restart, so recovery honours the remaining wait instead of retrying at once.</summary>
    public DateTime? AnalysisRetryAfter { get; set; }

    /// <summary>Derived from the latest completed run's WarningsCount; kept separate from RequestStatus.</summary>
    public bool HasIngestionWarnings { get; set; }

    /// <summary>#376: Keep original filing PDFs permanently regardless of global retention policy.</summary>
    public bool KeepPermanently { get; set; }

    public byte[]? RowVersion { get; set; }

    public List<RequestDocument> Documents { get; set; } = [];
    public List<IngestionRun> IngestionRuns { get; set; } = [];
}
