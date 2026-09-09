using MCAROC_Analysis.Services.Chat;

namespace MCAROC_Analysis.Tests;

/// <summary>Tests ChatCompletionService's internal Validate method directly against constructed response
/// JSON — the same boundary Phase 2/3 accepted for their own AI services (no live-call testing, eager
/// credential loading in the constructor).</summary>
public class ChatCompletionServiceValidationTests
{
    private static readonly RetrievedSource FactSource = new("F1", SourceType.StructuredFact, "Revenue FY2025: ₹36.46 Cr", "FY2025 Financials", EntityType: "FinancialYearData", EntityId: 42);
    private static readonly RetrievedSource ChunkSource = new("D1", SourceType.DocumentChunk, "chunk text", "Form CHG-1.pdf · Page 3", RelevanceScore: 0.2, ChunkId: 456, DocumentName: "Form CHG-1.pdf", PageNumber: 3);

    [Fact]
    public void ValidCitation_ResolvesToCitationObject()
    {
        var response = """{ "answer": "Revenue was ₹36.46 Cr.", "citedTags": ["F1"], "insufficientEvidence": false }""";

        var result = ChatCompletionService.Validate(response, [FactSource, ChunkSource]);

        Assert.False(result.InsufficientEvidence);
        var citation = Assert.Single(result.CitedSources);
        Assert.Equal("StructuredFact", citation.SourceType);
        Assert.Equal("FinancialYearData", citation.EntityType);
        Assert.Equal(42, citation.EntityId);
    }

    [Fact]
    public void DocumentChunkCitation_ResolvesWithPageAndDocumentName()
    {
        var response = """{ "answer": "The charge instrument says X.", "citedTags": ["D1"], "insufficientEvidence": false }""";

        var result = ChatCompletionService.Validate(response, [FactSource, ChunkSource]);

        var citation = Assert.Single(result.CitedSources);
        Assert.Equal("DocumentChunk", citation.SourceType);
        Assert.Equal(456, citation.ChunkId);
        Assert.Equal("Form CHG-1.pdf", citation.DocumentName);
        Assert.Equal(3, citation.PageNumber);
    }

    [Fact]
    public void InventedTag_IsDropped_AndConfidentAnswerWithNoValidCitationsIsDowngraded()
    {
        // Round 3's simplified rule: insufficientEvidence == false requires >= 1 VALID citation.
        var response = """{ "answer": "Some confident-sounding answer.", "citedTags": ["Z9"], "insufficientEvidence": false }""";

        var result = ChatCompletionService.Validate(response, [FactSource, ChunkSource]);

        Assert.True(result.InsufficientEvidence);
        Assert.Empty(result.CitedSources);
        Assert.Equal("I could not verify this from the uploaded records.", result.Answer);
    }

    [Fact]
    public void GenuinelyInsufficientCase_PassesThroughWithEmptyCitations()
    {
        var response = """{ "answer": "I could not verify this from the uploaded records.", "citedTags": [], "insufficientEvidence": true }""";

        var result = ChatCompletionService.Validate(response, [FactSource, ChunkSource]);

        Assert.True(result.InsufficientEvidence);
        Assert.Empty(result.CitedSources);
    }

    [Fact]
    public void MultipleValidCitations_AllResolved()
    {
        var response = """{ "answer": "Combined answer.", "citedTags": ["F1", "D1"], "insufficientEvidence": false }""";

        var result = ChatCompletionService.Validate(response, [FactSource, ChunkSource]);

        Assert.Equal(2, result.CitedSources.Count);
    }

    [Fact]
    public void InvalidJson_ReturnsInsufficientEvidence()
    {
        var result = ChatCompletionService.Validate("not json", [FactSource]);

        Assert.True(result.InsufficientEvidence);
    }

    [Fact]
    public void EmptyResponse_ReturnsInsufficientEvidence()
    {
        var result = ChatCompletionService.Validate("", [FactSource]);

        Assert.True(result.InsufficientEvidence);
    }

    [Fact]
    public void DuplicateTagsInResponse_ResolvedOnlyOnce()
    {
        var response = """{ "answer": "x", "citedTags": ["F1", "F1"], "insufficientEvidence": false }""";

        var result = ChatCompletionService.Validate(response, [FactSource]);

        Assert.Single(result.CitedSources);
    }
}
