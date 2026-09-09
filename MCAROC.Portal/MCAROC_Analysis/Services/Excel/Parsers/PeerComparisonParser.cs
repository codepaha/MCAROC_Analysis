using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Peer Comparison" sheet — a metrics grid with (Actual Value, Median) column pairs
/// per financial year. Emits one PeerComparisonMetric per (metric, year). Position is a purely
/// mathematical comparison of the company value to the peer median (see PeerComparisonDisplayRules for
/// per-metric desirability). The "5 Closest Peers" mini-table below the metrics is not extracted this phase.</summary>
public static class PeerComparisonParser
{
    private const string ParserName = nameof(PeerComparisonParser);

    public static ParseResult<PeerComparisonMetric> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<PeerComparisonMetric>();

        string? industry = null, segment = null;
        var metricsRow = -1;
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var c0 = Cell(sheet.Rows[r], 0);
            if (c0 is null) continue;
            if (string.Equals(c0, "Industry", StringComparison.OrdinalIgnoreCase)) industry = Cell(sheet.Rows[r], 1);
            else if (string.Equals(c0, "Segment", StringComparison.OrdinalIgnoreCase)) segment = Cell(sheet.Rows[r], 1);
            else if (string.Equals(c0, "Metrics", StringComparison.OrdinalIgnoreCase)) { metricsRow = r; break; }
        }
        if (metricsRow < 0)
        {
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "PEER_METRICS_HEADER_NOT_FOUND", "Could not locate the 'Metrics' header row."));
            return result;
        }

        // The "Metrics" row carries "FY 2014" labels at the start of each (Actual, Median) column pair.
        var yearHeader = sheet.Rows[metricsRow];
        var yearByCol = new Dictionary<int, int>();       // column of the "Actual" cell -> year
        for (var c = 1; c < yearHeader.Count; c++)
        {
            var y = ExtractYear(yearHeader[c]);
            if (y is not null) yearByCol[c] = y.Value;
        }
        if (yearByCol.Count == 0) return result;

        var peerCountByYear = new Dictionary<int, int>();

        for (var r = metricsRow + 2; r < sheet.Rows.Count; r++)   // +2: skip the Actual/Median sub-header
        {
            var row = sheet.Rows[r];
            var name = Cell(row, 0);
            if (name is null) continue;
            if (name.StartsWith("5 Closest Peers", StringComparison.OrdinalIgnoreCase)) break;

            if (name.StartsWith("# of Peers", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (col, year) in yearByCol)
                    if (AmountNormalizer.TryParse(At(row, col), out var pc, out _) && pc is { } pcv)
                        peerCountByYear[year] = (int)pcv;
                continue;
            }

            foreach (var (col, year) in yearByCol)
            {
                var companyRaw = At(row, col);
                var medianRaw = At(row, col + 1);
                var hasCompany = AmountNormalizer.TryParse(companyRaw, out var company, out _);
                var hasMedian = AmountNormalizer.TryParse(medianRaw, out var median, out _);
                if ((!hasCompany || company is null) && (!hasMedian || median is null)) continue;

                var m = new PeerComparisonMetric
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = sheet.Name,
                    SourceRowNumber = r + 1,
                    MetricName = name,
                    FinancialYear = year,
                    CompanyValue = company,
                    PeerMedianValue = median,
                    PeerCount = peerCountByYear.TryGetValue(year, out var pc) ? pc : null,
                    Industry = industry,
                    Segment = segment,
                    Position = PeerComparisonDisplayRules.Compare(company, median)
                };
                result.Items.Add(m);
            }
        }

        return result;
    }

    private static int? ExtractYear(object? cell)
    {
        var text = cell?.ToString() ?? "";
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 && int.TryParse(digits[..4], out var y) && y is >= 1990 and <= 2100 ? y : null;
    }

    private static object? At(IReadOnlyList<object?> row, int i) => i < row.Count ? row[i] : null;

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
