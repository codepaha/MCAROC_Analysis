using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

public class AuditorRulesTests
{
    [Theory]
    [InlineData("No fraud has been reported by the auditor during the year.")]
    [InlineData("No material uncertainty relating to going concern exists as of the balance sheet date.")]
    public void NegationLanguage_NeverProducesAnAdverseFinding(string observationText)
    {
        // Regression test: naive keyword matching would see "fraud"/"going concern" as substrings and
        // misclassify these explicitly negated, clean statements as Critical.
        var ctx = BuildContext(auditorObservations:
        [
            new AuditorObservation { FinancialYear = 2025, HasQualificationOrAdverseRemark = true, ObservationText = observationText }
        ]);

        var result = AuditorRules.Evaluate(ctx);

        var outcome = Assert.Single(result);
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FindingSeverity.Positive, outcome.Finding!.Severity);
        Assert.Equal(AuditorRules.CleanOpinionCode, outcome.Finding.Code);
    }

    [Fact]
    public void NoQualificationFlag_IsCleanOpinion()
    {
        var ctx = BuildContext(auditorObservations:
        [
            new AuditorObservation { FinancialYear = 2025, HasQualificationOrAdverseRemark = false }
        ]);

        var result = AuditorRules.Evaluate(ctx);

        Assert.Equal(FindingSeverity.Positive, Assert.Single(result).Finding!.Severity);
    }

    [Fact]
    public void AdverseOpinionKeyword_IsCritical()
    {
        var ctx = BuildContext(auditorObservations:
        [
            new AuditorObservation { FinancialYear = 2025, HasQualificationOrAdverseRemark = true, ObservationText = "The auditor issued an adverse opinion on the financial statements." }
        ]);

        var result = AuditorRules.Evaluate(ctx);

        var outcome = Assert.Single(result);
        Assert.Equal(FindingSeverity.Critical, outcome.Finding!.Severity);
        Assert.Equal(AuditorRules.AdverseOpinionCode, outcome.Finding.Code);
    }

    [Fact]
    public void QualifiedOpinionKeyword_IsReview()
    {
        var ctx = BuildContext(auditorObservations:
        [
            new AuditorObservation { FinancialYear = 2025, HasQualificationOrAdverseRemark = true, ObservationText = "The auditor issued a qualified opinion regarding inventory valuation." }
        ]);

        var result = AuditorRules.Evaluate(ctx);

        var outcome = Assert.Single(result);
        Assert.Equal(FindingSeverity.Review, outcome.Finding!.Severity);
    }

    [Fact]
    public void UnrecognizedAdverseText_DefaultsToWatch()
    {
        var ctx = BuildContext(auditorObservations:
        [
            new AuditorObservation { FinancialYear = 2025, HasQualificationOrAdverseRemark = true, ObservationText = "Some minor formatting observation." }
        ]);

        var result = AuditorRules.Evaluate(ctx);

        Assert.Equal(FindingSeverity.Watch, Assert.Single(result).Finding!.Severity);
    }

    [Fact]
    public void NoObservationsAvailable_IsNotEvaluated()
    {
        var ctx = BuildContext();

        var result = AuditorRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Assert.Single(result).Status);
    }
}
