using MCAROC_Analysis.Services.Chat;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Pure-function coverage for #194's Reciprocal Rank Fusion and full-text query construction — no
/// database needed. The real-SQL behavior is covered by HybridRetrievalTests.</summary>
public class HybridSearchTests
{
    [Fact]
    public void Fusion_WithNoLexicalHits_IsExactlyTheSemanticRanking()
    {
        var fused = HybridSearch.ReciprocalRankFusion<long>([5, 3, 9], [], take: 8);

        Assert.Equal([5, 3, 9], fused);
    }

    [Fact]
    public void Fusion_RewardsAgreementBetweenRankers()
    {
        // 9 is only third semantically but first lexically — agreement (1/63 + 1/61) beats 5's lone 1/61.
        var fused = HybridSearch.ReciprocalRankFusion<long>([5, 3, 9], [9], take: 8);

        Assert.Equal(9, fused[0]);
    }

    [Fact]
    public void Fusion_AddsLexicalOnlyHits_AndTrimsToTake()
    {
        var fused = HybridSearch.ReciprocalRankFusion<long>([1, 2, 3], [42], take: 3);

        Assert.Equal(3, fused.Count);
        Assert.Contains(42, fused);
        // Ties on score (semantic #1 and lexical #1 both 1/61) keep the semantic hit first.
        Assert.Equal([1, 42, 2], fused);
    }

    [Fact]
    public void Fusion_NeverDuplicatesAKeyFoundByBothRankers()
    {
        var fused = HybridSearch.ReciprocalRankFusion<long>([1, 2], [2, 1], take: 8);

        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void ContainsQuery_QuotesEveryTermAsAPhrase_AndOrsThem()
    {
        Assert.Equal("\"TP 255/2019\" OR \"Section 7\"",
            HybridSearch.BuildContainsQuery(["TP 255/2019", "Section 7"]));
    }

    [Fact]
    public void ContainsQuery_StripsFullTextOperatorsFromUserText()
    {
        // A stray quote or wildcard in the question must not break out of the phrase or become a prefix search.
        Assert.Equal("\"stay granted\" OR \"loan\"",
            HybridSearch.BuildContainsQuery(["stay \" granted", "loan*"]));
    }

    [Fact]
    public void ContainsQuery_IsNullWhenThereIsNothingSearchable()
    {
        Assert.Null(HybridSearch.BuildContainsQuery(null));
        Assert.Null(HybridSearch.BuildContainsQuery([]));
        Assert.Null(HybridSearch.BuildContainsQuery(["\"", " * "]));
    }

    [Fact]
    public void ContainsQuery_DeduplicatesCaseInsensitively()
    {
        Assert.Equal("\"Section 7\"", HybridSearch.BuildContainsQuery(["Section 7", "section 7"]));
    }
}
