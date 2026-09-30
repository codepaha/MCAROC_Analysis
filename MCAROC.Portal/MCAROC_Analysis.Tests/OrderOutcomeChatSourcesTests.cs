using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#195's chat side, without a database or model: how an order-outcome lookup becomes "O" prompt sources,
/// and how a cited O source resolves to a citation that can link to the order PDF.</summary>
public class OrderOutcomeChatSourcesTests
{
    private static OrderOutcomeMatch Match(long docId, decimal? fine = null, bool truncated = false, bool downloaded = true) =>
        new(docId * 10, docId, docId + 1000, 7, "TP 255/2019", null, "NCLT Chennai", "10-02-2022", "Order",
            [LitigationOrderOutcome.FinePenalty], fine, ClassificationConfidence.High, truncated, downloaded);

    private static List<RetrievedSource> Build(OrderOutcomeLookup lookup)
    {
        var sources = new List<RetrievedSource>();
        RetrievalContextBuilder.AddOrderOutcomeSources(sources, [LitigationOrderOutcome.FinePenalty], lookup);
        return sources;
    }

    [Fact]
    public void EachMatch_BecomesAnOSource_LinkedToItsOrderDocument_ThenACoverageNote()
    {
        var sources = Build(new OrderOutcomeLookup([Match(1, fine: 50000m), Match(2)], OrdersWithText: 2, OrdersClassified: 2));

        Assert.Equal(["O1", "O2", "O3"], sources.Select(s => s.Tag));
        Assert.All(sources, s => Assert.Equal(SourceType.OrderOutcome, s.Type));
        Assert.Equal(1, sources[0].DocumentId);
        Assert.Equal(1001, sources[0].LitigationCaseOrderId);
        Assert.Equal(10, sources[0].EntityId);
        Assert.Contains("Rs 50,000.00", sources[0].Text);
        Assert.Contains("TP 255/2019", sources[0].DisplayLabel);

        var coverage = sources[2];
        Assert.Null(coverage.DocumentId);
        Assert.Contains("2 matching order(s)", coverage.Text);
        Assert.Contains("2 of 2", coverage.Text);
        Assert.DoesNotContain("NOT exhaustive", coverage.Text);
    }

    [Fact]
    public void NoMatches_StillProduceACoverageNote_SoAbsenceIsOnlyClaimedOverClassifiedOrders()
    {
        var sources = Build(new OrderOutcomeLookup([], OrdersWithText: 5, OrdersClassified: 3));

        var coverage = Assert.Single(sources);
        Assert.Contains("0 matching order(s)", coverage.Text);
        Assert.Contains("3 of 5", coverage.Text);
        Assert.Contains("NOT exhaustive", coverage.Text);
    }

    [Fact]
    public void TruncatedOrUnretainedOrders_SaySo()
    {
        var text = Build(new OrderOutcomeLookup([Match(1, truncated: true, downloaded: false)], 1, 1))[0].Text;

        Assert.Contains("leading pages only", text);
        Assert.Contains("not currently retained", text);
    }

    [Fact]
    public void MoreMatchesThanTheCap_AreListedUpToTheCap_AndTheNoteSaysSo()
    {
        var matches = Enumerable.Range(1, RetrievalContextBuilder.MaxOrderOutcomeSources + 5).Select(i => Match(i)).ToList();

        var sources = Build(new OrderOutcomeLookup(matches, matches.Count, matches.Count));

        Assert.Equal(RetrievalContextBuilder.MaxOrderOutcomeSources + 1, sources.Count);
        Assert.Contains($"{matches.Count} matching order(s) (only the first {RetrievalContextBuilder.MaxOrderOutcomeSources}", sources[^1].Text);
    }

    [Fact]
    public void CitedOSource_ResolvesToAnOrderOutcomeCitation_CarryingTheOrderDocument()
    {
        var sources = Build(new OrderOutcomeLookup([Match(1, fine: 50000m)], 1, 1));

        var result = ChatCompletionService.Validate("""{ "answer": "One order imposed a fine.", "citedTags": ["O1", "O2"], "insufficientEvidence": false }""", sources);

        Assert.False(result.InsufficientEvidence);
        Assert.Equal(2, result.CitedSources.Count);
        var order = result.CitedSources[0];
        Assert.Equal("OrderOutcome", order.SourceType);
        Assert.Equal(1, order.DocumentId);
        Assert.Equal(1001, order.LitigationCaseOrderId);
        Assert.Equal(nameof(LitigationOrderClassification), order.EntityType);
        Assert.Null(result.CitedSources[1].DocumentId); // the coverage note cites, but links nowhere
    }
}
