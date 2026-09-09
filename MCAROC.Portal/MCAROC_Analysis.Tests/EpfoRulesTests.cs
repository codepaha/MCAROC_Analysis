using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression tests for the EPFO "behaviour engine" — the two contrasting worked examples from
/// review: a steadily declining/distressed pattern (worsening delays, headcount decline, contracting
/// contributions) that should raise real flags, vs. a stable pattern with one trivial isolated delay that
/// should raise at most a Watch, never a Review-tier or cross-section signal.
///
/// EpfoRules.Evaluate appends outcomes in a fixed order: [0] WorkforceDecline, [1] RepeatedPaymentDelay,
/// [2] DelayWorsening, [3] MissingContribution, [4] PayrollContraction, [5] ErraticCompliancePattern —
/// tests index positionally since a NotTriggered outcome carries no code to search by.</summary>
public class EpfoRulesTests
{
    private static EpfoContribution Row(int monthIndex, int employeeCount, decimal contributionCrore, int delayDays, string workingStatus = "Active")
    {
        var month = new DateOnly(2026, 1, 1).AddMonths(monthIndex - 1);
        var due = new DateOnly(month.Year, month.Month, 15);
        return new EpfoContribution
        {
            EstablishmentId = "EST1", EstablishmentName = "Test Establishment", WorkingStatus = workingStatus,
            WageMonth = month.ToString("MMM-yyyy"), EmployeeCount = employeeCount, ContributionAmountCrore = contributionCrore,
            PaymentDueDate = due, PaymentDate = due.AddDays(delayDays)
        };
    }

    [Fact]
    public void DistressPattern_DecliningHeadcountWorseningDelaysContractingPayroll_RaisesReviewFlags()
    {
        var rows = new List<EpfoContribution>
        {
            Row(1, 120, 1.20m, 0),
            Row(2, 118, 1.18m, 3),
            Row(3, 115, 1.15m, 0),
            Row(4, 109, 1.09m, 12),
            Row(5, 102, 1.02m, 18),
            Row(6, 94, 0.94m, 27),
            Row(7, 86, 0.86m, 32)
        };
        var ctx = BuildContext(epfoContributions: rows);

        var result = EpfoRules.Evaluate(ctx, RuleThresholds.Default);

        var workforce = result[0];
        Assert.Equal(RuleEvaluationStatus.Triggered, workforce.Status);
        Assert.Equal(FindingSeverity.Review, workforce.Finding!.Severity); // ~17.5% decline, in the 10-20% Review tier

        var repeatedDelay = result[1];
        Assert.Equal(RuleEvaluationStatus.Triggered, repeatedDelay.Status);
        Assert.Equal(FindingSeverity.Review, repeatedDelay.Finding!.Severity); // 5 delayed months

        var worsening = result[2];
        Assert.Equal(RuleEvaluationStatus.Triggered, worsening.Status);

        var contraction = result[4];
        Assert.Equal(RuleEvaluationStatus.Triggered, contraction.Status);
        Assert.Equal(FindingSeverity.Review, contraction.Finding!.Severity);
    }

    [Fact]
    public void StablePattern_OneTrivialIsolatedDelay_NeverExceedsWatch()
    {
        var rows = new List<EpfoContribution>
        {
            Row(1, 100, 1.00m, 0),
            Row(2, 103, 1.02m, 1), // one isolated 1-day delay, otherwise stable
            Row(3, 98, 0.99m, 0),
            Row(4, 102, 1.01m, 0),
            Row(5, 99, 1.00m, 0),
            Row(6, 101, 1.00m, 0)
        };
        var ctx = BuildContext(epfoContributions: rows);

        var result = EpfoRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status); // WorkforceDecline
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[2].Status); // DelayWorsening
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[4].Status); // PayrollContraction
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[5].Status); // ErraticCompliancePattern

        var delay = result[1];
        Assert.Equal(RuleEvaluationStatus.Triggered, delay.Status);
        Assert.Equal(FindingSeverity.Watch, delay.Finding!.Severity); // isolated, <= 7 days — Watch only, never Review
    }

    [Fact]
    public void NoParseableWageMonths_AllRulesNotEvaluated()
    {
        var ctx = BuildContext(epfoContributions: [new EpfoContribution { EstablishmentId = "E1", WageMonth = "garbage", EmployeeCount = 10 }]);

        var result = EpfoRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.All(result, o => Assert.Equal(RuleEvaluationStatus.NotEvaluated, o.Status));
    }
}
