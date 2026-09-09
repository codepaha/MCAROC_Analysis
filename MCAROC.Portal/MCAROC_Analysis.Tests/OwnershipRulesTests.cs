using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

public class OwnershipRulesTests
{
    [Fact]
    public void MaterialPromoterHoldingDecline_TriggersReview()
    {
        var ctx = BuildContext(shareholdings:
        [
            new Shareholding { FinancialYear = 2024, ShareholderNameRaw = "A", ShareholderNameNormalized = "A", IsPromoter = true, HoldingPercentage = 60m },
            new Shareholding { FinancialYear = 2025, ShareholderNameRaw = "A", ShareholderNameNormalized = "A", IsPromoter = true, HoldingPercentage = 45m }
        ]);

        var result = OwnershipRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = Assert.Single(result);
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FindingSeverity.Review, outcome.Finding!.Severity);
        Assert.Equal(TemporalStatus.Trend, outcome.Finding.TemporalStatus);
    }

    [Fact]
    public void DuplicateOverlappingRows_ExceedingHundredPercentTolerance_IsNotEvaluated()
    {
        // Regression test for the review's point: Phase 1 merges "Director Shareholding" + "Shareholding
        // > 5%", which can produce duplicate rows for the same holder/year. Summing them naively could push
        // total promoter holding above 100%, producing a nonsensical trend instead of a data-quality flag.
        var ctx = BuildContext(shareholdings:
        [
            new Shareholding { FinancialYear = 2025, ShareholderNameRaw = "A", ShareholderNameNormalized = "A", IsPromoter = true, HoldingPercentage = 60m },
            new Shareholding { FinancialYear = 2025, ShareholderNameRaw = "B", ShareholderNameNormalized = "B", IsPromoter = true, HoldingPercentage = 55m },
            new Shareholding { FinancialYear = 2024, ShareholderNameRaw = "A", ShareholderNameNormalized = "A", IsPromoter = true, HoldingPercentage = 60m }
        ]);

        var result = OwnershipRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Assert.Single(result).Status);
    }

    [Fact]
    public void SameHolderDuplicateRowsInOneYear_DeduplicatedByMax()
    {
        var ctx = BuildContext(shareholdings:
        [
            // "A" appears twice in FY2025 (from the two merged source sheets) — should count once at the max value, not summed.
            new Shareholding { FinancialYear = 2025, ShareholderNameRaw = "A", ShareholderNameNormalized = "A", IsPromoter = true, HoldingPercentage = 40m },
            new Shareholding { FinancialYear = 2025, ShareholderNameRaw = "A", ShareholderNameNormalized = "A", IsPromoter = true, HoldingPercentage = 42m },
            new Shareholding { FinancialYear = 2024, ShareholderNameRaw = "A", ShareholderNameNormalized = "A", IsPromoter = true, HoldingPercentage = 60m }
        ]);

        var result = OwnershipRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = Assert.Single(result);
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status); // 60% -> 42% is a material decline, not exceeding tolerance
    }

    [Fact]
    public void NoPromoterData_IsNotEvaluated()
    {
        var ctx = BuildContext();

        var result = OwnershipRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Assert.Single(result).Status);
    }
}
