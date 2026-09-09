using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>LitigationRules.Evaluate returns two outcomes for pending cases: [0] PendingAgainstCompany,
/// [1] RoleUncertain.</summary>
public class LitigationRulesTests
{
    private static readonly CompanyProfile Profile = new() { CompanyName = "Test Co" };

    [Fact]
    public void ProbableMatch_NeverEscalatesBeyondWatch()
    {
        var ctx = BuildContext(companyProfile: Profile, litigations:
        [
            new Litigation { CaseStatus = "Pending", CaseNumber = "1", Litigants = "XYZ Bank vs Test Co", MatchStatus = LitigationMatchStatus.Probable }
        ]);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status); // never Critical/Review-tier "against company" for a probable match
        var uncertain = result[1];
        Assert.Equal(RuleEvaluationStatus.Triggered, uncertain.Status);
        Assert.Equal(FindingSeverity.Watch, uncertain.Finding!.Severity);
    }

    [Fact]
    public void ConfirmedAgainstCompany_TriggersReview()
    {
        var ctx = BuildContext(companyProfile: Profile, litigations:
        [
            new Litigation { CaseStatus = "Pending", CaseNumber = "1", Litigants = "XYZ Bank vs Test Co", MatchStatus = LitigationMatchStatus.Confirmed }
        ]);

        var result = LitigationRules.Evaluate(ctx);

        var outcome = result[0];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FindingSeverity.Review, outcome.Finding!.Severity);
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
        // Evidence link names the exact Litigation row so the Litigation tab can attribute a per-case role.
        Assert.Contains("\"entityType\":\"Litigation\"", outcome.Finding!.SourceReferenceJson);
        Assert.Contains("\"entityIds\":[", outcome.Finding!.SourceReferenceJson);
    }

    [Fact]
    public void ConfirmedByCompany_IsNotAnAdverseSignal()
    {
        var ctx = BuildContext(companyProfile: Profile, litigations:
        [
            new Litigation { CaseStatus = "Pending", CaseNumber = "1", Litigants = "Test Co vs XYZ Bank", MatchStatus = LitigationMatchStatus.Confirmed }
        ]);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    [Fact]
    public void ConfirmedAmbiguousRole_CapsAtWatchNotReview()
    {
        var ctx = BuildContext(companyProfile: Profile, litigations:
        [
            new Litigation { CaseStatus = "Pending", CaseNumber = "1", Litigants = "Some unrelated free-text description", MatchStatus = LitigationMatchStatus.Confirmed }
        ]);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
        var uncertain = result[1];
        Assert.Equal(RuleEvaluationStatus.Triggered, uncertain.Status);
        Assert.Equal(FindingSeverity.Watch, uncertain.Finding!.Severity);
    }

    [Fact]
    public void ClosedCase_ExcludedEntirely()
    {
        var ctx = BuildContext(companyProfile: Profile, litigations:
        [
            new Litigation { CaseStatus = "Disposed", CaseNumber = "1", Litigants = "XYZ Bank vs Test Co", MatchStatus = LitigationMatchStatus.Confirmed }
        ]);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[0].Status);
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status);
    }

    [Fact]
    public void NoLitigationRecords_IsNotEvaluated()
    {
        var ctx = BuildContext(companyProfile: Profile);

        var result = LitigationRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Assert.Single(result).Status);
    }
}
