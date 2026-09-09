using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Merges "Director Shareholding" (title row embeds the year, e.g. "DIRECTORS SHAREHOLDING - 31
/// Mar, 2025", possibly repeated for multiple years) and "Shareholding More Than 5%" (year is a per-row
/// column). Rows are grouped by (NameNormalized, FinancialYear) so a person appearing in both sheets for
/// the same year becomes one record, combining SharesHeld with the richer >5% fields.</summary>
public static class ShareholdingParser
{
    private const string ParserName = nameof(ShareholdingParser);
    private static readonly Regex YearPattern = new(@"(\d{4})", RegexOptions.Compiled);

    public static ParseResult<Shareholding> Parse(
        SheetData? directorSheet, SheetData? majorSheet,
        long requestId, long ingestionRunId,
        long? directorSourceDocumentId, long? majorSourceDocumentId)
    {
        var result = new ParseResult<Shareholding>();
        var byKey = new Dictionary<(string Name, int Year), Shareholding>();

        if (directorSheet is not null)
            ParseDirectorShareholding(directorSheet, requestId, ingestionRunId, directorSourceDocumentId, byKey, result);

        if (majorSheet is not null)
            ParseMajorShareholding(majorSheet, requestId, ingestionRunId, majorSourceDocumentId, byKey, result);

        result.Items.AddRange(byKey.Values);
        return result;
    }

    private static void ParseDirectorShareholding(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        Dictionary<(string, int), Shareholding> byKey, ParseResult<Shareholding> result)
    {
        int? currentYear = null;
        var inDataBlock = false;

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var col0 = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (col0 is null) continue;

            if (col0.StartsWith("DIRECTORS SHAREHOLDING", StringComparison.OrdinalIgnoreCase))
            {
                var match = YearPattern.Match(col0);
                currentYear = match.Success ? int.Parse(match.Value) : null;
                inDataBlock = false;
                continue;
            }

            if (col0 == "Name")
            {
                inDataBlock = true;
                continue;
            }

            if (!inDataBlock || currentYear is null || col0.Length == 0) continue;

            var nameRaw = col0;
            var nameNorm = NameNormalizer.Normalize(nameRaw);
            var key = (nameNorm, currentYear.Value);

            if (!byKey.TryGetValue(key, out var sh))
            {
                sh = new Shareholding
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = sheet.Name,
                    SourceRowNumber = r + 1,
                    FinancialYear = currentYear.Value,
                    ShareholderNameRaw = nameRaw,
                    ShareholderNameNormalized = nameNorm,
                    ShareholderType = "Director",
                    IsPromoter = true,
                    SourceType = ShareholdingSourceType.DirectorShareholding
                };
                byKey[key] = sh;
            }

            var pct = row.Count > 2 ? row[2] : null;
            if (AmountNormalizer.TryParse(pct, out var pctValue, out _)) sh.HoldingPercentage ??= pctValue;

            var shares = row.Count > 3 ? row[3] : null;
            if (AmountNormalizer.TryParse(shares, out var sharesValue, out _) && sharesValue is not null)
                sh.SharesHeld ??= (long)sharesValue.Value;
        }
    }

    private static void ParseMajorShareholding(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        Dictionary<(string, int), Shareholding> byKey, ParseResult<Shareholding> result)
    {
        for (var r = 2; r < sheet.Rows.Count; r++) // row 0 = title, row 1 = header
        {
            var row = sheet.Rows[r];
            var yearText = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            var entityName = row.Count > 1 ? row[1]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(yearText) || string.IsNullOrEmpty(entityName)) continue;

            var yearMatch = YearPattern.Match(yearText);
            if (!yearMatch.Success)
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, "FinancialYear", yearText,
                    "BAD_YEAR", $"Could not parse financial year from '{yearText}'", r + 1));
                continue;
            }

            var year = int.Parse(yearMatch.Value);
            var nameNorm = NameNormalizer.Normalize(entityName);
            var key = (nameNorm, year);

            if (!byKey.TryGetValue(key, out var sh))
            {
                sh = new Shareholding
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = sheet.Name,
                    SourceRowNumber = r + 1,
                    FinancialYear = year,
                    ShareholderNameRaw = entityName,
                    ShareholderNameNormalized = nameNorm,
                    SourceType = ShareholdingSourceType.MajorShareholding
                };
                byKey[key] = sh;
            }
            else
            {
                // Present in both sheets — keep the richer entity-type context from this sheet.
                sh.SourceType = ShareholdingSourceType.MajorShareholding;
            }

            var relationship = row.Count > 2 ? row[2]?.ToString()?.Trim() : null;
            sh.ShareholderType = row.Count > 3 ? row[3]?.ToString()?.Trim() : relationship;
            if (relationship?.Equals("PROMOTER", StringComparison.OrdinalIgnoreCase) == true) sh.IsPromoter = true;

            var pct = row.Count > 4 ? row[4] : null;
            if (AmountNormalizer.TryParse(pct, out var pctValue, out _) && pctValue is not null)
                sh.HoldingPercentage = pctValue; // this sheet is authoritative for %, overwrite if present
        }
    }
}
