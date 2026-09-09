using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Tests;

public class CrossSectionRulesTests
{
    private static FindingDraft Draft(FindingSection section, FindingSeverity severity, string code, TemporalStatus temporal = TemporalStatus.Current) =>
        new(section, severity, temporal, code, code, code);

    [Fact]
    public void FinancialStress_RequiresAllThreeComponents()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Review, FinancialRules.RevenueTrendGroup),
            Draft(FindingSection.Financial, FindingSeverity.Review, FinancialRules.ProfitabilityTrendGroup),
            Draft(FindingSection.Financial, FindingSeverity.Critical, FinancialRules.InterestCoverageCriticalCode)
        };

        var result = CrossSectionRules.Evaluate(findings);

        var stress = Assert.Single(result, f => f.Code == CrossSectionRules.FinancialStressCode);
        Assert.Equal(FindingSeverity.Critical, stress.Severity);
    }

    [Fact]
    public void FinancialStress_DoesNotFireWithOnlyTwoOfThree()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Review, FinancialRules.RevenueTrendGroup),
            Draft(FindingSection.Financial, FindingSeverity.Review, FinancialRules.ProfitabilityTrendGroup)
        };

        var result = CrossSectionRules.Evaluate(findings);

        Assert.DoesNotContain(result, f => f.Code == CrossSectionRules.FinancialStressCode);
    }

    [Fact]
    public void HistoricalComponentFindings_AreExcludedFromCombination()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Financial, FindingSeverity.Review, FinancialRules.RevenueTrendGroup, TemporalStatus.Historical),
            Draft(FindingSection.Financial, FindingSeverity.Review, FinancialRules.ProfitabilityTrendGroup),
            Draft(FindingSection.Financial, FindingSeverity.Critical, FinancialRules.InterestCoverageCriticalCode)
        };

        var result = CrossSectionRules.Evaluate(findings);

        Assert.DoesNotContain(result, f => f.Code == CrossSectionRules.FinancialStressCode);
    }

    [Fact]
    public void LiquidityPayrollStress_ThreeOfFourGroups_IsReview_AllFourIsCritical()
    {
        var threeGroups = new List<FindingDraft>
        {
            Draft(FindingSection.Epfo, FindingSeverity.Review, EpfoRules.RepeatedPaymentDelayCode),
            Draft(FindingSection.Epfo, FindingSeverity.Review, EpfoRules.WorkforceDeclineCode),
            Draft(FindingSection.Msme, FindingSeverity.Review, MsmeRules.MsmeDueMaterialCode)
        };
        var threeResult = CrossSectionRules.Evaluate(threeGroups);
        var threeStress = Assert.Single(threeResult, f => f.Code == CrossSectionRules.LiquidityPayrollStressCode);
        Assert.Equal(FindingSeverity.Review, threeStress.Severity);

        var fourGroups = new List<FindingDraft>(threeGroups)
        {
            Draft(FindingSection.Financial, FindingSeverity.Watch, FinancialRules.CfoNegativeCode)
        };
        var fourResult = CrossSectionRules.Evaluate(fourGroups);
        var fourStress = Assert.Single(fourResult, f => f.Code == CrossSectionRules.LiquidityPayrollStressCode);
        Assert.Equal(FindingSeverity.Critical, fourStress.Severity);
    }

    [Fact]
    public void LiquidityPayrollStress_OnlyTwoGroups_DoesNotFire()
    {
        var findings = new List<FindingDraft>
        {
            Draft(FindingSection.Epfo, FindingSeverity.Review, EpfoRules.RepeatedPaymentDelayCode),
            Draft(FindingSection.Epfo, FindingSeverity.Review, EpfoRules.WorkforceDeclineCode)
        };

        var result = CrossSectionRules.Evaluate(findings);

        Assert.DoesNotContain(result, f => f.Code == CrossSectionRules.LiquidityPayrollStressCode);
    }
}
