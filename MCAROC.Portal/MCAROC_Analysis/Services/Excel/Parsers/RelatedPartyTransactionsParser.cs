using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Related Party Transactions" sheet — one row per (party, transaction type,
/// financial year). Banner row 0, header row 1: Financial Year Ending On / Entity Type / Entity Name /
/// Relationship / Transaction Type / Amount (Rs. Crore). Absent from the COASTAL fixture (25/41 of the
/// wider portfolio set carry it) — column layout from the A8/#50 issue, not verified against a real
/// workbook; header row is located dynamically rather than assumed at a fixed index so a banner-less
/// export still parses.</summary>
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
                "RPT_HEADER_NOT_FOUND", "Could not locate the 'Entity Name' header row."));
            return result;
        }

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var name = Cell(row, 2);
            if (string.IsNullOrEmpty(name)) continue;

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

    private static int FindHeaderRow(SheetData sheet)
    {
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 6); r++)
        {
            var c2 = sheet.Rows[r].Count > 2 ? sheet.Rows[r][2]?.ToString()?.Trim() : null;
            if (string.Equals(c2, "Entity Name", StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return -1;
    }

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
