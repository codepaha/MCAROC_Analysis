using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Related Party Transactions" sheet — one row per (party, transaction type,
/// financial year). Banner row 0, header row 1: Financial Year Ending On / Entity Type / Entity Name /
/// Relationship / Transaction Type / Amount (Rs. Crore). Absent from the COASTAL fixture (25/41 of the
/// wider portfolio set carry it) — column layout from the A8/#50 issue, not verified against a real
/// workbook; header row is located dynamically rather than assumed at a fixed index so a banner-less
/// export still parses.
///
/// The data loop stops at a real table-boundary signal — a blank row or a "Total"/footer line — so a
/// footer or an unrelated table further down the sheet can never be silently ingested as transactions.
/// A repeated full header is a normal page-break/continuation artifact, not a boundary: it is skipped,
/// not treated as end-of-table, so rows after it are not lost (Codex review, PR #90).</summary>
public static class RelatedPartyTransactionsParser
{
    private const string ParserName = nameof(RelatedPartyTransactionsParser);

    public static ParseResult<RelatedPartyTransaction> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<RelatedPartyTransaction>();

        var headerRow = FindHeaderRow(sheet);
        if (headerRow < 0)
        {
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "RPT_HEADER_NOT_FOUND", "Could not locate the full 'Related Party Transactions' header row."));
            return result;
        }

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            if (IsHeaderRow(row)) continue; // a repeated header is a page-break artifact — skip it, keep parsing
            if (IsFooterRow(row)) break; // "Total" / "Grand Total" etc.

            var name = Cell(row, 2);
            if (string.IsNullOrEmpty(name)) break; // a blank row ends the table — never scan past it

            var rpt = new RelatedPartyTransaction
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                EntityType = Cell(row, 1),
                EntityNameRaw = name,
                EntityNameNormalized = NameNormalizer.Normalize(name),
                RelationshipRaw = Cell(row, 3),
                TransactionType = Cell(row, 4)
            };
            if (DateNormalizer.TryParse(row.Count > 0 ? row[0] : null, out var fye)) rpt.FinancialYearEnding = fye;
            if (AmountNormalizer.TryParse(row.Count > 5 ? row[5] : null, out var amt, out _)) rpt.AmountCrore = amt;

            result.Items.Add(rpt);
        }

        return result;
    }

    /// <summary>Validates the FULL expected header shape (all 5 text columns), not just "Entity Name"
    /// in column 2 — a lone-column check can't tell a real header from an unrelated row that happens to
    /// carry "Entity Name" in the same position.</summary>
    private static bool IsHeaderRow(IReadOnlyList<object?> row) =>
        CellEquals(row, 0, "Financial Year Ending On") &&
        CellEquals(row, 1, "Entity Type") &&
        CellEquals(row, 2, "Entity Name") &&
        CellEquals(row, 3, "Relationship") &&
        CellEquals(row, 4, "Transaction Type");

    private static bool IsFooterRow(IReadOnlyList<object?> row)
    {
        var c2 = Cell(row, 2);
        return c2 is not null && (c2.Equals("Total", StringComparison.OrdinalIgnoreCase)
            || c2.StartsWith("Grand Total", StringComparison.OrdinalIgnoreCase));
    }

    private static int FindHeaderRow(SheetData sheet)
    {
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 6); r++)
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
