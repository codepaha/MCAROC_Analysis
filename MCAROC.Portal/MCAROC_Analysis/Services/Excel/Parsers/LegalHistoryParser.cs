using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Legal History", which stacks THREE sub-tables, each with its own column layout
/// (confirmed by inspecting the full sheet for CIN U45203OR1995PLC003982 — 960 rows):
///   1. rows 2.. = confirmed litigation, ends at the first blank row:
///      Case Type | Case Status | Case Category | Court | Litigant(s) | Case No. | Date  →  Confirmed.
///   2. a "PROBABLE CASES" section with its OWN header — the vendor could not confirm the case belongs
///      to this company (name resemblance / spelling variants / multiple similarly-named entities):
///      Case Status | Case Category | Court | Petitioner(s) | Respondent(s) | Case No. | Date  →  Probable.
///   3. an "UNVERIFIED COURT RECORDS" section — the vendor could not check the court website at
///      processing time, so even the basic fields are provisional. DIFFERENT, shorter layout:
///      Court | Petitioner(s) | Respondent(s) | Case No. | Date of Last Activity  →  Uncertain.
/// The Confirmed layout must NOT be applied to sections 2 and 3 — doing so drops court/party text into
/// Case Status / Case Category and loses the real case number (regression fixed 2026-09-10).</summary>
public static class LegalHistoryParser
{
    private const string ProbableTitle = "PROBABLE CASES";
    private const string UnverifiedTitle = "UNVERIFIED COURT RECORDS";

    public static ParseResult<Litigation> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<Litigation>();

        Litigation New(int rowIndex, LitigationMatchStatus match) => new()
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = rowIndex + 1,
            MatchStatus = match
        };

        // ── 1. Confirmed litigation — rows 2.. until the first blank row ──
        var r = 2;
        for (; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var caseType = Cell(row, 0);
            if (string.IsNullOrEmpty(caseType)) break;
            if (IsSectionTitle(row)) break; // defensive: a title with no blank row before it

            DateNormalizer.TryParse(row.Count > 6 ? row[6] : null, out var hearingDate);
            var item = New(r, LitigationMatchStatus.Confirmed);
            item.CaseType = caseType;
            item.CaseStatus = Cell(row, 1);
            item.CaseCategory = Cell(row, 2);
            item.Court = Cell(row, 3);
            item.Litigants = Cell(row, 4);
            item.CaseNumber = Cell(row, 5);
            item.LastHearingDate = hearingDate;
            result.Items.Add(item);
        }

        // ── 2. PROBABLE CASES — Case Status | Case Category | Court | Petitioner | Respondent | Case No. | Date ──
        var probableTitleRow = FindTitle(sheet, ProbableTitle, from: r);
        if (probableTitleRow >= 0)
        {
            for (r = probableTitleRow + 2; r < sheet.Rows.Count; r++) // +2 skips the title and this section's own header
            {
                var row = sheet.Rows[r];
                if (Cell(row, 0) == UnverifiedTitle) break;
                var caseStatus = Cell(row, 0);
                if (string.IsNullOrEmpty(caseStatus) || caseStatus == "Case Status") continue;

                DateNormalizer.TryParse(row.Count > 6 ? row[6] : null, out var hearingDate);
                var item = New(r, LitigationMatchStatus.Probable);
                item.CaseStatus = caseStatus;
                item.CaseCategory = Cell(row, 1);
                item.Court = Cell(row, 2);
                item.Litigants = JoinParties(Cell(row, 3), Cell(row, 4));
                item.CaseNumber = Cell(row, 5);
                item.LastHearingDate = hearingDate;
                result.Items.Add(item);
            }
        }

        // ── 3. UNVERIFIED COURT RECORDS — Court | Petitioner | Respondent | Case No. | Date of Last Activity ──
        var unverifiedTitleRow = FindTitle(sheet, UnverifiedTitle, from: 0);
        if (unverifiedTitleRow >= 0)
        {
            for (r = unverifiedTitleRow + 2; r < sheet.Rows.Count; r++) // +2 skips the title and this section's own header
            {
                var row = sheet.Rows[r];
                var court = Cell(row, 0);
                if (string.IsNullOrEmpty(court) || court == "Court") continue;

                DateNormalizer.TryParse(row.Count > 4 ? row[4] : null, out var lastActivity);
                var item = New(r, LitigationMatchStatus.Uncertain);
                item.Court = court;
                item.Litigants = JoinParties(Cell(row, 1), Cell(row, 2));
                item.CaseNumber = Cell(row, 3);
                item.LastHearingDate = lastActivity;
                result.Items.Add(item);
            }
        }

        return result;
    }

    private static int FindTitle(SheetData sheet, string title, int from)
    {
        for (var i = Math.Max(from, 0); i < sheet.Rows.Count; i++)
            if (Cell(sheet.Rows[i], 0) == title)
                return i;
        return -1;
    }

    /// <summary>A row that carries a value in column 0 and nothing else is a section title, not data.</summary>
    private static bool IsSectionTitle(IReadOnlyList<object?> row)
    {
        if (string.IsNullOrEmpty(Cell(row, 0))) return false;
        for (var i = 1; i < row.Count; i++)
            if (!string.IsNullOrEmpty(Cell(row, i))) return false;
        return true;
    }

    private static string? JoinParties(string? petitioner, string? respondent)
    {
        var joined = string.Join(" vs. ", new[] { petitioner, respondent }.Where(s => !string.IsNullOrEmpty(s)));
        return joined.Length == 0 ? null : joined;
    }

    private static string? Cell(IReadOnlyList<object?> row, int index)
    {
        var text = index < row.Count ? row[index]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
