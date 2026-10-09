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
    public void A_disposed_case_against_the_company_is_read_in_full_because_the_judgment_is_in_it()
    {
        var disposed = Rate(caseType: "Civil suit", status: LitigationCaseStatusBucket.Disposed);
        Assert.Equal(LitigationAnalysisPriority.A, disposed.Priority);
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
}
