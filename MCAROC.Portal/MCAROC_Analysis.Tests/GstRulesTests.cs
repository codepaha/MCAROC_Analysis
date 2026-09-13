using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Tests;

public class GstRulesTests
{
    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Inactive")]
    public void Cancelled_or_inactive_status_without_cancellation_date_triggers_registration_finding(string status)
    {
        var context = AnalysisTestHelpers.BuildContext(gstRegistrations:
        [
            new GstRegistration { Gstin = "29AAAAA0000A1Z5", Status = status },
            new GstRegistration { Gstin = "27AAAAA0000A1Z4", Status = "Active" }
        ]);

        var outcome = Assert.Single(GstRules.Evaluate(context, RuleThresholds.Default),
            x => x.Code == GstRules.RegistrationCancelledCode);

        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FindingSeverity.Watch, outcome.Finding!.Severity);
        Assert.Equal(TemporalStatus.Historical, outcome.Finding.TemporalStatus);
        Assert.Contains("1 of 2 GST registration(s) are cancelled", outcome.Finding.SummaryText);
    }
}
