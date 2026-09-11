using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Legal Cases - Financial Dispute" sheet — flat table, header row 0 (no banner),
/// 10 cols: AMOUNT PAYABLE/RECEIVABLE · TYPE OF FINANCIAL DISPUTE · CURRENCY · AMOUNT UNDER DEFAULT ·
/// VERDICT · COURT · LITIGANT(S) · CASE NO. · DATE OF DEFAULT · DATE OF JUDGEMENT. The first column is a
/// direction flag ("Payable"/"Receivable" text), not an amount — the number lives in AMOUNT UNDER
/// DEFAULT. Absent from the COASTAL fixture (12/41 of the wider portfolio set carry it) — column layout
/// from the A10/#52 issue, not verified against a real workbook.
///
/// The data loop stops (does not merely skip) at the first table-boundary signal — both identity
/// columns (Litigants, Case No.) blank, a repeated header, or a "Total"/footer line — so a footer, a
/// repeated header, or an unrelated table further down can never be silently ingested as a dispute case
/// (same hardening Codex's review required on the sibling A8/A9 parsers).</summary>
public static class FinancialDisputeParser
{
    private const string ParserName = nameof(FinancialDisputeParser);

    public static ParseResult<FinancialDisputeCase> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<FinancialDisputeCase>();

        var headerRow = FindHeaderRow(sheet);
        if (headerRow < 0)
        {
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "FINANCIAL_DISPUTE_HEADER_NOT_FOUND", "Could not locate the full 'Legal Cases - Financial Dispute' header row."));
            return result;
        }

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            if (IsHeaderRow(row)) break; // a repeated header ends this table, not a data row

            var litigants = Cell(row, 6);
            var caseNumber = Cell(row, 7);
            if (IsFooterRow(litigants) || IsFooterRow(caseNumber)) break; // "Total" / "Grand Total" etc.
            if (string.IsNullOrEmpty(litigants) && string.IsNullOrEmpty(caseNumber)) break; // neither identity column set ends the table

            var fd = new FinancialDisputeCase
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                Direction = Cell(row, 0),
                DisputeType = Cell(row, 1),
                Currency = Cell(row, 2),
                Verdict = Cell(row, 4),
                Court = Cell(row, 5),
                Litigants = litigants,
                CaseNumber = caseNumber
            };
            if (AmountNormalizer.TryParse(row.Count > 3 ? row[3] : null, out var amt, out _)) fd.AmountUnderDefault = amt;
            if (DateNormalizer.TryParse(row.Count > 8 ? row[8] : null, out var dod)) fd.DateOfDefault = dod;
            if (DateNormalizer.TryParse(row.Count > 9 ? row[9] : null, out var doj)) fd.DateOfJudgement = doj;

            result.Items.Add(fd);
        }

        return result;
    }

    /// <summary>Validates the full expected header shape (7 of the 10 columns — the two date columns'
    /// exact wording is the least load-bearing to pin), not a single "starts with" check on column 0.</summary>
    private static bool IsHeaderRow(IReadOnlyList<object?> row) =>
        (Cell(row, 0)?.StartsWith("AMOUNT PAYABLE", StringComparison.OrdinalIgnoreCase) ?? false) &&
        CellEquals(row, 1, "TYPE OF FINANCIAL DISPUTE") &&
        CellEquals(row, 2, "CURRENCY") &&
        CellEquals(row, 3, "AMOUNT UNDER DEFAULT") &&
        CellEquals(row, 4, "VERDICT") &&
        CellEquals(row, 5, "COURT") &&
        (Cell(row, 6)?.StartsWith("LITIGANT", StringComparison.OrdinalIgnoreCase) ?? false) &&
        (Cell(row, 7)?.StartsWith("CASE NO", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsFooterRow(string? cell) =>
        cell is not null && (cell.Equals("Total", StringComparison.OrdinalIgnoreCase)
            || cell.StartsWith("Grand Total", StringComparison.OrdinalIgnoreCase));

    private static int FindHeaderRow(SheetData sheet)
    {
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 4); r++)
            if (IsHeaderRow(sheet.Rows[r]))
                return r;
        return -1;
    }

    private static bool CellEquals(IReadOnlyList<object?> row, int i, string expected) =>
        string.Equals(Cell(row, i), expected, StringComparison.OrdinalIgnoreCase);

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
