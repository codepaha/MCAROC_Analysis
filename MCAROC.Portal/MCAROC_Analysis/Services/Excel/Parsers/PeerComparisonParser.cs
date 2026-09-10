using System.Globalization;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Peer Comparison" sheet: a metrics grid with (Actual Value, Median) column pairs
/// per financial year (→ one <see cref="PeerComparisonMetric"/> per (metric, year)), and the
/// "5 Closest Peers by Revenue" block below it (→ <see cref="PeerCompany"/> per named peer, including
/// the company itself). Position is a purely mathematical comparison of the company value to the peer
/// median (see PeerComparisonDisplayRules for per-metric desirability).</summary>
public static class PeerComparisonParser
{
    private const string ParserName = nameof(PeerComparisonParser);

    public static ParseResult<PeerComparisonMetric> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId) =>
        Parse(sheet, requestId, ingestionRunId, sourceDocumentId, out _);

    public static ParseResult<PeerComparisonMetric> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        out List<PeerCompany> peers)
    {
        var result = new ParseResult<PeerComparisonMetric>();
        peers = [];

        string? industry = null, segment = null;
        int? referenceYear = null;
        var metricsRow = -1;
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var c0 = Cell(sheet.Rows[r], 0);
            if (c0 is null) continue;
            if (string.Equals(c0, "Industry", StringComparison.OrdinalIgnoreCase)) industry = Cell(sheet.Rows[r], 1);
            else if (string.Equals(c0, "Segment", StringComparison.OrdinalIgnoreCase)) segment = Cell(sheet.Rows[r], 1);
            else if (string.Equals(c0, "Financial Year", StringComparison.OrdinalIgnoreCase))
                referenceYear = ExtractYear(sheet.Rows[r].Count > 1 ? sheet.Rows[r][1] : null);
            else if (string.Equals(c0, "Metrics", StringComparison.OrdinalIgnoreCase)) { metricsRow = r; break; }
        }
        if (metricsRow < 0)
        {
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "PEER_METRICS_HEADER_NOT_FOUND", "Could not locate the 'Metrics' header row."));
            ParsePeerBlock(sheet, requestId, ingestionRunId, sourceDocumentId, referenceYear, industry, segment, peers, result);
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
        // Fall back to the latest metrics year if the "Financial Year" row was absent.
        referenceYear ??= yearByCol.Count > 0 ? yearByCol.Values.Max() : null;

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

                result.Items.Add(new PeerComparisonMetric
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
                });
            }
        }

        ParsePeerBlock(sheet, requestId, ingestionRunId, sourceDocumentId, referenceYear, industry, segment, peers, result);
        return result;
    }

    /// <summary>Reads the "5 Closest Peers by Revenue" block: a banner, a "Legal Name / CIN / City /
    /// Revenue (Rs. Crore)" column header, then one row per peer (the list includes the company
    /// itself — a caller flags that by matching <see cref="PeerCompany.Cin"/>). The block is capped at
    /// five rows: a sixth structured row means a footer / following section bled in, so it warns and
    /// stops.</summary>
    private const int MaxClosestPeers = 5;

    private static void ParsePeerBlock(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        int? referenceYear, string? industry, string? segment,
        List<PeerCompany> peers, ParseResult<PeerComparisonMetric> result)
    {
        var bannerRow = -1;
        for (var r = 0; r < sheet.Rows.Count; r++)
            if ((Cell(sheet.Rows[r], 0) ?? "").StartsWith("5 Closest Peers", StringComparison.OrdinalIgnoreCase))
            {
                bannerRow = r;
                break;
            }
        if (bannerRow < 0) return;

        var rank = 0;
        for (var r = bannerRow + 1; r < sheet.Rows.Count; r++)
        {
            var name = Cell(sheet.Rows[r], 0);
            if (name is null) break;   // block ends at the first blank row
            if (name.StartsWith("Legal Name", StringComparison.OrdinalIgnoreCase)) continue;   // column header

            if (rank >= MaxClosestPeers)
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, name,
                    "PEER_BLOCK_OVERFLOW",
                    $"'5 Closest Peers by Revenue' has more than {MaxClosestPeers} structured rows — stopping at row {r + 1} " +
                    $"('{name}'); a footer or a following section may have bled into the block.", r + 1));
                break;
            }

            peers.Add(new PeerCompany
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                FinancialYear = referenceYear,   // null (not 0) when the sheet carries no year — incomplete, not "year zero"
                Rank = ++rank,
                LegalName = name,
                Cin = Cell(sheet.Rows[r], 1),
                City = Cell(sheet.Rows[r], 2),
                RevenueCrore = AmountNormalizer.TryParse(At(sheet.Rows[r], 3), out var rev, out _) ? rev : null,
                Industry = industry,
                Segment = segment
            });
        }

        if (peers.Count > 0 && referenceYear is null)
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName,
                nameof(PeerCompany.FinancialYear), null, "PEER_BLOCK_NO_YEAR",
                "Captured the closest-peers block but the sheet carries no 'Financial Year' row and no " +
                "metric-year headers — the peer rows have no reference year.", bannerRow + 1));
    }

    private static int? ExtractYear(object? cell)
    {
        switch (cell)
        {
            case double d when d is >= 1990 and <= 2100: return (int)d;
            case DateTime dt: return dt.Year;
        }
        var text = cell?.ToString() ?? "";
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 && int.TryParse(digits[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            && y is >= 1990 and <= 2100 ? y : null;
    }

    private static object? At(IReadOnlyList<object?> row, int i) => i < row.Count ? row[i] : null;

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
