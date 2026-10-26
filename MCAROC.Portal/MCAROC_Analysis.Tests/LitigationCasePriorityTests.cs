using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationCasePriorityTests
{
    private static LitigationCasePriorityResult Rate(string? act = null, string? caseType = null, string? court = null, string? type = null,
        LitigationCompanySide side = LitigationCompanySide.AgainstCompany, LitigationCaseStatusBucket status = LitigationCaseStatusBucket.Pending) =>
        LitigationCasePriority.Evaluate(new LitigationRiskInput(type, court, null, caseType, act, null, null, null, status, side, []));

    [Fact]
    public void An_insolvency_case_is_always_A_and_outranks_everything_whichever_side_filed()
    {
        var byBank = Rate(act: "Insolvency and Bankruptcy Code, 2016", type: "NCLT", side: LitigationCompanySide.ByCompany);
        var against = Rate(act: "Insolvency and Bankruptcy Code, 2016", type: "NCLT");
        var worstOther = Rate(act: "SARFAESI Act", type: "DRT", caseType: "criminal cheating", status: LitigationCaseStatusBucket.Disposed);
        Assert.Equal(LitigationAnalysisPriority.A, byBank.Priority);
        Assert.True(byBank.Score > worstOther.Score);
        Assert.True(against.Score > byBank.Score);
    }

    [Fact]
    public void The_debt_recovery_act_name_is_not_mistaken_for_insolvency()
    {
        var drt = Rate(act: "Recovery of Debts and Bankruptcy Act, 1993", type: "DRT", side: LitigationCompanySide.ByCompany);
        Assert.DoesNotContain("insolvency", drt.Reasons);
        Assert.True(drt.Score < 100);
    }

    [Fact]
    public void A_case_filed_by_the_company_under_no_financial_act_is_lowest_priority()
    {
        var civil = Rate(caseType: "Civil suit", side: LitigationCompanySide.ByCompany);
        Assert.Equal(LitigationAnalysisPriority.C, civil.Priority);
    }

    [Fact]
    public void A_financial_act_lifts_a_company_filing_above_its_other_suits_but_not_to_A()
    {
        var plain = Rate(caseType: "Civil suit", side: LitigationCompanySide.ByCompany, status: LitigationCaseStatusBucket.Disposed);
        var sarfaesi = Rate(act: "SARFAESI Act 2002", type: "DRT", side: LitigationCompanySide.ByCompany, status: LitigationCaseStatusBucket.Disposed);
        Assert.True(sarfaesi.Score > plain.Score);
        Assert.Equal(LitigationAnalysisPriority.B, sarfaesi.Priority);
    }

    [Fact]
    public void A_disposed_case_against_the_company_in_a_superior_forum_is_read_in_full_because_the_judgment_weighs_most()
    {
        Assert.Equal(LitigationAnalysisPriority.A, Rate(type: "highcourt", caseType: "Civil suit", status: LitigationCaseStatusBucket.Disposed).Priority);
        Assert.Equal(LitigationAnalysisPriority.A, Rate(type: "drt", status: LitigationCaseStatusBucket.Disposed).Priority);
    }

    [Fact]
    public void A_plain_civil_case_against_the_company_is_tier_B_even_when_disposed_and_so_is_a_pending_High_Court_matter()
    {
        Assert.Equal(LitigationAnalysisPriority.B, Rate(caseType: "Civil suit", status: LitigationCaseStatusBucket.Disposed).Priority);
        Assert.Equal(LitigationAnalysisPriority.B, Rate(type: "highcourt", caseType: "Civil suit").Priority);
    }

    [Fact]
    public void A_pending_case_with_a_financial_act_or_a_fraud_charge_is_tier_A()
    {
        Assert.Equal(LitigationAnalysisPriority.A, Rate(act: "Arbitration and Conciliation Act", type: "highcourt").Priority);
        Assert.Equal(LitigationAnalysisPriority.A, Rate(caseType: "Criminal complaint - cheating", type: "district").Priority);
    }

    [Fact]
    public void A_case_the_company_filed_is_never_above_B_whatever_the_act()
    {
        var best = Rate(act: "SARFAESI Act", type: "drt", side: LitigationCompanySide.ByCompany, status: LitigationCaseStatusBucket.Disposed);
        Assert.Equal(LitigationAnalysisPriority.B, best.Priority);
    }

    [Theory]
    [InlineData("Negotiable Instruments Act, 1881 - S.138")]
    [InlineData("PMLA 2002")]
    [InlineData("FEMA 1999")]
    [InlineData("Prevention of Corruption Act")]
    [InlineData("Banking Regulation Act, 1949")]
    public void Financial_acts_against_the_company_are_A(string act) =>
        Assert.Equal(LitigationAnalysisPriority.A, Rate(act: act).Priority);

    [Fact]
    public void A_short_act_name_does_not_match_inside_another_word() =>
        Assert.Equal(0, LitigationCasePriority.ActWeight("calcibcentre gstreet", out _));

    [Theory]
    [InlineData("criminal", null, null)]                           // a pending criminal case against the company
    [InlineData(null, "ITAT", null)]                               // a pending tax tribunal matter
    [InlineData(null, "CESTAT", null)]
    [InlineData(null, null, "Arbitration and Conciliation Act")]
    [InlineData(null, null, "Companies Act, 2013 - oppression and mismanagement")]
    [InlineData(null, null, "SEBI Act")]
    [InlineData(null, null, "Consumer Protection Act - deficiency in service")]
    [InlineData(null, null, "Information Technology Act")]
    [InlineData(null, null, "Income Tax Act")]
    public void Every_category_the_rule_names_reaches_tier_A_when_pending_and_against_the_company(string? caseType, string? type, string? act) =>
        Assert.Equal(LitigationAnalysisPriority.A, Rate(caseType: caseType, type: type, act: act).Priority);

    [Fact]
    public void A_pending_criminal_case_and_a_tax_tribunal_matter_score_at_least_the_threshold()
    {
        Assert.True(Rate(caseType: "criminal").Score >= LitigationCasePriority.AThreshold);
        Assert.True(Rate(type: "ITAT").Score >= LitigationCasePriority.AThreshold);
    }

    [Theory]
    [InlineData(MCAROC_Analysis.Data.Entities.PersonAttributionGrade.Possible)]
    [InlineData(MCAROC_Analysis.Data.Entities.PersonAttributionGrade.Excluded)]
    public void Possible_or_excluded_attribution_grade_evaluates_to_lowest_priority_C(MCAROC_Analysis.Data.Entities.PersonAttributionGrade grade)
    {
        var input = new LitigationRiskInput(
            Type: "NCLT",
            Court: "NCLT",
            CourtCategory: null,
            CaseType: null,
            Act: "Insolvency and Bankruptcy Code, 2016",
            CaseStage: null,
            CaseClassification: null,
            ProceedingType: null,
            Status: LitigationCaseStatusBucket.Pending,
            Side: LitigationCompanySide.AgainstCompany,
            Petitioners: [],
            AttributionGrade: grade);

        var result = LitigationCasePriority.Evaluate(input);
        Assert.Equal(LitigationAnalysisPriority.C, result.Priority);
        Assert.Equal(0, result.Score);
        Assert.Contains("Unconfirmed case", result.Reasons);
    }

}
