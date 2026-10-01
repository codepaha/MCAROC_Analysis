using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Chat;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#338's chat routes, without a database or model: dossier metrics become "M" sources for quantitative
/// questions, and a list-all question gets an "S" note that the D/L passages are a top-K sample (#193 Finding 2).</summary>
public class MetricAndCoverageChatSourcesTests
{
    private static readonly IReadOnlyList<MetricGroup> Groups =
    [
        new("Charge register", [
            MetricResult.Ok("Open charges", 2m, MetricUnit.Count, "Current", "Charge.Status"),
            MetricResult.Insufficient("Secured debt to net worth", MetricUnit.Ratio, "Net worth not filed", "FinancialYearData.NetWorth")
        ]),
        new("Directors", [MetricResult.Ok("Director count", 3m, MetricUnit.Count, "As of FY2026", "Director.Name")])
    ];

    [Fact]
    public void EachMetric_BecomesAnMSource_WithValuePeriodAndInputs_OrItsInsufficiencyReason()
    {
        var sources = new List<RetrievedSource>();
        RetrievalContextBuilder.AddMetricSources(sources, Groups);

        Assert.Equal(["M1", "M2", "M3"], sources.Select(s => s.Tag));
        Assert.All(sources, s => Assert.Equal(SourceType.Metric, s.Type));
        Assert.Contains("Open charges", sources[0].Text);
        Assert.Contains("Current", sources[0].Text);
        Assert.Contains("Charge.Status", sources[0].Text);
        Assert.Equal("Charge register", sources[0].EntityType);
        Assert.Contains("not computed", sources[1].Text);
        Assert.Contains("Net worth not filed", sources[1].Text);
        Assert.Equal("Directors", sources[2].EntityType);
    }

    [Fact]
    public void MetricSources_AreCapped()
    {
        var many = Enumerable.Range(1, RetrievalContextBuilder.MaxMetricSources + 10)
            .Select(i => MetricResult.Ok($"Metric {i}", i, MetricUnit.Count, "Current", "X.Y")).ToList();
        var sources = new List<RetrievedSource>();
        RetrievalContextBuilder.AddMetricSources(sources, [new MetricGroup("Big", many)]);

        Assert.Equal(RetrievalContextBuilder.MaxMetricSources, sources.Count);
    }

    [Fact]
    public void SearchCoverage_StatesRetrievedAndIndexedCounts_AndThatTheListIsNotExhaustive()
    {
        var sources = new List<RetrievedSource>();
        RetrievalContextBuilder.AddSearchCoverageSource(sources, 8, 1200, 5, 3400);

        var note = Assert.Single(sources);
        Assert.Equal("S1", note.Tag);
        Assert.Equal(SourceType.SearchCoverage, note.Type);
        Assert.Contains("8 most relevant of 1200", note.Text);
        Assert.Contains("5 most relevant of 3400", note.Text);
        Assert.Contains("NOT exhaustive", note.Text);
    }

    [Fact]
    public void CitedMetricAndCoverage_ResolveToTheirOwnCitationTypes_NotStructuredFact()
    {
        var sources = new List<RetrievedSource>();
        RetrievalContextBuilder.AddMetricSources(sources, Groups);
        RetrievalContextBuilder.AddSearchCoverageSource(sources, 8, 100, 0, 0);

        var result = ChatCompletionService.Validate(
            """{ "answer": "There are 2 open charges; this may not be complete.", "citedTags": ["M1", "S1"], "insufficientEvidence": false }""", sources);

        Assert.False(result.InsufficientEvidence);
        Assert.Equal(["Metric", "SearchCoverage"], result.CitedSources.Select(c => c.SourceType));
        Assert.Equal("Charge register", result.CitedSources[0].EntityType);
        Assert.Null(result.CitedSources[0].DocumentId);
    }
}
