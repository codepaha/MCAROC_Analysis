using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Highlights" (latest year only) and "Annexure - Financial Parameters" (multi-year)
/// into a flat FinancialParameter key/value list. Both sheets share the layout
/// "Parameter (Rs. Crore) | &lt;year&gt; | &lt;year&gt; | ...". Heterogeneous cells are preserved: a clean number
/// goes to NumericValue, anything else ("No", "Not Applicable", "-") stays in RawValue / TextValue and
/// NumericValue is left null. Rows are keyed by (ParameterName, FinancialYear) so the Annexure and
/// Highlights don't produce duplicates for a shared year.</summary>
public static class FinancialParametersParser
{
    private const string ParserName = nameof(FinancialParametersParser);

    public static ParseResult<FinancialParameter> Parse(
        SheetData? highlightsSheet, SheetData? annexureSheet,
        long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<FinancialParameter>();
        var seen = new Dictionary<(string Name, int? Year), (string Raw, string Sheet)>();

        foreach (var sheet in new[] { annexureSheet, highlightsSheet })
        {
            if (sheet is null) continue;
            ParseSheet(sheet, requestId, ingestionRunId, sourceDocumentId, seen, result);
        }

        return result;
    }

    private static void ParseSheet(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        Dictionary<(string Name, int? Year), (string Raw, string Sheet)> seen, ParseResult<FinancialParameter> result)
    {
        var headerRow = -1;
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 4); r++)
        {
            var c0 = sheet.Rows[r].Count > 0 ? sheet.Rows[r][0]?.ToString()?.Trim() : null;
            if (c0 is not null && c0.StartsWith("Parameter", StringComparison.OrdinalIgnoreCase)) { headerRow = r; break; }
        }
        if (headerRow < 0) return;

        var header = sheet.Rows[headerRow];
        var unit = ExtractUnit(header.Count > 0 ? header[0]?.ToString() : null);
        var yearByCol = new Dictionary<int, int>();
        for (var c = 1; c < header.Count; c++)
        {
            var y = ExtractYear(header[c]);
            if (y is not null) yearByCol[c] = y.Value;
        }

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var name = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(name)) continue;

            var cols = yearByCol.Count > 0 ? yearByCol.Keys.ToList() : Enumerable.Range(1, Math.Max(0, row.Count - 1)).ToList();
            foreach (var c in cols)
            {
                if (c >= row.Count) continue;
                var cell = row[c];
                var raw = cell?.ToString()?.Trim() ?? "";
                if (raw.Length == 0) continue;   // skip blank cells, but keep "-"

                int? year = yearByCol.TryGetValue(c, out var y) ? y : null;
                var key = (name, year);
                if (seen.TryGetValue(key, out var prev) && !string.Equals(prev.Raw, raw, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, name, raw,
                        "DUPLICATE_PARAMETER_CONFLICT",
                        $"Parameter '{name}' FY{year} has conflicting values: '{prev.Raw}' ({prev.Sheet}) vs '{raw}' ({sheet.Name})",
                        r + 1));
                }
                seen[key] = (raw, sheet.Name);

                var fp = new FinancialParameter
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = sheet.Name,
                    SourceRowNumber = r + 1,
                    ParameterName = name,
                    FinancialYear = year,
                    RawValue = raw,
                    Unit = unit
                };

                if (AmountNormalizer.TryParse(cell, out var num, out _) && num is not null)
                    fp.NumericValue = num;
                else
                    fp.TextValue = raw;

                result.Items.Add(fp);
            }
        }
    }

    private static string? ExtractUnit(string? headerText)
    {
        if (string.IsNullOrEmpty(headerText)) return null;
        var open = headerText.IndexOf('(');
        var close = headerText.IndexOf(')');
        return open >= 0 && close > open ? headerText[(open + 1)..close].Trim() : null;
    }

    private static int? ExtractYear(object? cell)
    {
        switch (cell)
        {
            case null: return null;
            case DateTime dt: return dt.Year;
            case double d when d is >= 1990 and <= 2100: return (int)d;
        }
        var text = cell.ToString() ?? "";
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length >= 4 && int.TryParse(digits[^4..], out var y) && y is >= 1990 and <= 2100) return y;
        return null;
    }
}
