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

    /// <summary>Derived from the latest completed run's WarningsCount; kept separate from RequestStatus.</summary>
    public bool HasIngestionWarnings { get; set; }

    public List<RequestDocument> Documents { get; set; } = [];
    public List<IngestionRun> IngestionRuns { get; set; } = [];
}
