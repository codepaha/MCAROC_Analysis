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

    /// <summary>The source workbook's own export/snapshot timestamp parsed from 'About the Company' (e.g. "Printed at").
    /// Immutable anchor for time-sensitive metrics; null if the source workbook omitted the header or it could not be parsed.</summary>
    public DateTime? SourceSnapshotDate { get; set; }

    public IngestionRunStatus Status { get; set; } = IngestionRunStatus.Running;

    /// <summary>Bumped manually when parser logic changes materially.</summary>
    public string ParserVersion { get; set; } = "1.0";

    public long? SourceRocDocumentId { get; set; }
    public long? SourceChargeDocumentId { get; set; }

    public int RowsExtracted { get; set; }
    public int WarningsCount { get; set; }
    public int ErrorsCount { get; set; }

    /// <summary>JSON array of the canonical names of every optional workbook sheet that was NOT present
    /// in this upload (<see cref="MCAROC_Analysis.Services.Excel.SheetAliases.TrackedOptionalSheets"/>).
    /// Lets the portal and dossier say "this workbook did not include a &lt;Sheet&gt;" rather than
    /// showing the same "no records" empty state whether a section was absent or verified empty.
    /// <c>"[]"</c> = every tracked optional sheet was present.</summary>
    public string AbsentOptionalSheetsJson { get; set; } = "[]";

    /// <summary>True when the ROC report lists one or more charges but the Detailed Charge Report
    /// workbook was not supplied (or was quarantined for an identity mismatch) — the charge annexure
    /// is then built from ROC-sequence data only. A specific, common gap worth its own flag.</summary>
    public bool ChargeReportMissing { get; set; }

    public string? FailureReason { get; set; }

    public List<IngestionIssue> Issues { get; set; } = [];
}
