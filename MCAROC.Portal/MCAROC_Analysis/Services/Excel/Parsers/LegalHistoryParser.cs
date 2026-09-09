using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Legal History", which stacks two sub-tables (confirmed by inspecting the full sheet):
///   1. row 0 = title, row 1 = header, rows 2.. = confirmed litigation: Case Type, Case Status, Case
///      Category, Court, Litigant(s), Case No., Date of Last Hearing — ends at the first blank row.
///   2. A "PROBABLE CASES" section further down with its OWN header (Case Status, Case Category, Court,
///      Petitioner(s), Respondent(s), Case No., Date — no Case Type column): cases the data vendor could
///      not confirm belong to this company with certainty. This is exactly what Litigation.MatchStatus
///      exists for — Confirmed for table 1, Probable for table 2 — rather than a bare bool.</summary>
public static class LegalHistoryParser
{
    public static ParseResult<Litigation> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<Litigation>();

        var r = 2;
        for (; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var caseType = Cell(row, 0);
            if (string.IsNullOrEmpty(caseType)) break; // blank row ends table 1

            DateNormalizer.TryParse(row.Count > 6 ? row[6] : null, out var hearingDate);

            result.Items.Add(new Litigation
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                CaseType = caseType,
                CaseStatus = Cell(row, 1),
                CaseCategory = Cell(row, 2),
                Court = Cell(row, 3),
                Litigants = Cell(row, 4),
                CaseNumber = Cell(row, 5),
                LastHearingDate = hearingDate,
                MatchStatus = LitigationMatchStatus.Confirmed
            });
        }

        var probableHeaderRow = -1;
        for (; r < sheet.Rows.Count; r++)
        {
            if (Cell(sheet.Rows[r], 0) == "PROBABLE CASES") { probableHeaderRow = r; break; }
        }
        if (probableHeaderRow < 0) return result;

        for (r = probableHeaderRow + 2; r < sheet.Rows.Count; r++) // +2 skips the section title and its own header row
        {
            var row = sheet.Rows[r];
            var caseStatus = Cell(row, 0);
            if (string.IsNullOrEmpty(caseStatus)) continue;

            DateNormalizer.TryParse(row.Count > 6 ? row[6] : null, out var hearingDate);
            var petitioner = Cell(row, 3);
            var respondent = Cell(row, 4);
            var litigants = string.Join(" vs. ", new[] { petitioner, respondent }.Where(s => !string.IsNullOrEmpty(s)));

            result.Items.Add(new Litigation
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                CaseStatus = caseStatus,
                CaseCategory = Cell(row, 1),
                Court = Cell(row, 2),
                Litigants = litigants.Length == 0 ? null : litigants,
                CaseNumber = Cell(row, 5),
                LastHearingDate = hearingDate,
                MatchStatus = LitigationMatchStatus.Probable
            });
        }

        return result;
    }

    private static string? Cell(IReadOnlyList<object?> row, int index)
    {
        var text = index < row.Count ? row[index]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
