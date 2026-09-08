namespace MCAROC_Analysis.Data.Entities;

/// <summary>One MCA Filings zip upload. Progress counts are computed on read (COUNT(*) over
/// McaFilingDocument.ProcessingStatus), not stored/incremented — avoids races between concurrent workers.</summary>
public class McaFilingBatch
{
    public long BatchId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    /// <summary>The outer zip's RequestDocument (DocumentType.McaFilingsArchive).</summary>
    public long SourceDocumentId { get; set; }

    public FilingBatchStatus Status { get; set; } = FilingBatchStatus.Uploaded;
    public string? FailureReason { get; set; }

    public DateTime StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }

    public List<McaFiling> Filings { get; set; } = [];
}
