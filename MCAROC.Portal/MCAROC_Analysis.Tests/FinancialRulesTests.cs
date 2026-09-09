using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>FinancialRules.Evaluate appends outcomes in a fixed, known order: [0] Revenue1Y, [1] Revenue2Y,
/// [2] RevenueGrowth, [3] Ebitda, [4] Pat, [5] NetWorth, [6] Leverage, [7] CurrentRatio, [8] DebtorDays,
/// [9] PayableDays, [10] InterestCoverage, [11-13] CashFlow (CfoNegative, PatCfoDivergence, CfoHealthy).
/// Tests index positionally rather than searching by code, since a NotTriggered outcome carries no code.</summary>
public class FinancialRulesTests
{
    [Fact]
    public void WorkedExample_MaterialOneYearRevenueDecline_TriggersReview()
    {
        // portalplan.md §45's own worked example: FY24 ₹48.62 Cr -> FY25 ₹36.46 Cr, a -25% change.
        var ctx = BuildContext(financialYears: [Fy(2024, revenue: 48.62m), Fy(2025, revenue: 36.46m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[0];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FinancialRules.RevenueDecline1YCode, outcome.Finding!.Code);
        Assert.Equal(FindingSeverity.Review, outcome.Finding.Severity);
        Assert.Equal("Material Revenue Decline", outcome.Finding.Title);
    }

    [Theory]
    [InlineData(14.99, false)] // just under the 15% threshold — not triggered
    [InlineData(15.00, false)] // exactly the threshold — not triggered (boundary is exclusive on the adverse side)
    [InlineData(15.01, true)] // just over — triggered
    public void RevenueDecline1Y_BoundaryBehavior(double declinePercent, bool shouldTrigger)
    {
        var prior = 100m;
        var latest = prior - prior * (decimal)declinePercent / 100m;
        var ctx = BuildContext(financialYears: [Fy(2024, revenue: prior), Fy(2025, revenue: latest)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(shouldTrigger, result[0].Status == RuleEvaluationStatus.Triggered);
    }

    [Fact]
    public void RevenueDecline1Y_NoPriorYear_IsNotEvaluated()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, revenue: 36.46m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, result[0].Status);
    }

    [Fact]
    public void RevenueDecline2Y_TwoConsecutiveSmallDeclines_TriggersAsTrend()
    {
        var ctx = BuildContext(financialYears: [Fy(2023, revenue: 100m), Fy(2024, revenue: 92m), Fy(2025, revenue: 84m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[1];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FinancialRules.RevenueDecline2YCode, outcome.Finding!.Code);
        Assert.Equal(TemporalStatus.Trend, outcome.Finding.TemporalStatus);
        Assert.Equal(FinancialRules.RevenueTrendGroup, outcome.Finding.ConsolidationGroup);
    }

    [Fact]
    public void InterestCoverage_NegativeEbit_ClassifiedBySignNotMagnitude()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, ebit: -50m, financeCost: 2m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[10];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FinancialRules.InterestCoverageCriticalCode, outcome.Finding!.Code);
        Assert.Equal(FindingSeverity.Critical, outcome.Finding.Severity);
        Assert.Contains("insufficient", outcome.Finding.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ratio", outcome.Finding.MetricsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterestCoverage_WeakButPositive_StillCritical()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, ebit: 5m, financeCost: 10m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[10];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FindingSeverity.Critical, outcome.Finding!.Severity);
    }

    [Fact]
    public void InterestCoverage_MissingInputs_IsNotEvaluated()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, ebit: 5m, financeCost: null)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, result[10].Status);
    }

    [Fact]
    public void InterestCoverage_NoFinanceCostBurden_NotTriggered()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, ebit: 5m, financeCost: 0m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[10].Status);
    }

    [Theory]
    [InlineData(0.9, FindingSeverity.Review)] // < 1.0 — weak liquidity
    [InlineData(1.1, FindingSeverity.Watch)] // 1.0-1.2 — modest liquidity
    [InlineData(2.0, FindingSeverity.Positive)] // > 1.5 — strong liquidity
    public void CurrentRatio_ThreeTiers(double ratio, FindingSeverity expectedSeverity)
    {
        var liabilities = 100m;
        var assets = liabilities * (decimal)ratio;
        var ctx = BuildContext(financialYears: [Fy(2025, currentAssets: assets, currentLiabilities: liabilities)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[7];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(expectedSeverity, outcome.Finding!.Severity);
    }

    [Fact]
    public void CurrentRatio_MiddleGround_NotTriggered()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, currentAssets: 135m, currentLiabilities: 100m)]); // ratio 1.35, between 1.2 watch ceiling and 1.5 strong floor

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[7].Status);
    }

    [Fact]
    public void CashFlow_NegativeCfoTwoConsecutiveYears_IsReviewTrend()
    {
        var ctx = BuildContext(financialYears: [Fy(2024, cfo: -5m), Fy(2025, cfo: -3m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        var cfoOutcome = result[11];
        Assert.Equal(RuleEvaluationStatus.Triggered, cfoOutcome.Status);
        Assert.Equal(FindingSeverity.Review, cfoOutcome.Finding!.Severity);
        Assert.Equal(TemporalStatus.Trend, cfoOutcome.Finding.TemporalStatus);
    }

    [Fact]
    public void CashFlow_PatPositiveCfoNegative_TriggersDivergence()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, pat: 10m, cfo: -2m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.Triggered, result[11].Status); // CfoNegative (single year, Watch)
        var divergence = result[12];
        Assert.Equal(RuleEvaluationStatus.Triggered, divergence.Status);
        Assert.Equal(FinancialRules.PatCfoDivergenceCode, divergence.Finding!.Code);
    }

    [Fact]
    public void CashFlow_PatAndCfoBothPositive_IsPositiveFinding()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, pat: 10m, cfo: 8m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[11].Status); // CfoNegative: not triggered
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[12].Status); // PatCfoDivergence: not triggered
        var healthy = result[13];
        Assert.Equal(RuleEvaluationStatus.Triggered, healthy.Status);
        Assert.Equal(FindingSeverity.Positive, healthy.Finding!.Severity);
    }

    [Fact]
    public void NetWorth_Negative_IsCriticalCurrent()
    {
        var ctx = BuildContext(financialYears: [Fy(2025, netWorth: -12m)]);

        var result = FinancialRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[5];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FinancialRules.NetWorthNegativeCode, outcome.Finding!.Code);
        Assert.Equal(FindingSeverity.Critical, outcome.Finding.Severity);
    }
}
