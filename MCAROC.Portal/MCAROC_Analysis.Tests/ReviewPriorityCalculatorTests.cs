using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Tests;

public class ReviewPriorityCalculatorTests
{
    private static FindingDraft Draft(FindingSection section, FindingSeverity severity, TemporalStatus temporal, string code) =>
        new(section, severity, temporal, code, code, code);

    [Fact]
    public void OneHistoricalCriticalPlusManyPositives_StaysLow()
    {
        // The review's own worked example: one historical GST cancellation + 20 positive findings should
        // not become a High review priority.
        var findings = new List<FindingDraft> { Draft(FindingSection.Gst, FindingSeverity.Critical, TemporalStatus.Historical, "GST_HIST") };
        findings.AddRange(Enumerable.Range(0, 20).Select(i => Draft(FindingSection.Financial, FindingSeverity.Positive, TemporalStatus.Current, $"POS_{i}")));

        var priority = ReviewPriorityCalculator.Calculate(findings);

        Assert.Equal(ReviewPriority.Low, priority);
    }

    [Fact]
    public void TwoCurrentCriticalAcrossTwoDomains_IsHigh()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_X"),
            Draft(FindingSection.CompanyProfile, FindingSeverity.Critical, TemporalStatus.Current, "CORP_X")
        };

        Assert.Equal(ReviewPriority.High, ReviewPriorityCalculator.Calculate(findings));
    }

    [Fact]
    public void TwoCurrentCriticalInOneDomainOnly_IsNotHighOnCountAlone()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_X"),
            Draft(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_Y")
        };

        Assert.Equal(ReviewPriority.Medium, ReviewPriorityCalculator.Calculate(findings));
    }

    [Fact]
    public void DesignatedCriticalEventCode_IsHighRegardlessOfDomainCount()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, FinancialRules.InterestCoverageCriticalCode)
        };

        Assert.Equal(ReviewPriority.High, ReviewPriorityCalculator.Calculate(findings));
    }

    [Fact]
    public void CrossSectionCritical_IsHigh()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.CrossSection, FindingSeverity.Critical, TemporalStatus.Current, CrossSectionRules.FinancialStressCode)
        };

        Assert.Equal(ReviewPriority.High, ReviewPriorityCalculator.Calculate(findings));
    }

    [Fact]
    public void OneCurrentCritical_NotDesignated_IsMedium()
    {
        var findings = new List<FindingDraft> { Draft(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current, "FIN_SOMETHING_ELSE") };

        Assert.Equal(ReviewPriority.Medium, ReviewPriorityCalculator.Calculate(findings));
    }

    [Fact]
    public void TwoCurrentReviewFindings_IsMedium()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current, "FIN_X"),
            Draft(FindingSection.Msme, FindingSeverity.Review, TemporalStatus.Current, "MSME_X")
        };

        Assert.Equal(ReviewPriority.Medium, ReviewPriorityCalculator.Calculate(findings));
    }

    [Fact]
    public void TrendCriticalCountsTowardEscalation()
    {
        // Round 2 correction: a serious Trend (e.g. 3 consecutive years negative EBITDA) is as meaningful
        // as a point-in-time Current state, so it now counts toward the "2+ Critical" High threshold too.
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Trend, "FIN_X"),
            Draft(FindingSection.CompanyProfile, FindingSeverity.Critical, TemporalStatus.Current, "CORP_X")
        };

        Assert.Equal(ReviewPriority.High, ReviewPriorityCalculator.Calculate(findings));
    }

    [Fact]
    public void WatchAndPositiveOnly_IsLow()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Watch, TemporalStatus.Current, "FIN_X"),
            Draft(FindingSection.Financial, FindingSeverity.Positive, TemporalStatus.Current, "FIN_Y")
        };

        Assert.Equal(ReviewPriority.Low, ReviewPriorityCalculator.Calculate(findings));
    }
}
