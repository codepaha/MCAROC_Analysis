using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Legal Cases - Financial Dispute" sheet — flat table, header row 0 (no banner),
/// 10 cols: AMOUNT PAYABLE/RECEIVABLE · TYPE OF FINANCIAL DISPUTE · CURRENCY · AMOUNT UNDER DEFAULT ·
/// VERDICT · COURT · LITIGANT(S) · CASE NO. · DATE OF DEFAULT · DATE OF JUDGEMENT. The first column is a
/// direction flag ("Payable"/"Receivable" text), not an amount — the number lives in AMOUNT UNDER
/// DEFAULT. Absent from the COASTAL fixture (12/41 of the wider portfolio set carry it) — column layout
/// from the A10/#52 issue, not verified against a real workbook.</summary>
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
                "FINANCIAL_DISPUTE_HEADER_NOT_FOUND", "Could not locate the 'Amount Payable/Receivable' header row."));
            return result;
        }

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var direction = Cell(row, 0);
            var litigants = Cell(row, 6);
            if (string.IsNullOrEmpty(direction) && string.IsNullOrEmpty(litigants)) continue;

            var fd = new FinancialDisputeCase
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                Direction = direction,
                DisputeType = Cell(row, 1),
                Currency = Cell(row, 2),
                Verdict = Cell(row, 4),
                Court = Cell(row, 5),
                Litigants = litigants,
                CaseNumber = Cell(row, 7)
            };
            if (AmountNormalizer.TryParse(row.Count > 3 ? row[3] : null, out var amt, out _)) fd.AmountUnderDefault = amt;
            if (DateNormalizer.TryParse(row.Count > 8 ? row[8] : null, out var dod)) fd.DateOfDefault = dod;
            if (DateNormalizer.TryParse(row.Count > 9 ? row[9] : null, out var doj)) fd.DateOfJudgement = doj;

            result.Items.Add(fd);
        }

        return result;
    }

    private static int FindHeaderRow(SheetData sheet)
    {
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 4); r++)
        {
            var c0 = sheet.Rows[r].Count > 0 ? sheet.Rows[r][0]?.ToString()?.Trim() : null;
            if (c0 is not null && c0.StartsWith("AMOUNT PAYABLE", StringComparison.OrdinalIgnoreCase))
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
