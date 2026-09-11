using MCAROC_Analysis.Services.Excel;

namespace MCAROC_Analysis.Tests.CatalogueCoverage;

/// <summary>One logical header row's worth of column-label cells, tagged with a label for failure
/// messages (a sheet can carry more than one — a stacked sub-table, a key/value block, ...).</summary>
public sealed record HeaderGroup(string Label, IReadOnlyList<string> Cells);

/// <summary>Locates the real column-header cells of a sheet, sheet-shape by sheet-shape. Every location
/// rule here mirrors logic that already exists in the corresponding parser (title/banner detection,
/// which row index carries the header, where a stacked sub-table's own header sits) — this file doesn't
/// invent new assumptions about the workbooks, it re-derives the same header positions the parsers
/// already trust, so a drift in either place is easy to spot by diffing against the other.</summary>
public static class HeaderGroups
{
    public static IReadOnlyList<HeaderGroup> Extract(string workbook, SheetData sheet) => sheet.Name switch
    {
        "About the Company" => [new HeaderGroup("label/value fields", KeyValueLabels(sheet, stopAtGridHeader: false))],

        "Structure" => [new HeaderGroup("SHARE HOLDING SUMMARY", KeyValueLabels(sheet, stopAtGridHeader: true))],
        // The two SEBI-category grids' 2-row header is layout scaffolding read by trusted column
        // position, not named fields — see the class doc comment. Deliberately no group for them.

        "Highlights" => ExtractHighlights(sheet),
        "Peer Comparison" => ExtractPeerComparison(sheet),
        "Legal History" => ExtractLegalHistory(sheet),
        "Auditors' Comments-Standalone" or "Auditors' Comments-Consolidated" => ExtractAuditors(sheet),
        "Compliance" => ExtractCompliance(sheet),

        // Row-label matrices whose completeness is guaranteed by the parser architecture, not a fixed
        // named-column set — see the class doc comment.
        "Standalone Financial Data" or "Consolidated Financial Data" => [],

        // Row labels vary per company and are captured unconditionally by FinancialParametersParser
        // (every non-blank row becomes a FinancialParameter — no allow-list) — same architectural
        // completeness reasoning as the Standalone/Consolidated Financial Data matrix above.
        "Annexure - Financial Parameters" => [],
        "MSME Supplier Payment Delays" => ExtractByExactCol0(sheet, "Supplier Name", "Supplier Name row"),

        _ => [new HeaderGroup("header row", RowCells(sheet, SimpleHeaderRowIndex(sheet)))]
    };

    // ── Simple flat tables: a title banner (row 0) then the header (row 1), or the header directly (row 0) ──

    private static readonly HashSet<string> TitleThenHeaderSheets =
    [
        "Director Shareholding", "Shareholding More Than 5%", "Related Corporates"
    ];

    private static int SimpleHeaderRowIndex(SheetData sheet) => TitleThenHeaderSheets.Contains(sheet.Name) ? 1 : 0;

    // ── "About the Company" / "Structure"'s summary block: one label per row (col 0), value in col 1+ ──

    private static IReadOnlyList<string> KeyValueLabels(SheetData sheet, bool stopAtGridHeader)
    {
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in sheet.Rows)
        {
            var c0 = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(c0)) continue;

            // Structure: a grid title ("PROMOTERS - 31 Mar, 2017") ends the summary block — checked
            // before the lone-value-row skip below, since the grid title row IS a lone value.
            if (stopAtGridHeader && c0.Contains(" - ", StringComparison.Ordinal)
                && (c0.StartsWith("PROMOTER", StringComparison.OrdinalIgnoreCase) || c0.StartsWith("PUBLIC", StringComparison.OrdinalIgnoreCase)))
                break;

            // Structure only: a lone value in col 0 with nothing else on the row is the sheet's own
            // "SHARE HOLDING SUMMARY" banner, not a label — mirrors LegalHistoryParser.IsSectionTitle.
            // (Not applied to About the Company: a genuinely blank value there is still a real field.)
            if (stopAtGridHeader && row.Skip(1).All(c => string.IsNullOrEmpty(c?.ToString()?.Trim()))) continue;

            // Section banners ("REGISTERED ADDRESS:") and footnotes aren't fields of their own.
            if (c0.EndsWith(':')) continue;
            if (c0.StartsWith('*')) continue;
            if (c0.Contains('@')) continue; // an email-address continuation row, not a label

