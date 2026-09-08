using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Tests;

/// <summary>Tests AiCrossSectionAnalysisService's internal Validate method directly against constructed
/// response JSON — the same boundary Phase 2 accepted for VertexAiExtractionService (no live-call testing,
/// since the constructor eagerly loads Google Cloud credentials from disk).</summary>
public class AiCrossSectionAnalysisServiceValidationTests
{
    private static AnalysisFinding Finding(string code, FindingSeverity severity, string? metricsJson = null, string? periodLabel = null, string? supportingSignalsJson = null, FindingSection section = FindingSection.Financial) =>
        new() { Code = code, Severity = severity, Section = section, Title = code, SummaryText = code, MetricsJson = metricsJson, PeriodLabel = periodLabel, SupportingSignalsJson = supportingSignalsJson };

    [Fact]
    public void GeneralCountingLanguage_IsNeverDropped()
    {
        var findings = new List<AnalysisFinding>
        {
            Finding("FIN_REVENUE_DECLINE_1Y", FindingSeverity.Review,
                metricsJson: """{"priorYear":2024,"latestYear":2025,"changePercent":-25.0}""", periodLabel: "FY2024-FY2025")
        };
        var response = """
            {
              "executiveSummary": { "businessPerformance": "x", "financialPosition": "x", "borrowingSecurity": "x", "governanceCompliance": "x", "keyReviewItems": [] },
              "findingNarratives": [{ "code": "FIN_REVENUE_DECLINE_1Y", "whyThisMatters": "Revenue declined over two consecutive years, a concerning trend.", "recommendedReview": "Obtain management commentary on the decline." }],
              "crossSectionFindings": []
            }
            """;

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        Assert.True(outcome.Success);
        var narrative = Assert.Single(outcome.FindingNarratives);
        Assert.Equal("FIN_REVENUE_DECLINE_1Y", narrative.Code);
    }

    [Fact]
    public void SupportedPercentFromMetrics_IsNotDropped()
    {
        var findings = new List<AnalysisFinding>
        {
            Finding("FIN_REVENUE_DECLINE_1Y", FindingSeverity.Review, metricsJson: """{"changePercent":-25.0}""")
        };
        var response = """
            {
              "executiveSummary": null,
              "findingNarratives": [{ "code": "FIN_REVENUE_DECLINE_1Y", "whyThisMatters": "Revenue declined by 25%, which is material.", "recommendedReview": "Review." }],
              "crossSectionFindings": []
            }
            """;

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        Assert.Single(outcome.FindingNarratives);
    }

    [Fact]
    public void UnsupportedPercent_IsDroppedNotWholeRunFailed()
    {
        var findings = new List<AnalysisFinding>
        {
            Finding("FIN_REVENUE_DECLINE_1Y", FindingSeverity.Review, metricsJson: """{"changePercent":-25.0}""")
        };
        var response = """
            {
              "executiveSummary": null,
              "findingNarratives": [{ "code": "FIN_REVENUE_DECLINE_1Y", "whyThisMatters": "Revenue declined by 40%, which is severe.", "recommendedReview": "Review." }],
              "crossSectionFindings": []
            }
            """;

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        Assert.True(outcome.Success); // run still succeeds
        Assert.Empty(outcome.FindingNarratives); // but the unsupported narrative is dropped
    }

    [Fact]
    public void NarrativeForWatchSeverityFinding_IsDropped()
    {
        var findings = new List<AnalysisFinding> { Finding("SOME_WATCH_CODE", FindingSeverity.Watch) };
        var response = """
            {
              "findingNarratives": [{ "code": "SOME_WATCH_CODE", "whyThisMatters": "x", "recommendedReview": "y" }],
              "crossSectionFindings": []
            }
            """;

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        Assert.Empty(outcome.FindingNarratives);
    }

    [Fact]
    public void CrossSectionFinding_DuplicatingDeterministicOne_IsRejected()
    {
        var findings = new List<AnalysisFinding>
        {
            Finding("A", FindingSeverity.Review),
            Finding("B", FindingSeverity.Review),
            Finding("CROSS_EXISTING", FindingSeverity.Review, section: FindingSection.CrossSection, supportingSignalsJson: """["A","B"]""")
        };
        var response = """
            {
              "findingNarratives": [],
              "crossSectionFindings": [{ "title": "Duplicate", "severity": "Review", "narrative": "x", "relatedCodes": ["A", "B"] }]
            }
            """;

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        Assert.Empty(outcome.CrossSectionFindings);
    }

    [Fact]
    public void CrossSectionFinding_SeverityClampedToHighestAdverseRelated_NoEscalation()
    {
        var findings = new List<AnalysisFinding> { Finding("A", FindingSeverity.Review), Finding("B", FindingSeverity.Watch) };
        var response = """
            {
              "findingNarratives": [],
              "crossSectionFindings": [{ "title": "Combo", "severity": "Critical", "narrative": "combined signal", "relatedCodes": ["A", "B"] }]
            }
            """;

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        var cross = Assert.Single(outcome.CrossSectionFindings);
        Assert.Equal(FindingSeverity.Review, cross.Severity); // clamped to the highest adverse related severity (Review), never escalated to Critical
    }

    [Fact]
    public void CrossSectionFindings_RequireAtLeastTwoRelatedCodes()
    {
        var findings = new List<AnalysisFinding> { Finding("A", FindingSeverity.Review) };
        var response = """
            {
              "findingNarratives": [],
              "crossSectionFindings": [{ "title": "Single", "severity": "Review", "narrative": "x", "relatedCodes": ["A"] }]
            }
            """;

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        Assert.Empty(outcome.CrossSectionFindings);
    }

    [Fact]
    public void CrossSectionFindings_CappedAtThreeAccepted()
    {
        var findings = Enumerable.Range(0, 8).Select(i => Finding($"C{i}", FindingSeverity.Review)).ToList();
        var proposals = string.Join(",", Enumerable.Range(0, 4).Select(i =>
            $$"""{ "title": "Combo{{i}}", "severity": "Review", "narrative": "x", "relatedCodes": ["C{{i * 2}}", "C{{i * 2 + 1}}"] }"""));
        var response = $$"""{ "findingNarratives": [], "crossSectionFindings": [{{proposals}}] }""";

        var outcome = AiCrossSectionAnalysisService.Validate(response, findings);

        Assert.Equal(3, outcome.CrossSectionFindings.Count);
    }

    [Fact]
    public void InvalidJson_FailsGracefullyWithReason()
    {
        var outcome = AiCrossSectionAnalysisService.Validate("not json", []);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.FailureReason);
    }

    [Fact]
    public void EmptyResponse_FailsGracefully()
    {
        var outcome = AiCrossSectionAnalysisService.Validate("", []);

        Assert.False(outcome.Success);
    }
}
