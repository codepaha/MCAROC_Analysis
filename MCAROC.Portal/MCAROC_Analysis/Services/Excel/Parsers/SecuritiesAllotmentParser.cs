using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Securities Allotment" sheet — the company's capital-raise history.
/// Header row 0: ALLOTMENT DATE | ALLOTMENT TYPE | INSTRUMENT | AMOUNT (Rs. Crore) |
/// NO. OF SECURITIES ALLOTTED | NOMINAL VALUE | PREMIUM VALUE.</summary>
public static class SecuritiesAllotmentParser
{
    public static ParseResult<SecurityAllotment> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<SecurityAllotment>();

        var headerRow = -1;
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 4); r++)
        {
            var c0 = sheet.Rows[r].Count > 0 ? sheet.Rows[r][0]?.ToString()?.Trim().ToUpperInvariant() : null;
            if (c0 is "ALLOTMENT DATE") { headerRow = r; break; }
        }
        if (headerRow < 0)
        {
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, nameof(SecuritiesAllotmentParser), null, null,
                "ALLOTMENT_HEADER_NOT_FOUND", "Could not locate the 'ALLOTMENT DATE' header row."));
            return result;
        }

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var hasDate = DateNormalizer.TryParse(row.Count > 0 ? row[0] : null, out var date);
            var type = Cell(row, 1);
            var instrument = Cell(row, 2);
            if (date is null && type is null && instrument is null) continue;

            var a = new SecurityAllotment
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                AllotmentDate = hasDate ? date : null,
                AllotmentType = type,
                InstrumentType = instrument
            };
            if (AmountNormalizer.TryParse(Raw(row, 3), out var amt, out _)) a.AmountCrore = amt;
            if (AmountNormalizer.TryParse(Raw(row, 4), out var cnt, out _) && cnt is { } cv) a.NumberOfSecurities = (long)cv;
            if (AmountNormalizer.TryParse(Raw(row, 5), out var nom, out _)) a.NominalValuePerShare = nom;
            if (AmountNormalizer.TryParse(Raw(row, 6), out var prem, out _)) a.PremiumValuePerShare = prem;

            result.Items.Add(a);
        }

        return result;
    }

    private static object? Raw(IReadOnlyList<object?> row, int i) => i < row.Count ? row[i] : null;

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
