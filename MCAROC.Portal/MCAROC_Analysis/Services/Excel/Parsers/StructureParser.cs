using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Structure" sheet: the "SHARE HOLDING SUMMARY" key/value block at the top
/// (promoter/public split, shareholder counts, total shares → one <see cref="CompanyStructure"/>), and
/// the two SEBI-category grids below it — <c>PROMOTERS</c> and <c>PUBLIC / OTHER THAN PROMOTERS</c> —
/// each a 10-category × {equity shares, equity %, preference shares, preference %} table
/// (→ <see cref="ShareholdingPatternRow"/> per data row).</summary>
public static partial class StructureParser
{
    private const string ParserName = nameof(StructureParser);

    public static ParseResult<CompanyStructure> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId) =>
        Parse(sheet, requestId, ingestionRunId, sourceDocumentId, out _);

    public static ParseResult<CompanyStructure> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        out List<ShareholdingPatternRow> patternRows)
    {
        var result = new ParseResult<CompanyStructure>();
        patternRows = [];

        var s = new CompanyStructure
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = 1
        };

        var any = false;
        ShareholderClass? currentClass = null;
        DateOnly? currentDate = null;
        string? currentGroup = null;
        var order = 0;

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var label = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(label)) continue;
            var value = row.Count > 1 ? row[1] : null;

            // ── Grid section headers switch us out of the summary block. The real headers read
            // "PROMOTERS - 31 Mar, 2017" / "PUBLIC / OTHER THAN PROMOTERS - 31 Mar, 2017"; the " - "
            // date suffix is what distinguishes them from the summary keys "Promoter %" / "Public %". ──
            var isGridHeader = label.Contains(" - ", StringComparison.Ordinal);
            if (isGridHeader && label.StartsWith("PROMOTER", StringComparison.OrdinalIgnoreCase))
            {
                currentClass = ShareholderClass.Promoter;
                currentDate = TrailingDate(label);
                currentGroup = null;
                order = 0;
                continue;
            }
            if (isGridHeader && label.StartsWith("PUBLIC", StringComparison.OrdinalIgnoreCase))
            {
                currentClass = ShareholderClass.Public;
                currentDate = TrailingDate(label);
                currentGroup = null;
                order = 0;
                continue;
            }

            if (currentClass is null)
            {
                // ── SHARE HOLDING SUMMARY key/value block ──
                switch (label.ToUpperInvariant())
                {
                    case "PROMOTER %":
                        if (AmountNormalizer.TryParse(value, out var p, out _)) { s.PromoterHoldingPercent = p; any = true; }
                        break;
                    case "PUBLIC %":
                        if (AmountNormalizer.TryParse(value, out var pub, out _)) { s.PublicHoldingPercent = pub; any = true; }
                        break;
                    case "NO. OF SHAREHOLDERS":
                        if (AmountNormalizer.TryParse(value, out var n, out _) && n is { } nv) { s.TotalShareholders = (int)nv; any = true; }
                        break;
                    case "NO. OF PROMOTER SHAREHOLDERS":
                        if (AmountNormalizer.TryParse(value, out var pn, out _) && pn is { } pnv) { s.PromoterShareholders = (int)pnv; any = true; }
                        break;
                    case "TOTAL EQUITY SHARES":
                        if (AmountNormalizer.TryParse(value, out var eq, out _) && eq is { } eqv) { s.TotalEquityShares = (long)eqv; any = true; }
                        break;
                    case "TOTAL PREFERENCE SHARES":
                        if (AmountNormalizer.TryParse(value, out var pr, out _) && pr is { } prv) { s.TotalPreferenceShares = (long)prv; any = true; }
                        break;
                }
                continue;
            }

            // ── Inside a grid ──
            var upper = label.ToUpperInvariant();
            if (upper is "CATEGORY" or "TOTAL") continue;                 // column header / grand-total line
            if (label.StartsWith("Number of Shares", StringComparison.OrdinalIgnoreCase)) continue;

            var equityShares = Whole(row, 1);
            var equityPercent = Fraction(row, 2);
            var prefShares = Whole(row, 3);
            var prefPercent = Fraction(row, 4);
            var hasValue = equityShares is not null || equityPercent is not null
                || prefShares is not null || prefPercent is not null;

            var isNumbered = NumberedCategory().IsMatch(label);
            var isSubRow = label.StartsWith('(');

            if (isNumbered && !hasValue)
            {
                // Parent-only header, e.g. "1. Individual / Hindu Undivided Family" — its (i)/(ii)/(iii)
                // sub-rows carry the figures.
                currentGroup = label;
                continue;
            }
            if (!hasValue && !isSubRow) continue;                        // stray/blank spacer inside the grid

            patternRows.Add(new ShareholdingPatternRow
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                HolderClass = currentClass.Value,
                AsOnDate = currentDate,
                Category = label,
                CategoryGroup = isSubRow ? currentGroup : null,
                DisplayOrder = ++order,
                EquityShares = equityShares,
                EquityPercent = equityPercent,
                PreferenceShares = prefShares,
                PreferencePercent = prefPercent
            });

            if (isNumbered) currentGroup = null;                         // a numbered data row closes any open group
        }

        if (!any)
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "STRUCTURE_SUMMARY_EMPTY", "No SHARE HOLDING SUMMARY values were found on the Structure sheet."));
        else
            result.Items.Add(s);

        if (currentClass is not null && patternRows.Count == 0)
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "STRUCTURE_GRID_EMPTY", "The PROMOTERS / PUBLIC shareholding-pattern grids were present but no category rows parsed."));

        return result;
    }

    private static long? Whole(IReadOnlyList<object?> row, int col)
    {
        var cell = row.Count > col ? row[col] : null;
        return AmountNormalizer.TryParse(cell, out var v, out _) && v is { } d ? (long)d : null;
    }

    private static decimal? Fraction(IReadOnlyList<object?> row, int col)
    {
        var cell = row.Count > col ? row[col] : null;
        return AmountNormalizer.TryParse(cell, out var v, out _) ? v : null;
    }

    private static DateOnly? TrailingDate(string header)
    {
        var dash = header.LastIndexOf('-');
        if (dash < 0 || dash == header.Length - 1) return null;
        return DateNormalizer.TryParse(header[(dash + 1)..].Trim(), out var d) ? d : null;
    }

    [GeneratedRegex(@"^\d+\.")]
    private static partial Regex NumberedCategory();
}
