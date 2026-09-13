using System.Globalization;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Models.Viz;

/// <summary>Maps the fleet-wide Dashboard's own data shapes into the shared chart contract — proves the
/// contract serves both domains, not just the single-request dossier side. <see cref="ToChartSeries"/>
/// is kept from #116; <see cref="ToPriorityDistributionSeries"/>/<see cref="ToFindingsBySectionSeries"/>
/// are #120's (C9) additions for the Dashboard's other two charts. Wiring these into
/// <c>Views/Dashboard/Index.cshtml</c> and retiring its Chart.js canvases is C9's own scope.</summary>
public static class DashboardChartMapping
{
    // Fixed display orders — match today's dashboard.js exactly (priorityOrder / sectionOrder /
    // severityOrder) so the migration changes rendering technology, not what's shown or in what order.
    private static readonly ReviewPriority[] PriorityOrder = [ReviewPriority.High, ReviewPriority.Medium, ReviewPriority.Low];
    private static readonly FindingSection[] SectionOrder =
    [
        FindingSection.CompanyProfile, FindingSection.Directors, FindingSection.DirectorNetwork,
        FindingSection.Ownership, FindingSection.Financial, FindingSection.Charges, FindingSection.Msme,
        FindingSection.Gst, FindingSection.Epfo, FindingSection.Auditor, FindingSection.Litigation,
        FindingSection.CrossSection,
    ];
    private static readonly FindingSeverity[] SeverityOrder = [FindingSeverity.Critical, FindingSeverity.Review, FindingSeverity.Watch];

    public static ChartSeries ToChartSeries(IReadOnlyList<TrendPoint> trend, TrendGranularity granularity)
    {
        var points = trend
            .Select(t => new ChartTimePoint(
                ChartPeriod.ForDate(t.BucketStart, FormatLabel(t.BucketStart, granularity)),
                t.Created))
            .ToList();

        return ChartSeries.Create("Requests Created", MetricUnit.Count, ["RequestSummaryRow.CreatedDate"], points);
    }

    /// <summary>Maps <c>ReviewPriority -&gt; count</c> into a horizontal-bar-ready series — fixed
    /// High/Medium/Low order, zero-count priorities excluded, each with its semantic severity color.
    /// Returns <c>null</c> when nothing survives the filter (no priority has a positive count) — this
    /// null *is* the "no data" guard, replacing both the old Razor <c>Count == 0</c> check and the old
    /// JS's separate <c>priorityLabels.length > 0</c> check with one.</summary>
    public static ChartCategorySeries? ToPriorityDistributionSeries(IReadOnlyDictionary<ReviewPriority, int> distribution)
    {
        var points = PriorityOrder
            .Where(p => distribution.TryGetValue(p, out var count) && count > 0)
            .Select(p => new ChartCategoryPoint(p.ToString(), distribution[p], null, null, AccentFor(p)))
            .ToList();

        return points.Count == 0
            ? null
            : ChartCategorySeries.Create("Review Priority Distribution", MetricUnit.Count, ["RequestSummaryRow.LatestReviewPriority"], points);
    }

    private static ChartAccent AccentFor(ReviewPriority priority) => priority switch
    {
        ReviewPriority.High => ChartAccent.Danger,
        ReviewPriority.Medium => ChartAccent.Warning,
        _ => ChartAccent.Success
    };

    /// <summary>Maps <c>(FindingSection, FindingSeverity) -&gt; count</c> into a stacked-horizontal-bar
    /// series — fixed section order, fixed Critical/Review/Watch stack order, a section with no findings
    /// at all excluded, every included section carrying an explicit 0 for any severity it has none of
    /// (never an omitted segment — required by <see cref="ChartStackedCategorySeries.Create"/>'s
    /// exact-segment-set rule). Returns <c>null</c> when no section survives.</summary>
    public static ChartStackedCategorySeries? ToFindingsBySectionSeries(IReadOnlyList<SectionSeverityCount> findings)
    {
        var countsBySection = findings
            .GroupBy(f => f.Section)
            .ToDictionary(g => g.Key, g => g.ToDictionary(f => f.Severity, f => f.Count));

        var points = SectionOrder
            .Where(section => countsBySection.TryGetValue(section, out var bySeverity) && bySeverity.Values.Sum() > 0)
            .Select(section =>
            {
                var bySeverity = countsBySection[section];
                var segments = SeverityOrder
                    .Select(sev => new ChartStackedSegment(sev.ToString(), bySeverity.GetValueOrDefault(sev, 0), AccentFor(sev)))
                    .ToList();
                return new ChartStackedCategoryPoint(section.ToString(), segments);
            })
            .ToList();

        return points.Count == 0
            ? null
            : ChartStackedCategorySeries.Create(
                "Findings by Risk Category", MetricUnit.Count,
                ["AnalysisFinding.Section", "AnalysisFinding.Severity"],
                SeverityOrder.Select(s => s.ToString()).ToList(),
                points);
    }

    private static ChartAccent AccentFor(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => ChartAccent.Danger,
        FindingSeverity.Review => ChartAccent.Warning,
        _ => ChartAccent.Muted
    };

    // InvariantCulture on purpose: a chart label is machine-generated display text, not locale-sensitive
    // prose, and letting it depend on the server's ambient culture (as DetailsFormat.Money() did before
    // #123/C5b's fix) would make it non-deterministic across dev machines and CI runners — e.g. "MMM"
    // renders "Sept" instead of "Sep" for September under some cultures' calendar data.
    private static string FormatLabel(DateOnly bucketStart, TrendGranularity granularity) => granularity switch
    {
        TrendGranularity.Monthly => bucketStart.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        _ => bucketStart.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
    };
}
