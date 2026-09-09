namespace MCAROC_Analysis.Data.Entities;

public class IngestionRun
{
    public long IngestionRunId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    /// <summary>Sequential per request: 1, 2, 3... incremented on each reprocessing attempt.</summary>
    public int RunNumber { get; set; }

    public DateTime StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }

    public IngestionRunStatus Status { get; set; } = IngestionRunStatus.Running;

    /// <summary>Bumped manually when parser logic changes materially.</summary>
    public string ParserVersion { get; set; } = "1.0";

    public long? SourceRocDocumentId { get; set; }
    public long? SourceChargeDocumentId { get; set; }

    public int RowsExtracted { get; set; }
    public int WarningsCount { get; set; }
    public int ErrorsCount { get; set; }

    public string? FailureReason { get; set; }

    public List<IngestionIssue> Issues { get; set; } = [];
}
