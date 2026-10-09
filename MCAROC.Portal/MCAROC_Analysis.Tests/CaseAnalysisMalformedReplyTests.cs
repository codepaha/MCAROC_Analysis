using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CaseAnalysisMalformedReplyTests
{
    private static readonly CaseAnalysisInput Input = new(1, "{}", "{}", "hash", [], false);

    [Fact]
    public void A_reply_that_repeats_a_field_is_rejected_not_thrown()
    {
        var result = LitigationCaseAnalysisValidator.Validate("""{"case_key":"1","evidence":[],"evidence":[]}""", Input, LitigationRiskTier.R1, "t");
        Assert.False(result.IsAccepted);
        Assert.True(LitigationCaseAnalysisValidator.IsMalformed(result.RejectReason));
    }

    [Fact]
    public void Unreadable_json_is_malformed_but_a_wrong_case_is_not()
    {
        var bad = LitigationCaseAnalysisValidator.Validate("{not json", Input, LitigationRiskTier.R1, "t");
        Assert.True(LitigationCaseAnalysisValidator.IsMalformed(bad.RejectReason));
        var wrong = LitigationCaseAnalysisValidator.Validate("""{"case_key":"2"}""", Input, LitigationRiskTier.R1, "t");
        Assert.False(LitigationCaseAnalysisValidator.IsMalformed(wrong.RejectReason));
    }
}
