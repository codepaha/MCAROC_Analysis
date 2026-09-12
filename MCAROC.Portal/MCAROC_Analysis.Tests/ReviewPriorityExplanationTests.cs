using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Tests;

/// <summary>ReviewPriorityCalculator.Explain (#118, C8) — re-derives the same priority Calculate would
/// have produced, plus every condition that fired. No migration: everything Explain needs (Severity/
/// TemporalStatus/Code/Section) is already durably persisted on AnalysisFinding.</summary>
public class ReviewPriorityExplanationTests
{
    private static AnalysisFinding Finding(FindingSection section, FindingSeverity severity, TemporalStatus temporal, string code) =>
        new() { Section = section, Severity = severity, TemporalStatus = temporal, Code = code, Title = code, SummaryText = code };

    // ── One case per reason code ──

    [Fact]
    public void MultiDomainCritical_fires_for_2_plus_critical_across_2_plus_domains()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_X"),
            Finding(FindingSection.CompanyProfile, FindingSeverity.Critical, TemporalStatus.Current, "CORP_X")
        ]);

        Assert.Equal(ReviewPriority.High, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.MultiDomainCritical], explanation.Reasons);
        Assert.Equal(ReviewPriorityReason.MultiDomainCritical, explanation.PrimaryReason);
    }

    [Fact]
    public void DesignatedCriticalFinding_fires_for_a_designated_code_regardless_of_domain_count()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, FinancialRules.InterestCoverageCriticalCode)
        ]);

        Assert.Equal(ReviewPriority.High, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.DesignatedCriticalFinding], explanation.Reasons);
    }

    [Fact]
    public void CrossSectionCritical_fires_for_a_critical_cross_section_finding()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.CrossSection, FindingSeverity.Critical, TemporalStatus.Current, CrossSectionRules.FinancialStressCode)
        ]);

        Assert.Equal(ReviewPriority.High, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.CrossSectionCritical], explanation.Reasons);
    }

    [Fact]
    public void SingleDomainCritical_fires_for_one_critical_finding_that_did_not_escalate_to_high()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_SOMETHING_ELSE")
        ]);

        Assert.Equal(ReviewPriority.Medium, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.SingleDomainCritical], explanation.Reasons);
    }

    [Fact]
    public void MultipleReviewFindings_fires_for_2_plus_review_findings()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current, "FIN_X"),
            Finding(FindingSection.Msme, FindingSeverity.Review, TemporalStatus.Current, "MSME_X")
        ]);

        Assert.Equal(ReviewPriority.Medium, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.MultipleReviewFindings], explanation.Reasons);
    }

    [Fact]
    public void MaterialTrend_fires_for_a_trend_finding_at_review_severity_or_worse()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Trend, "FIN_DECLINE")
        ]);

        Assert.Equal(ReviewPriority.Medium, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.MaterialTrend], explanation.Reasons);
    }

    [Fact]
    public void No_findings_yields_low_with_no_reasons()
    {
        var explanation = ReviewPriorityCalculator.Explain([]);

        Assert.Equal(ReviewPriority.Low, explanation.Priority);
        Assert.Empty(explanation.Reasons);
        Assert.Null(explanation.PrimaryReason);
        Assert.Equal("no escalating condition", explanation.DescribeAll());
    }

    // ── Combined conditions — every applicable reason must appear, not just the first ──

    [Fact]
    public void Designated_critical_and_cross_section_critical_together_all_three_reasons_appear()
    {
        // A designated-critical Financial finding + a Critical Cross-Section finding is, by construction,
        // also 2+ Critical findings across 2+ domains — proving every applicable reason surfaces, not just
        // the first one that happened to be checked.
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, FinancialRules.InterestCoverageCriticalCode),
            Finding(FindingSection.CrossSection, FindingSeverity.Critical, TemporalStatus.Current, CrossSectionRules.FinancialStressCode)
        ]);

        Assert.Equal(ReviewPriority.High, explanation.Priority);
        Assert.Equal(
            [ReviewPriorityReason.MultiDomainCritical, ReviewPriorityReason.DesignatedCriticalFinding, ReviewPriorityReason.CrossSectionCritical],
            explanation.Reasons);
        Assert.Equal(ReviewPriorityReason.MultiDomainCritical, explanation.PrimaryReason);
        Assert.Contains("+", explanation.DescribeAll());
    }

    [Fact]
    public void Single_domain_critical_and_material_trend_together_both_appear()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_SOMETHING_ELSE"),
            Finding(FindingSection.Epfo, FindingSeverity.Review, TemporalStatus.Trend, "EPFO_DECLINE")
        ]);

        Assert.Equal(ReviewPriority.Medium, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.SingleDomainCritical, ReviewPriorityReason.MaterialTrend], explanation.Reasons);
    }

    // ── AI-added cross-section findings must never affect Explain (mirrors Calculate's own contract) ──

    [Fact]
    public void Ai_cross_section_findings_are_excluded_from_explain()
    {
        var explanation = ReviewPriorityCalculator.Explain(
        [
            Finding(FindingSection.Financial, FindingSeverity.Watch, TemporalStatus.Current, "FIN_MINOR"),
            Finding(FindingSection.CrossSection, FindingSeverity.Critical, TemporalStatus.Current,
                $"{AnalysisOrchestrator.AiCrossSectionCodePrefix}abc123")
        ]);

        // Without the exclusion this would be High (a Critical Cross-Section finding) — with it, Low.
        Assert.Equal(ReviewPriority.Low, explanation.Priority);
        Assert.Empty(explanation.Reasons);
    }

    // ── The invariant the reviewer asked for: Explain must never disagree with Calculate on Priority ──

    public static IEnumerable<object[]> EquivalentFixtures()
    {
        yield return
        [
            new (FindingSection Section, FindingSeverity Severity, TemporalStatus Temporal, string Code)[]
            {
                (FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_X"),
                (FindingSection.CompanyProfile, FindingSeverity.Critical, TemporalStatus.Current, "CORP_X")
            }
        ];
        yield return
        [
            new (FindingSection, FindingSeverity, TemporalStatus, string)[]
            {
                (FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, FinancialRules.InterestCoverageCriticalCode)
            }
        ];
        yield return
        [
            new (FindingSection, FindingSeverity, TemporalStatus, string)[]
            {
                (FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current, "FIN_X"),
                (FindingSection.Msme, FindingSeverity.Review, TemporalStatus.Current, "MSME_X")
            }
        ];
        yield return
        [
            new (FindingSection, FindingSeverity, TemporalStatus, string)[]
            {
                (FindingSection.Gst, FindingSeverity.Critical, TemporalStatus.Historical, "GST_HIST")
            }
        ];
        yield return new object[] { Array.Empty<(FindingSection, FindingSeverity, TemporalStatus, string)>() };
    }

    [Theory]
    [MemberData(nameof(EquivalentFixtures))]
    public void Explain_priority_always_equals_calculate_for_the_same_findings(
        (FindingSection Section, FindingSeverity Severity, TemporalStatus Temporal, string Code)[] rows)
    {
        var drafts = rows.Select(r => new FindingDraft(r.Section, r.Severity, r.Temporal, r.Code, r.Code, r.Code)).ToList();
        var persisted = rows.Select(r => Finding(r.Section, r.Severity, r.Temporal, r.Code)).ToList();

        var calculated = ReviewPriorityCalculator.Calculate(drafts);
        var explained = ReviewPriorityCalculator.Explain(persisted);

        Assert.Equal(calculated, explained.Priority);
    }

    // ── Backward compatibility: an old completed run explains correctly with zero backfill — Explain
    // only ever reads fields that have always been on AnalysisFinding, nothing new to migrate. ──

    [Fact]
    public void Historical_run_with_only_always_persisted_fields_set_explains_correctly()
    {
        var oldRunFinding = new AnalysisFinding
        {
            FindingId = 1,
            AnalysisRunId = 99,
            RequestId = 1,
            Section = FindingSection.Financial,
            Severity = FindingSeverity.Critical,
            TemporalStatus = TemporalStatus.Current,
            Code = "FIN_LEGACY",
            Title = "Legacy finding",
            SummaryText = "From a run predating Explain."
            // No MetricsJson/SupportingSignalsJson/SourceReferenceJson/WhyThisMatters/RecommendedReview —
            // exactly what an old row looks like; Explain must not need any of them.
        };

        var explanation = ReviewPriorityCalculator.Explain([oldRunFinding]);

        Assert.Equal(ReviewPriority.Medium, explanation.Priority);
        Assert.Equal([ReviewPriorityReason.SingleDomainCritical], explanation.Reasons);
    }
}
