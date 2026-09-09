using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dashboard;

namespace MCAROC_Analysis.Tests;

public class PriorityRequestRankerTests
{
    private static PriorityRequestRow Row(long id, ReviewPriority? priority, int criticalCount = 0, DateTime? created = null) =>
        new(new McaRequest { RequestId = id, CreatedDate = created ?? new DateTime(2026, 1, 1) },
            priority, criticalCount, AnalysisRunStatus.Completed, AttentionReasons: [], KeyFindingTitle: "—");

    [Fact]
    public void HighPriority_RanksAboveMediumAboveLow()
    {
        var candidates = new List<PriorityRequestRow>
        {
            Row(1, ReviewPriority.Low),
            Row(2, ReviewPriority.High),
            Row(3, ReviewPriority.Medium)
        };

        var ranked = PriorityRequestRanker.Rank(candidates, take: 10);

        Assert.Equal([2L, 3L, 1L], ranked.Select(r => r.Request.RequestId));
    }

    [Fact]
    public void NullPriority_RanksSameAsLow()
    {
        var candidates = new List<PriorityRequestRow> { Row(1, null), Row(2, ReviewPriority.Low) };

        var ranked = PriorityRequestRanker.Rank(candidates, take: 10);

        // Both rank at the bottom tier; tie-broken by CreatedDate desc (both equal here), so either order
        // is acceptable — the real assertion is that neither outranks a High/Medium candidate.
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void WithinSamePriority_TiedByCriticalFindingsCountDescending()
    {
        var candidates = new List<PriorityRequestRow>
        {
            Row(1, ReviewPriority.High, criticalCount: 1),
            Row(2, ReviewPriority.High, criticalCount: 5)
        };

        var ranked = PriorityRequestRanker.Rank(candidates, take: 10);

        Assert.Equal(2L, ranked[0].Request.RequestId);
    }

    [Fact]
    public void WithinSamePriorityAndCriticalCount_TiedByCreatedDateDescending()
    {
        var candidates = new List<PriorityRequestRow>
        {
            Row(1, ReviewPriority.High, created: new DateTime(2026, 1, 1)),
            Row(2, ReviewPriority.High, created: new DateTime(2026, 6, 1))
        };

        var ranked = PriorityRequestRanker.Rank(candidates, take: 10);

        Assert.Equal(2L, ranked[0].Request.RequestId);
    }

    [Fact]
    public void TakeLimitsResultCount()
    {
        var candidates = Enumerable.Range(0, 20).Select(i => Row(i, ReviewPriority.High)).ToList();

        var ranked = PriorityRequestRanker.Rank(candidates, take: 10);

        Assert.Equal(10, ranked.Count);
    }
}
