using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the two blocks of the "Highlights" sheet that sit below FINANCIAL PARAMETERS
/// (which <see cref="FinancialParametersParser"/> handles): PRINCIPAL BUSINESS ACTIVITIES and
/// NAME HISTORY. Both are small banner-led tables; a "this corporate has not had any name change"
/// note stands in for an empty NAME HISTORY.</summary>
public static class HighlightsParser
{
    private const string ParserName = nameof(HighlightsParser);

    public static ParseResult<PrincipalBusinessActivity> ParsePrincipalBusinessActivities(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<PrincipalBusinessActivity>();
        var inBlock = false;
        DateOnly? asOn = null;
        var order = 0;

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var c0 = Cell(sheet, r, 0);
            if (string.IsNullOrEmpty(c0)) { continue; }

            if (c0.StartsWith("PRINCIPAL BUSINESS ACTIVITIES", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                asOn = TrailingDate(c0);
                order = 0;
                continue;
            }
            if (IsBanner(c0)) { inBlock = false; continue; }
            if (!inBlock) continue;

            if (c0.StartsWith("Main Activity Group Code", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (c0.StartsWith("See Annexure", StringComparison.OrdinalIgnoreCase)) continue;

            var groupDesc = Cell(sheet, r, 1);
            var activityCode = Cell(sheet, r, 2);
            var activityDesc = Cell(sheet, r, 3);
            var turnover = sheet.Rows[r].Count > 4 ? sheet.Rows[r][4] : null;

            result.Items.Add(new PrincipalBusinessActivity
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                AsOnDate = asOn,
                MainActivityGroupCode = CleanCode(c0),
                MainActivityGroupDescription = Nullify(groupDesc),
                BusinessActivityCode = CleanCode(activityCode),
                BusinessActivityDescription = Nullify(activityDesc),
                TurnoverPercent = AmountNormalizer.TryParse(turnover, out var pct, out _) ? pct : null,
                DisplayOrder = ++order
            });
        }

        return result;
    }

    public static ParseResult<CompanyNameHistory> ParseNameHistory(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<CompanyNameHistory>();
        var inBlock = false;
        var order = 0;

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var c0 = Cell(sheet, r, 0);
            if (string.IsNullOrEmpty(c0)) { continue; }

            if (c0.Equals("NAME HISTORY", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                order = 0;
                continue;
            }
            if (IsBanner(c0)) { inBlock = false; continue; }
            if (!inBlock) continue;

            if (c0.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue; // header
            if (c0.Contains("has not had any name change", StringComparison.OrdinalIgnoreCase)) continue; // empty-state note

            var tillRaw = Cell(sheet, r, 1);
            DateOnly? till = null;
            string? tillRawKept = null;
            if (!string.IsNullOrEmpty(tillRaw))
            {
                if (DateNormalizer.TryParse(tillRaw, out var d) && d is not null) till = d;
                else { tillRawKept = tillRaw; result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName,
                    nameof(CompanyNameHistory.TillDate), tillRaw, "BAD_DATE", $"Could not parse name-history till-date '{tillRaw}'", r + 1)); }
            }

            result.Items.Add(new CompanyNameHistory
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                PreviousName = c0,
                TillDate = till,
                TillDateRaw = tillRawKept,
                DisplayOrder = ++order
            });
        }

        return result;
    }

    private static string? Cell(SheetData sheet, int r, int c) =>
        sheet.Rows[r].Count > c ? sheet.Rows[r][c]?.ToString()?.Trim() : null;

    private static bool IsBanner(string c0) =>
        c0 is "FINANCIAL PARAMETERS" or "NAME HISTORY"
        || c0.StartsWith("PRINCIPAL BUSINESS ACTIVITIES", StringComparison.OrdinalIgnoreCase);

    private static string? Nullify(string? s) =>
        string.IsNullOrWhiteSpace(s) || s is "-" or "NA" ? null : s;

    /// <summary>MCA activity codes arrive as "F2" or as a float like "11.0" — drop the ".0".</summary>
    private static string? CleanCode(string? s)
    {
        var t = Nullify(s);
        if (t is null) return null;
        return t.EndsWith(".0", StringComparison.Ordinal) ? t[..^2] : t;
    }

    private static DateOnly? TrailingDate(string header)
    {
        var dash = header.LastIndexOf('-');
        if (dash < 0 || dash == header.Length - 1) return null;
        return DateNormalizer.TryParse(header[(dash + 1)..].Trim(), out var d) ? d : null;
    }
}
