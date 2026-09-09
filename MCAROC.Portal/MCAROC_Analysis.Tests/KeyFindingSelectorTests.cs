using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dashboard;

namespace MCAROC_Analysis.Tests;

public class KeyFindingSelectorTests
{
    private static DashboardFindingRow Finding(
        string code, FindingSeverity severity, FindingSection section, TemporalStatus temporal = TemporalStatus.Current,
        int displayPriority = 0, DateOnly? observationDate = null) =>
        new(RequestId: 1, EntityKey: "CIN-1", code, $"{code} title", section, severity, temporal, displayPriority, observationDate);

    [Fact]
    public void CriticalCrossSection_OutranksEverythingElse()
    {
        var findings = new List<DashboardFindingRow>
        {
            Finding("REVIEW_DOMAIN", FindingSeverity.Review, FindingSection.Financial),
            Finding("CRITICAL_DOMAIN", FindingSeverity.Critical, FindingSection.Financial),
            Finding("REVIEW_CROSS", FindingSeverity.Review, FindingSection.CrossSection),
            Finding("CRITICAL_CROSS", FindingSeverity.Critical, FindingSection.CrossSection)
        };

        var selected = KeyFindingSelector.Select(findings);

        Assert.Equal("CRITICAL_CROSS", selected!.Code);
    }

    [Fact]
    public void CriticalDomain_OutranksReviewCrossSection()
    {
        var findings = new List<DashboardFindingRow>
        {
            Finding("REVIEW_CROSS", FindingSeverity.Review, FindingSection.CrossSection),
            Finding("CRITICAL_DOMAIN", FindingSeverity.Critical, FindingSection.Financial)
        };

        var selected = KeyFindingSelector.Select(findings);

        Assert.Equal("CRITICAL_DOMAIN", selected!.Code);
    }

    [Fact]
    public void ReviewCrossSection_OutranksReviewDomain()
    {
        var findings = new List<DashboardFindingRow>
        {
            Finding("REVIEW_DOMAIN", FindingSeverity.Review, FindingSection.Financial),
            Finding("REVIEW_CROSS", FindingSeverity.Review, FindingSection.CrossSection)
        };

        var selected = KeyFindingSelector.Select(findings);

        Assert.Equal("REVIEW_CROSS", selected!.Code);
    }

    [Fact]
    public void HistoricalCritical_IsNeverSelected_EvenWhenItsTheOnlyCriticalPresent()
    {
        var findings = new List<DashboardFindingRow>
        {
            Finding("HISTORICAL_CRITICAL", FindingSeverity.Critical, FindingSection.Financial, TemporalStatus.Historical),
            Finding("CURRENT_REVIEW", FindingSeverity.Review, FindingSection.Financial)
        };

        var selected = KeyFindingSelector.Select(findings);

        Assert.Equal("CURRENT_REVIEW", selected!.Code);
    }

    [Fact]
    public void NoCurrentCriticalOrReview_ReturnsNull()
    {
        var findings = new List<DashboardFindingRow>
        {
            Finding("WATCH_ONLY", FindingSeverity.Watch, FindingSection.Financial),
            Finding("HISTORICAL_CRITICAL", FindingSeverity.Critical, FindingSection.Financial, TemporalStatus.Historical)
        };

        var selected = KeyFindingSelector.Select(findings);

        Assert.Null(selected);
    }

    [Fact]
    public void TiesWithinTheSameTier_BrokenByDisplayPriorityThenObservationDate()
    {
        var findings = new List<DashboardFindingRow>
        {
            Finding("LOWER_PRIORITY", FindingSeverity.Critical, FindingSection.Financial, displayPriority: 1, observationDate: new DateOnly(2026, 1, 1)),
            Finding("HIGHER_PRIORITY", FindingSeverity.Critical, FindingSection.Financial, displayPriority: 5, observationDate: new DateOnly(2025, 1, 1))
        };

        var selected = KeyFindingSelector.Select(findings);

        Assert.Equal("HIGHER_PRIORITY", selected!.Code);
    }

    [Fact]
    public void TiesWithSameDisplayPriority_BrokenByNewestObservationDate()
    {
        var findings = new List<DashboardFindingRow>
        {
            Finding("OLDER", FindingSeverity.Critical, FindingSection.Financial, displayPriority: 1, observationDate: new DateOnly(2025, 1, 1)),
            Finding("NEWER", FindingSeverity.Critical, FindingSection.Financial, displayPriority: 1, observationDate: new DateOnly(2026, 1, 1))
        };

        var selected = KeyFindingSelector.Select(findings);

        Assert.Equal("NEWER", selected!.Code);
    }
}
