namespace MCAROC_Analysis.Data.Entities;

/// <summary>Layer 0 — one immutable record per non-blank data row of every worksheet of every uploaded
/// workbook, captured verbatim before any typing or deduplication. This is the system of record: the
/// typed entities (Director, RocCharge, FinancialYearData, …) are a deliberately lossy projection of
/// these rows, and the client dossier's "Full source" annexures render straight from here. Never
/// deduped, never dropped.</summary>
public class SourceRow
{
    public long SourceRowId { get; set; }

    public long RequestId { get; set; }
    public long IngestionRunId { get; set; }
    public long SourceDocumentId { get; set; }

    /// <summary>Which uploaded workbook this row came from — "RocReport" or "ChargeReport".</summary>
    public string WorkbookRole { get; set; } = string.Empty;

    public string SheetName { get; set; } = string.Empty;

    /// <summary>0-based position of the sheet within its workbook (two workbooks can carry a sheet of
    /// the same name).</summary>
    public int SheetIndex { get; set; }

    /// <summary>1-based row number within the sheet, matching what a person sees in Excel.</summary>
    public int RowNumber { get; set; }

    /// <summary>Ordered JSON array of the row's cell values as strings (an empty/null cell → JSON null).
    /// The column order is the sheet's own; no header interpretation is applied.</summary>
    public string CellsJson { get; set; } = "[]";

    /// <summary>SHA-256 (hex) of the row's normalized cell values — used only to collapse byte-identical
    /// duplicate rows when rendering the Full-source dossier, never to drop a row from this table.</summary>
    public string RowHash { get; set; } = string.Empty;

    public DateTime ExtractedAt { get; set; }
}