            if (seen.Add(c0)) labels.Add(c0);
        }

        return labels;
    }

    // ── "Highlights": FINANCIAL PARAMETERS (handled generically elsewhere via Annexure's own check —
    // the Highlights copy of that block shares the exact same "Parameter" header), then PRINCIPAL
    // BUSINESS ACTIVITIES ("Main Activity Group Code | ..." header) and NAME HISTORY ("Name | Till Date"). ──

    private static IReadOnlyList<HeaderGroup> ExtractHighlights(SheetData sheet)
    {
        var groups = new List<HeaderGroup>();
        var pba = FindRow(sheet, r => Col0(sheet, r)?.StartsWith("Main Activity Group Code", StringComparison.OrdinalIgnoreCase) == true);
        if (pba is { } pr) groups.Add(new HeaderGroup("PRINCIPAL BUSINESS ACTIVITIES header", RowCells(sheet, pr)));

        var nameHist = FindRow(sheet, r => Col0(sheet, r)?.Equals("Name", StringComparison.OrdinalIgnoreCase) == true);
        if (nameHist is { } nr) groups.Add(new HeaderGroup("NAME HISTORY header", RowCells(sheet, nr)));

        return groups;
    }

    // ── "Peer Comparison": Industry/Segment/Financial Year key-values + the closest-peers block header.
    // The 17-row metrics grid is excluded — see the class doc comment. ──

    private static IReadOnlyList<HeaderGroup> ExtractPeerComparison(SheetData sheet)
    {
        var groups = new List<HeaderGroup>();
        var keyValues = new List<string>();
        foreach (var label in new[] { "Industry", "Segment", "Financial Year" })
            if (FindRow(sheet, r => Col0(sheet, r)?.Equals(label, StringComparison.OrdinalIgnoreCase) == true) is not null)
                keyValues.Add(label);
        if (keyValues.Count > 0) groups.Add(new HeaderGroup("comparison key/values", keyValues));

        var banner = FindRow(sheet, r => (Col0(sheet, r) ?? "").StartsWith("5 Closest Peers", StringComparison.OrdinalIgnoreCase));
        if (banner is { } br)
        {
            var headerRow = FindRow(sheet, r => (Col0(sheet, r) ?? "").StartsWith("Legal Name", StringComparison.OrdinalIgnoreCase), br + 1);
            if (headerRow is { } hr) groups.Add(new HeaderGroup("5 Closest Peers by Revenue header", RowCells(sheet, hr)));
        }
        return groups;
    }

    // ── "Legal History": three stacked sub-tables, each its own column layout (PR #29 / LegalHistoryParser). ──

    private static IReadOnlyList<HeaderGroup> ExtractLegalHistory(SheetData sheet)
    {
        var groups = new List<HeaderGroup>();
        if (sheet.Rows.Count > 1) groups.Add(new HeaderGroup("Confirmed header", RowCells(sheet, 1)));

        var probableTitle = FindRow(sheet, r => Col0(sheet, r) == "PROBABLE CASES");
        if (probableTitle is { } pt && pt + 1 < sheet.Rows.Count)
            groups.Add(new HeaderGroup("PROBABLE CASES header", RowCells(sheet, pt + 1)));

        var unverifiedTitle = FindRow(sheet, r => Col0(sheet, r) == "UNVERIFIED COURT RECORDS");
        if (unverifiedTitle is { } ut && ut + 1 < sheet.Rows.Count)
            groups.Add(new HeaderGroup("UNVERIFIED COURT RECORDS header", RowCells(sheet, ut + 1)));

        return groups;
    }

    // ── "Auditors' Comments-*": a year-summary table (row 1 header) then a detail table with its own
    // "Serial Number | ..." header further down (AuditorsParser). ──

    private static IReadOnlyList<HeaderGroup> ExtractAuditors(SheetData sheet)
    {
        var groups = new List<HeaderGroup>();
        if (sheet.Rows.Count > 1) groups.Add(new HeaderGroup("year-summary header", RowCells(sheet, 1)));

        var detail = FindRow(sheet, r => Col0(sheet, r) == "Serial Number");
        if (detail is { } dr) groups.Add(new HeaderGroup("detail-table header", RowCells(sheet, dr)));

        return groups;
    }

    // ── "Compliance": four titled sections, each with its own header row (ComplianceParser's explicit
    // skip-list: "Struck Off Status" / "Case No." / "Description" / "Source"). ──

    private static IReadOnlyList<HeaderGroup> ExtractCompliance(SheetData sheet)
    {
        var groups = new List<HeaderGroup>();
        foreach (var marker in new[] { "Struck Off Status", "Case No.", "Description", "Source" })
        {
            var row = FindRow(sheet, r => Col0(sheet, r) == marker);
            if (row is { } r2) groups.Add(new HeaderGroup($"'{marker}' section header", RowCells(sheet, r2)));
        }
        return groups;
    }

    // ── shared helpers ──

    private static IReadOnlyList<HeaderGroup> ExtractByExactCol0(SheetData sheet, string exact, string label)
    {
        var row = FindRow(sheet, r => Col0(sheet, r) == exact);
        return row is { } r2 ? [new HeaderGroup(label, RowCells(sheet, r2))] : [];
    }

    private static string? Col0(SheetData sheet, int r) =>
        sheet.Rows[r].Count > 0 ? sheet.Rows[r][0]?.ToString()?.Trim() : null;

    private static int? FindRow(SheetData sheet, Func<int, bool> predicate, int startAt = 0)
    {
        for (var r = startAt; r < sheet.Rows.Count; r++)
            if (predicate(r))
                return r;
        return null;
    }

    private static IReadOnlyList<string> RowCells(SheetData sheet, int rowIndex) =>
        sheet.Rows[rowIndex]
            .Select(c => c?.ToString()?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .ToList();
}
