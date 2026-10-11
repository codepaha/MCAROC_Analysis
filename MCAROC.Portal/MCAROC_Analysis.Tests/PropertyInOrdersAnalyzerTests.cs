using MCAROC_Analysis.Services.LitigationModule.Analysis;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class PropertyInOrdersAnalyzerTests
{
    [Fact]
    public void AnalyzeOrder_with_cadastral_survey_markers_returns_SurveyOrLandSchedule()
    {
        const string text = "The application is allowed and respondent is restrained from raising any construction over the suit land comprised in khewat No. 62/52, khatauni No. 25/243, khasra No. 1424/1, measuring 1 acre bounded on the north by road.";

        var result = PropertyInOrdersAnalyzer.AnalyzeOrder(text, "order-1", "District Court");

        Assert.Equal(PropertyMentionLevel.SurveyOrLandSchedule, result.PropertyObservation.Level);
        Assert.Equal(ChargeMatchingFeasibility.High, result.PropertyObservation.Feasibility);
        Assert.Contains(result.PropertyObservation.SurveyMarkers, m => m.Contains("khewat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.PropertyObservation.SurveyMarkers, m => m.Contains("khatauni", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.PropertyObservation.SurveyMarkers, m => m.Contains("khasra", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.IsProcedural);
    }

    [Fact]
    public void AnalyzeOrder_with_identifiable_asset_returns_IdentifiableAsset()
    {
        const string text = "The respondent is directed to deliver possession of Flat No. 402 along with hypothecated plant and machinery within 30 days.";

        var result = PropertyInOrdersAnalyzer.AnalyzeOrder(text, "order-2", "Consumer");

        Assert.Equal(PropertyMentionLevel.IdentifiableAsset, result.PropertyObservation.Level);
        Assert.Equal(ChargeMatchingFeasibility.Low, result.PropertyObservation.Feasibility);
        Assert.Empty(result.PropertyObservation.SurveyMarkers);
        Assert.Contains(result.PropertyObservation.MatchedKeywords, kw => kw.Contains("Flat No", StringComparison.OrdinalIgnoreCase) || kw.Contains("plant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzeOrder_with_generic_restraint_returns_GenericRestraint()
    {
        const string text = "The promoters are restrained from dealing with or alienating moveable or immoveable property of the company without leave of court.";

        var result = PropertyInOrdersAnalyzer.AnalyzeOrder(text, "order-3", "High Court");

        Assert.Equal(PropertyMentionLevel.GenericRestraint, result.PropertyObservation.Level);
        Assert.Equal(ChargeMatchingFeasibility.Impossible, result.PropertyObservation.Feasibility);
        Assert.Empty(result.PropertyObservation.SurveyMarkers);
        Assert.Contains("Cannot be matched to individual ROC charges", result.PropertyObservation.Summary);
    }

    [Fact]
    public void AnalyzeOrder_with_procedural_listing_returns_None_and_marks_procedural()
    {
        const string text = "Sri A.V.V Badari narayana filed no objection vakalat for D1, D2. Applicant counsel reported not ready. For arguments posted to 21.6.19.";

        var result = PropertyInOrdersAnalyzer.AnalyzeOrder(text, "order-4", "DRT");

        Assert.Equal(PropertyMentionLevel.None, result.PropertyObservation.Level);
        Assert.Equal(ChargeMatchingFeasibility.Impossible, result.PropertyObservation.Feasibility);
        Assert.True(result.IsProcedural);
        Assert.Empty(result.PropertyObservation.SurveyMarkers);
    }

    [Fact]
    public void GenerateReport_aggregates_corpus_statistics()
    {
        var orders = new (string Identifier, string Text, string? Forum)[]
        {
            ("o1", "khasra No. 1424/1 measuring 2 acres", "District Court"),
            ("o2", "Flat No. B-101 handed over to complainant", "Consumer"),
            ("o3", "restrained from transferring immovable property", "High Court"),
            ("o4", "Matter adjourned to 10.12.2026 for hearing", "DRT"),
            ("o5", "Summons served. Call on 15.01.2027", "DRT")
        };

        var report = PropertyInOrdersAnalyzer.GenerateReport(orders);

        Assert.Equal(5, report.TotalOrdersAnalyzed);
        Assert.Equal(1, report.OrdersWithSurveyOrScheduleCount);
        Assert.Equal(1, report.OrdersWithIdentifiableAssetCount);
        Assert.Equal(1, report.OrdersWithGenericRestraintCount);
        Assert.Equal(2, report.OrdersWithNoPropertyMentionCount);
        Assert.Equal(2, report.ProceduralOrdersCount);
        Assert.Equal(3, report.SubstantiveOrdersCount);
        Assert.Equal(2, report.ForumBreakdown["DRT"]);
        Assert.Equal(1, report.ForumBreakdown["Consumer"]);
    }
}

