using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#123 (C5b) — turns the amount-unit-toggle call-site inventory
/// (<c>docs/c5b-amount-call-site-inventory.md</c>) from documentation into an enforced contract, the
/// same way <see cref="CatalogueCoverage.CatalogueCoverageTests"/> does for the data-coverage catalogue.
///
/// The literal <c>data-amount-crore="…"</c> attribute exists exactly ONCE, inside
/// <c>Details/_Amount.cshtml</c> itself — the call sites don't repeat it, they each invoke the partial
/// once (<c>&lt;partial name="Details/_Amount" .../&gt;</c>). So this reads the 6 target views' raw
/// source from disk and counts partial invocations, not the attribute. A companion test in
/// <see cref="AmountRenderingTests"/> separately proves the partial itself emits exactly one
/// <c>data-amount-crore</c> marker per non-null invocation — together the two levels (call-site count +
/// partial-output count) make "every counted site is actually marked" an enforced invariant, not a
/// claim. Any future edit that adds, removes, or accidentally un-wraps a call site changes one of these
/// counts and fails the build.</summary>
public class AmountToggleCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("MCAROC.Portal not found in parent hierarchy");
    }

    private static string ViewsDir() =>
        Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis", "Views", "Requests", "Details");

    private const string PartialMarker = "<partial name=\"Details/_Amount\"";
    private const string LabelMarker = "data-amount-unit-label=\"";

    /// <summary>Exact per-file counts from the audited inventory
    /// (<c>docs/c5b-amount-call-site-inventory.md</c>) — every one of the 43 crore-amount value sites
    /// and 21 unit-label sites found by direct line-by-line inspection of these six views.</summary>
    private static readonly (string File, int AmountSites, int LabelSites)[] Expected =
    [
        ("_ChargeDrawer.cshtml", 9, 0),
        ("_ChargesTab.cshtml", 6, 4),
        ("_ComplianceTab.cshtml", 10, 4),
        ("_CorporateTab.cshtml", 12, 8),
        ("_FinancialStatement.cshtml", 1, 1),
        ("_FinancialsTab.cshtml", 5, 4),
    ];

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public void Every_target_view_has_exactly_its_audited_number_of_amount_and_label_sites()
    {
        var mismatches = new List<string>();

        foreach (var (file, expectedAmounts, expectedLabels) in Expected)
        {
            var path = Path.Combine(ViewsDir(), file);
            Assert.True(File.Exists(path), $"Expected view not found: {path}");
            var source = File.ReadAllText(path);

            var actualAmounts = Count(source, PartialMarker);
            var actualLabels = Count(source, LabelMarker);

            if (actualAmounts != expectedAmounts)
                mismatches.Add($"{file}: expected {expectedAmounts} _Amount invocation(s), found {actualAmounts}");
            if (actualLabels != expectedLabels)
                mismatches.Add($"{file}: expected {expectedLabels} data-amount-unit-label site(s), found {actualLabels}");
        }

        Assert.True(mismatches.Count == 0,
            "The amount-unit-toggle call-site inventory has drifted from docs/c5b-amount-call-site-inventory.md:\n" +
            string.Join("\n", mismatches));
    }

    [Fact]
    public void Total_amount_and_label_sites_across_all_six_views_match_the_audited_inventory()
    {
        var totalAmounts = 0;
        var totalLabels = 0;
        foreach (var (file, _, _) in Expected)
        {
            var source = File.ReadAllText(Path.Combine(ViewsDir(), file));
            totalAmounts += Count(source, PartialMarker);
            totalLabels += Count(source, LabelMarker);
        }

        Assert.Equal(Expected.Sum(e => e.AmountSites), totalAmounts);
        Assert.Equal(Expected.Sum(e => e.LabelSites), totalLabels);
    }

    [Fact]
    public void Peer_comparison_metrics_table_stays_unwrapped_its_unit_varies_per_metric_name()
    {
        // _FinancialsTab's Peer Comparison grid (PeerComparisonMetric.CompanyValue/PeerMedianValue) can
        // hold a percentage, a ratio, or a crore amount depending on MetricName — a single toggle-aware
        // wrapper would be actively wrong for the non-crore rows, so it's deliberately out of scope.
        var source = File.ReadAllText(Path.Combine(ViewsDir(), "_FinancialsTab.cshtml"));

        Assert.Contains("@Num(m.CompanyValue)", source);
        Assert.Contains("@Num(m.PeerMedianValue)", source);
    }

    [Fact]
    public void Financial_ratios_panel_stays_unwrapped_its_values_are_not_crore_amounts()
    {
        // _FinancialStatement.cshtml's one shared RenderTable serves both the P&L/BS/CF panels (crore,
        // wrapped) and the Ratios panel (dimensionless ratios/percentages, never wrapped) — gated on
        // data.Unit == "₹ Crore".
        var source = File.ReadAllText(Path.Combine(ViewsDir(), "_FinancialStatement.cshtml"));

        Assert.Contains("data.Unit == \"₹ Crore\"", source);
        Assert.Contains("@Num(num)", source); // the untouched Ratios-path fallback
    }

    [Fact]
    public void Corporate_tab_divergence_tooltip_no_longer_embeds_a_dynamic_amount()
    {
        // A title="..." attribute can't hold markup, so it can't call the _Amount partial — the fix is
        // to drop the dynamic amounts from the tooltip text entirely (the badge's own visible text,
        // already wrapped, repeats the value), not invent a bespoke attribute-templating path.
        var source = File.ReadAllText(Path.Combine(ViewsDir(), "_CorporateTab.cshtml"));

        Assert.DoesNotContain("title=\"MCA stated sum of charges (@Money", source);
        Assert.DoesNotContain("differs from computed open charges total (@Money", source);
    }
}
