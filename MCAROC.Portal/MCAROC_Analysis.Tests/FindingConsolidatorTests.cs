using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Tests;

public class FindingConsolidatorTests
{
    [Fact]
    public void TwoRevenueDeclineDrafts_MergeIntoOneCardUnderTheGroupCode()
    {
        var drafts = new List<FindingDraft>
        {
            new(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                FinancialRules.RevenueDecline1YCode, "Revenue Decline", "1Y summary", ConsolidationGroup: FinancialRules.RevenueTrendGroup),
            new(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Trend,
                FinancialRules.RevenueDecline2YCode, "Sustained Revenue Contraction", "2Y summary", ConsolidationGroup: FinancialRules.RevenueTrendGroup)
        };

        var result = FindingConsolidator.Consolidate(drafts);

        var merged = Assert.Single(result);
        Assert.Equal(FinancialRules.RevenueTrendGroup, merged.Code);
        Assert.Equal("Sustained Revenue Contraction", merged.Title);
        Assert.Contains(FinancialRules.RevenueDecline1YCode, merged.SupportingSignalCodes!);
        Assert.Contains(FinancialRules.RevenueDecline2YCode, merged.SupportingSignalCodes!);
    }

    [Fact]
    public void ProfitabilityAndRevenueGroups_StayAsTwoSeparateCards()
    {
        // Round 2's strongest correction: revenue contraction and profitability deterioration must not be
        // merged into one broad card — only the cross-section engine combines across dimensions.
        var drafts = new List<FindingDraft>
        {
            new(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Trend,
                FinancialRules.RevenueDecline2YCode, "Sustained Revenue Contraction", "revenue", ConsolidationGroup: FinancialRules.RevenueTrendGroup),
            new(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                FinancialRules.EbitdaNegativeCode, "Negative EBITDA", "ebitda", ConsolidationGroup: FinancialRules.ProfitabilityTrendGroup)
        };

        var result = FindingConsolidator.Consolidate(drafts);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.Code == FinancialRules.RevenueTrendGroup);
        Assert.Contains(result, f => f.Code == FinancialRules.ProfitabilityTrendGroup);
    }

    [Fact]
    public void SingleMemberGroup_StillRenamedToGroupCode()
    {
        var drafts = new List<FindingDraft>
        {
            new(FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                FinancialRules.RevenueDecline1YCode, "Revenue Decline", "summary", ConsolidationGroup: FinancialRules.RevenueTrendGroup)
        };

        var result = FindingConsolidator.Consolidate(drafts);

        var only = Assert.Single(result);
        Assert.Equal(FinancialRules.RevenueTrendGroup, only.Code);
        Assert.Equal("Revenue Decline", only.Title); // single-member: original title preserved, not forced to the group's merged-title mapping
    }

    [Fact]
    public void FindingsWithoutConsolidationGroup_PassThroughUnchanged()
    {
        var drafts = new List<FindingDraft>
        {
            new(FindingSection.Charges, FindingSeverity.Review, TemporalStatus.Current, "CHARGE_X", "Title", "summary")
        };

        var result = FindingConsolidator.Consolidate(drafts);

        var only = Assert.Single(result);
        Assert.Equal("CHARGE_X", only.Code);
    }
}
