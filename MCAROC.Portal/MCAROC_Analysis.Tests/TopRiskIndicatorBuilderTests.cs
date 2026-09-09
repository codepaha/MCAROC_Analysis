using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dashboard;

namespace MCAROC_Analysis.Tests;

public class TopRiskIndicatorBuilderTests
{
    private static DashboardFindingRow Finding(
        long requestId, string entityKey, string code, FindingSeverity severity,
        FindingSection section = FindingSection.Financial, TemporalStatus temporal = TemporalStatus.Current) =>
        new(requestId, entityKey, code, $"{code} title", section, severity, temporal, DisplayPriority: 0, ObservationDate: null);

    [Fact]
    public void RanksByDistinctCompaniesAffected_NotRawFindingCount()
    {
        // CODE_A: 5 findings, all in the same company. CODE_B: 2 findings, spread across 2 companies.
        // CODE_B must rank higher — the whole point of "companies affected" over raw hit count.
        var rows = new List<DashboardFindingRow>();
        for (var i = 0; i < 5; i++) rows.Add(Finding(1, "CIN-1", "CODE_A", FindingSeverity.Critical));
        rows.Add(Finding(2, "CIN-2", "CODE_B", FindingSeverity.Critical));
        rows.Add(Finding(3, "CIN-3", "CODE_B", FindingSeverity.Critical));

        var result = TopRiskIndicatorBuilder.Build(rows);

        Assert.Equal("CODE_B", result[0].Code);
        Assert.Equal(2, result[0].CompaniesAffected);
        Assert.Equal("CODE_A", result[1].Code);
        Assert.Equal(1, result[1].CompaniesAffected);
    }

    [Fact]
    public void SameCinAcrossMultipleRequests_CountsAsOneCompany()
    {
        // Round 2's mandatory fix: the same company (same CIN) re-submitted under two different
        // McaRequest ids must count as ONE company affected, not two.
        var rows = new List<DashboardFindingRow>
        {
            Finding(101, "U12345", "CODE_X", FindingSeverity.Critical),
            Finding(118, "U12345", "CODE_X", FindingSeverity.Critical)
        };

        var result = TopRiskIndicatorBuilder.Build(rows);

        var row = Assert.Single(result);
        Assert.Equal(1, row.CompaniesAffected);
        Assert.Equal(2, row.FindingCount);
    }

    [Fact]
    public void PicksHighestSeverityTitleAndSectionPerCode()
    {
        var rows = new List<DashboardFindingRow>
        {
            Finding(1, "CIN-1", "CODE_A", FindingSeverity.Watch, FindingSection.Financial),
            Finding(2, "CIN-2", "CODE_A", FindingSeverity.Critical, FindingSection.CrossSection)
        };

        var result = TopRiskIndicatorBuilder.Build(rows);

        var row = Assert.Single(result);
        Assert.Equal(FindingSeverity.Critical, row.MaxSeverity);
        Assert.Equal(FindingSection.CrossSection, row.Section);
    }

    [Fact]
    public void TakeLimitsResultCount()
    {
        var rows = Enumerable.Range(0, 15).Select(i => Finding(i, $"CIN-{i}", $"CODE_{i}", FindingSeverity.Critical)).ToList();

        var result = TopRiskIndicatorBuilder.Build(rows, take: 10);

        Assert.Equal(10, result.Count);
    }
}
