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

    /// <summary>Regression for a real bug found in review: G18 added detail-table rows
    /// (IsDetailRow = true) to AuditorObservations. A same-year detail row with a null ObservationText
    /// and the default HasQualificationOrAdverseRemark=false must never be picked over the year-summary
    /// row (IsDetailRow = false) — doing so would silently downgrade a qualified/adverse opinion to
    /// "Clean" just because the detail row happened to come first in enumeration order.</summary>
    [Fact]
    public void DetailRowForTheSameYearNeverOverridesTheYearSummaryOpinion()
    {
        var ctx = BuildContext(auditorObservations:
        [
            // A G18 detail-table row for the same year, carrying no opinion of its own (as real
            // Directors' Comments/Footnotes-only detail rows now can).
            new AuditorObservation
            {
                FinancialYear = 2025, IsDetailRow = true, ObservationText = null,
                DirectorsComments = "Board noted the delay", HasQualificationOrAdverseRemark = false
            },
            // The year-summary row (table 1) — the actual qualified opinion for FY2025.
            new AuditorObservation
            {
                FinancialYear = 2025, IsDetailRow = false, HasQualificationOrAdverseRemark = true,
                ObservationText = "The auditor issued an adverse opinion on the financial statements."
            }
        ]);

        var result = AuditorRules.Evaluate(ctx);

        var outcome = Assert.Single(result);
        Assert.Equal(FindingSeverity.Critical, outcome.Finding!.Severity);
        Assert.Equal(AuditorRules.AdverseOpinionCode, outcome.Finding.Code);
        Assert.NotEqual(AuditorRules.CleanOpinionCode, outcome.Finding.Code);
    }

    [Fact]
    public void OnlyDetailRowsAvailable_IsNotEvaluated()
    {
        // No year-summary row exists at all (e.g. a company whose detail table has rows but whose
        // year-summary table is missing/blank) — must fail closed to NotEvaluated, never fall back to
        // treating a detail row as the opinion.
        var ctx = BuildContext(auditorObservations:
        [
            new AuditorObservation { FinancialYear = 2025, IsDetailRow = true, DirectorsComments = "Some note" }
        ]);

        var result = AuditorRules.Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Assert.Single(result).Status);
    }
}
