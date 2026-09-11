using MCAROC_Analysis.Services.Excel;
using Xunit;

namespace MCAROC_Analysis.Tests.CatalogueCoverage;

/// <summary>A7 (#38) — turns <c>docs/data-coverage-catalogue.json</c> from documentation into an
/// enforced contract:
///   1. Every field entry marked <c>not-parsed</c> / <c>parsed-not-shown</c> must name a gap id that is
///      both tracked in the catalogue's <c>gaps[]</c> list AND still open there — not a gap the
///      catalogue itself already marks "DONE (...)" (catches a catalogue row going stale: the gap
///      closed elsewhere but this field row was never updated to 'live'/'dropped-by-design') — always
///      runs, no fixture needed.
///   2. Every column header in the real COASTAL workbooks must be represented in the catalogue by some
///      field entry (any status) — a brand-new or renamed column with zero catalogue entry fails loudly,
///      telling the dev to add a row. Needs the real (git-ignored) fixtures, so it's a <see cref="SkippableFact"/>
///      like the rest of <see cref="SourceReconciliationTests"/>.
///
/// Most sheets are flat tables with one column-header row, matched against the catalogue's "source" text
/// (normalized: uppercased, non-alphanumeric collapsed to spaces, then a two-way substring containment —
/// the catalogue is allowed to abbreviate/paraphrase a header as long as the core words survive).
/// A handful of sheets don't have a single named-column header at all:
///   - "About the Company": a label/value sheet — every row IS a field, so its "header" is the set of
///     distinct labels.
///   - "Structure": a label/value summary block (checked the normal way) plus two SEBI-category grids
///     whose 2-row header ("CATEGORY | EQUITY | | PREFERENCE" / "| Number of Shares | Percentage | ...")
///     is layout scaffolding, not named fields — StructureParser reads those 4 data columns by trusted
///     position, never by header text, and the real category/value coverage is already exhaustively
///     pinned by <c>SourceReconciliationTests.Structure_sheet_captures_both_shareholding_pattern_grids</c>
///     (28 rows, exact category names, exact values). Checking header prose here would be theatre.
///   - "Standalone Financial Data" / "Consolidated Financial Data": a matrix whose ROW labels are the
///     variable dimension (~55 line items, differs per company) and are already guaranteed complete by
///     construction — <c>StandaloneFinancialDataParser</c> maps ~25 by name and captures every other
///     non-structural label as a <see cref="MCAROC_Analysis.Data.Entities.FinancialFact"/>; nothing can
///     fall through. Enumerating ~55 line-item names in the catalogue would be brittle busywork for a
///     guarantee the code already provides architecturally.
///   - "Peer Comparison": Industry/Segment/Financial-Year and the "5 Closest Peers" block are checked
///     normally; the 17-row metrics grid is excluded for the same reason as the financial matrix —
///     <c>PeerComparisonParser</c> turns every row between the header and the closest-peers banner into
///     a <see cref="MCAROC_Analysis.Data.Entities.PeerComparisonMetric"/> unconditionally, no allow-list.
/// </summary>
public class CatalogueCoverageTests
{
    static CatalogueCoverageTests() =>
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); // legacy .xls encodings

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static (string Roc, string Charge)? Fixtures()
    {
        var dir = Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis.Tests", "Fixtures", "workbooks");
        var roc = Path.Combine(dir, "roc.xls");
        var charge = Path.Combine(dir, "charge.xls");
        if (File.Exists(roc) && File.Exists(charge)) return (roc, charge);

        var env = Environment.GetEnvironmentVariable("MCAROC_RECON_FIXTURES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var r = Path.Combine(env, "U45203OR1995PLC003982.xls");
            var c = Path.Combine(env, "U45203OR1995PLC003982-charge.xls");
            if (File.Exists(r) && File.Exists(c)) return (r, c);
        }
        return null;
    }

    // ── Rule 4: every not-parsed / parsed-not-shown field names a tracked, still-open gap ──

    [Fact]
    public void Every_incomplete_field_references_a_tracked_gap()
    {
        var violations = FindGapViolations(CatalogueRoot.Load(RepoRoot()));
        Assert.True(violations.Count == 0,
            $"{violations.Count} catalogue row(s) are incomplete without a tracked, open gap id:\n" + string.Join("\n", violations));
    }

    /// <summary>Pure so <see cref="HeaderGroupsTests"/> can pin all three failure modes against
    /// synthetic data: no gap id, a gap id absent from gaps[], and — the one PR #89's first version
    /// missed (Codex review) — a gap id that IS tracked but already marked done, which is exactly the
    /// stale-catalogue regression this rule exists to catch (the gap moved on, the field row didn't).</summary>
    internal static List<string> FindGapViolations(CatalogueRoot catalogue)
    {
        var gapsById = catalogue.Gaps.ToDictionary(g => g.Id);
        var violations = new List<string>();

        foreach (var sheet in catalogue.Sheets)
            foreach (var field in sheet.Fields)
            {
                if (field.Status is not ("not-parsed" or "parsed-not-shown")) continue;

                if (string.IsNullOrWhiteSpace(field.Gap))
                {
                    violations.Add($"{sheet.Workbook}/{sheet.Sheet}: \"{field.Source}\" is '{field.Status}' but names no gap id");
                }
                else if (!gapsById.TryGetValue(field.Gap, out var gap))
                {
                    violations.Add($"{sheet.Workbook}/{sheet.Sheet}: \"{field.Source}\" references gap '{field.Gap}', which is not in gaps[]");
                }
                else if (gap.IsDone)
                {
                    violations.Add($"{sheet.Workbook}/{sheet.Sheet}: \"{field.Source}\" is '{field.Status}' but references gap '{field.Gap}', " +
                        "which the catalogue already marks done — either the field row is stale (should be 'live'/'dropped-by-design' now) or the gap closed prematurely");
                }
            }

        return violations;
    }

    // ── Rules 3 + 5: every real workbook column is catalogued ──

    [SkippableFact]
    public void Every_real_workbook_column_is_catalogued()
    {
        Skip.If(Fixtures() is null,
            "Reconciliation workbooks not present — see MCAROC_Analysis.Tests/Fixtures/README.md");
        var fx = Fixtures()!.Value;
        var catalogue = CatalogueRoot.Load(RepoRoot());
        var reader = new ExcelSheetReader();

        var catalogueBySheet = new Dictionary<(string Workbook, string Sheet), List<CatalogueField>>();
        foreach (var s in catalogue.Sheets)
            foreach (var name in s.SheetNames)
                catalogueBySheet[(s.Workbook, name)] = s.Fields;

        var problems = new List<string>();

        void CheckSheet(string workbook, SheetData sheet)
        {
            if (!catalogueBySheet.TryGetValue((workbook, sheet.Name), out var fields))
            {
                problems.Add($"{workbook}/{sheet.Name}: present in the real workbook but has NO catalogue entry at all — add a row to docs/data-coverage-catalogue.json");
                return;
            }

            var pool = fields.SelectMany(f => f.Chunks).Select(Norm).Where(c => c.Length > 0).ToList();

            foreach (var group in HeaderGroups.Extract(workbook, sheet))
                foreach (var cell in group.Cells)
                {
                    var n = Norm(cell);
                    if (n.Length == 0) continue;
                    if (!pool.Any(p => n.Contains(p) || p.Contains(n)))
                        problems.Add($"{workbook}/{sheet.Name} [{group.Label}]: column '{cell}' has no matching catalogue entry");
                }
        }

        foreach (var sheet in reader.ReadWorkbook(fx.Roc)) CheckSheet("RocReport", sheet);
        foreach (var sheet in reader.ReadWorkbook(fx.Charge)) CheckSheet("ChargeReport", sheet);

        Assert.True(problems.Count == 0,
            $"{problems.Count} workbook column(s) not represented in docs/data-coverage-catalogue.json:\n" + string.Join("\n", problems));
    }

    /// <summary>Uppercase, collapse every non-alphanumeric run to a single space, trim. Deliberately
    /// keeps unit/qualifier words ("RS CRORE", "FOR INACTIVE GSTINS") rather than stripping them —
    /// two-way containment still matches as long as the catalogue's (shorter, paraphrased) text is a
    /// substring of the real header, or vice versa.</summary>
    internal static string Norm(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
