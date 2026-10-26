using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>LitigationRules.Evaluate reads the litigation service's cases and returns two outcomes once a search has completed:
/// [0] PendingAgainstCompany, [1] RoleUncertain. The company's side is the case page's: its name among the petitioners or the
/// respondents, else the provider's direction.</summary>
public class LitigationRulesTests
{
    private static readonly CompanyProfile Profile = new() { CompanyName = "Test Co" };

    [Fact]
    public void A_pending_case_naming_the_company_as_respondent_triggers_Review_and_cites_the_case()
    {
        var ctx = BuildContext(companyProfile: Profile, litigationCases: [Case(7, "WP 1/2024", "Pending", "XYZ Bank vs Test Co")]);

        var result = LitigationRules.Evaluate(ctx);

        var outcome = result[0];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FindingSeverity.Review, outcome.Finding!.Severity);
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
        Assert.Equal("{\"entityType\":\"LitigationCase\",\"entityIds\":[7]}", outcome.Finding.SourceReferenceJson);
    }

    [Fact]
    public void The_company_as_petitioner_is_not_an_adverse_signal()
    {
        var ctx = BuildContext(companyProfile: Profile, litigationCases: [Case(1, "1", "Pending", "Test Co vs XYZ Bank")]);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    [Fact]
    public void The_providers_direction_decides_when_the_company_is_not_named_among_the_parties()
    {
        var ctx = BuildContext(companyProfile: Profile, litigationCases: [Case(1, "1", "Pending", "XYZ Bank vs Its Directors", direction: "against")]);

        Assert.Equal(RuleEvaluationStatus.Triggered, LitigationRules.Evaluate(ctx)[0].Status);
    }

    [Fact]
    public void A_case_whose_side_is_not_stated_caps_at_Watch()
    {
        var ctx = BuildContext(companyProfile: Profile, litigationCases: [Case(1, "1", "Pending", "Some party vs Another party")]);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
        Assert.Equal(RuleEvaluationStatus.Triggered, result[1].Status);
        Assert.Equal(FindingSeverity.Watch, result[1].Finding!.Severity);
    }

    [Fact]
    public void A_former_name_decides_the_side_too()
    {
        var ctx = BuildContext(companyProfile: Profile, litigationCases: [Case(1, "1", "Pending", "XYZ Bank vs Old Name Pvt Ltd")],
            litigationCompanyNames: ["Test Co", "Old Name Pvt Ltd"]);

        Assert.Equal(RuleEvaluationStatus.Triggered, LitigationRules.Evaluate(ctx)[0].Status);
    }

    [Fact]
    public void A_disposed_case_is_left_out()
    {
        var ctx = BuildContext(companyProfile: Profile, litigationCases: [Case(1, "1", "Disposed", "XYZ Bank vs Test Co")]);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    [Fact]
    public void A_completed_search_with_no_cases_is_a_genuine_nothing_found()
    {
        var result = LitigationRules.Evaluate(BuildContext(companyProfile: Profile, litigationCases: []));

        Assert.All(result, r => Assert.Equal(RuleEvaluationStatus.NotTriggered, r.Status));
    }

    [Fact]
    public void Before_a_search_completes_the_rule_is_not_evaluated()
    {
        var result = LitigationRules.Evaluate(BuildContext(companyProfile: Profile));

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Assert.Single(result).Status);
    }
}
