using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class DocumentLinkDecisionServiceTests
{
    private readonly DocumentLinkDecisionService _decisionService = new();

    [Fact]
    public void Decision_Suppresses_Recreation_When_Same_RuleVersion_And_InputHash()
    {
        // Arrange
        var priorRejection = new DocumentDataLink
        {
            DocumentDataLinkId = 1,
            CanonicalFilingDocumentId = 500,
            IngestionRunId = 10,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 200,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            Status = DocumentDataLinkStatus.Rejected,
            RuleVersion = "1.0.0",
            InputHash = "HASH_ABC_123",
            ReviewReason = "Incorrect charge holder corroboration"
        };

        var candidate = new CandidateEvaluationInput
        {
            CanonicalFilingDocumentId = 500,
            IngestionRunId = 10,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 200,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            RuleVersion = "1.0.0", // Same rule version
            InputHash = "HASH_ABC_123" // Same input hash
        };

        // Act
        var decision = _decisionService.EvaluateCandidate(candidate, [priorRejection]);

        // Assert
        Assert.False(decision.ShouldGenerate);
        Assert.Equal(DocumentDataLinkStatus.Rejected, decision.RecommendedStatus);
        Assert.Contains("previously rejected under rule version '1.0.0'", decision.DecisionReason);
    }

    [Fact]
    public void Decision_Allows_Recreation_When_RuleVersion_Is_Incremented()
    {
        // Arrange
        var priorRejection = new DocumentDataLink
        {
            DocumentDataLinkId = 1,
            CanonicalFilingDocumentId = 500,
            IngestionRunId = 10,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 200,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            Status = DocumentDataLinkStatus.Rejected,
            RuleVersion = "1.0.0",
            InputHash = "HASH_ABC_123",
            ReviewReason = "Reviewer flagged ambiguity"
        };

        var candidate = new CandidateEvaluationInput
        {
            CanonicalFilingDocumentId = 500,
            IngestionRunId = 10,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 200,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            RuleVersion = "1.1.0", // Upgraded rule version
            InputHash = "HASH_ABC_123"
        };

        // Act
        var decision = _decisionService.EvaluateCandidate(candidate, [priorRejection]);

        // Assert
        Assert.True(decision.ShouldGenerate);
        Assert.Equal(DocumentDataLinkStatus.PendingReview, decision.RecommendedStatus);
        Assert.Contains("rule version or input hash updated", decision.DecisionReason);
    }

    [Fact]
    public void Decision_Allows_Recreation_When_InputHash_Changes()
    {
        // Arrange
        var priorRejection = new DocumentDataLink
        {
            DocumentDataLinkId = 1,
            CanonicalFilingDocumentId = 500,
            IngestionRunId = 10,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 200,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            Status = DocumentDataLinkStatus.Rejected,
            RuleVersion = "1.0.0",
            InputHash = "HASH_OLD_OCR",
            ReviewReason = "OCR mistranscribed amount"
        };

        var candidate = new CandidateEvaluationInput
        {
            CanonicalFilingDocumentId = 500,
            IngestionRunId = 10,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 200,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            RuleVersion = "1.0.0",
            InputHash = "HASH_NEW_NATIVE_TEXT" // Altered input hash
        };

        // Act
        var decision = _decisionService.EvaluateCandidate(candidate, [priorRejection]);

        // Assert
        Assert.True(decision.ShouldGenerate);
        Assert.Equal(DocumentDataLinkStatus.PendingReview, decision.RecommendedStatus);
        Assert.Contains("rule version or input hash updated", decision.DecisionReason);
    }
}
