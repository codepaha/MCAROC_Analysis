using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationAnalysisPromptBuilderTests
{
    [Fact]
    public void BuildCaseEvidence_IsBoundedOrderedAndKeepsExactAddresses()
    {
        var @case = new LitigationCase { LitigationCaseId = 41, Cnr = "CNR-1", Court = "Court" };
        var chunks = Enumerable.Range(0, 15).Select(i => new LitigationOrderChunk
        {
            LitigationCaseId = 41, LitigationCaseOrderId = 1, LitigationOrderDocumentId = 2,
            PageNumber = 1, ChunkIndex = 14 - i, ChunkText = new string('x', 3500)
        });
        var evidence = LitigationAnalysisPromptBuilder.BuildCaseEvidence(@case, chunks);
        Assert.Equal(12, evidence.Excerpts.Count);
        Assert.Equal(0, evidence.Excerpts[0].ChunkIndex);
        Assert.Equal(3000, evidence.Excerpts[0].Text.Length);
        Assert.Contains("litigationCaseOrderId", LitigationAnalysisPromptBuilder.BuildCasePrompt(evidence));
    }

    [Fact]
    public void Validator_RejectsCitationOutsidePersistedEvidence()
    {
        var evidence = new LitigationAnalysisEvidence(1, null, null, null, null, null,
            [new LitigationEvidenceExcerpt(2, 3, 4, 5, "order text")]);
        var result = LitigationAnalysisResponseValidator.ValidateCase("""
            {"status":"Completed","summary":"Claim","unknowns":[],"evidenceReferences":[{"litigationCaseOrderId":99,"litigationOrderDocumentId":3,"pageNumber":4,"chunkIndex":5}]}
            """, evidence);
        Assert.False(result.IsAccepted);
        Assert.Contains("not present", result.RejectReason);
    }

    [Fact]
    public void Validator_AllowsInsufficientEvidenceWithoutOrderText()
    {
        var evidence = new LitigationAnalysisEvidence(1, null, null, null, null, null, []);
        var result = LitigationAnalysisResponseValidator.ValidateCase("""
            {"status":"InsufficientEvidence","summary":"No order text","unknowns":["unknown"],"evidenceReferences":[]}
            """, evidence);
        Assert.True(result.IsAccepted);
    }
}
