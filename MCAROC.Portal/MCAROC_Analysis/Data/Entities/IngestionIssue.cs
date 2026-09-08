namespace MCAROC_Analysis.Data.Entities;

public class IngestionIssue
{
    public long IssueId { get; set; }
    public long IngestionRunId { get; set; }
    public IngestionRun? IngestionRun { get; set; }

    public IssueSeverity Severity { get; set; }
    public long? DocumentId { get; set; }

    public string? SheetName { get; set; }
    public int? RowNumber { get; set; }

    public string ParserName { get; set; } = string.Empty;
    public string? FieldName { get; set; }

    /// <summary>Original cell value preserved here when it failed to parse.</summary>
    public string? RawValue { get; set; }

    public string IssueCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
