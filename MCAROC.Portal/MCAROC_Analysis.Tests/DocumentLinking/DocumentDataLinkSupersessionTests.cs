using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class DocumentDataLinkSupersessionTests
{
    private readonly DocumentLinkCanonicalizationService _canonicalizationService = new();

    [Fact]
    public void CanonicalizeLateDuplicate_Creates_Supersession_And_Preserves_EvidenceSnapshot()
    {
        // Arrange
        long requestId = 42;
        long ingestionRunId = 7;
        long canonicalDocId = 100;
        long duplicateDocId = 101;

        var survivingLink = new DocumentDataLink
        {
            DocumentDataLinkId = 1,
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            FilingDocumentId = canonicalDocId,
            CanonicalFilingDocumentId = canonicalDocId,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 555,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            Status = DocumentDataLinkStatus.Confirmed,
            EvidenceJson = "{\"ChargeId\":\"101\",\"Source\":\"CanonicalDoc\"}",
            RuleVersion = "1.0.0"
        };

        var collidingDuplicateLink = new DocumentDataLink
        {
            DocumentDataLinkId = 2,
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            FilingDocumentId = duplicateDocId,
            CanonicalFilingDocumentId = duplicateDocId,
            TargetEntityType = "RocChargeEvent",
            TargetEntityId = 555,
            TargetField = null,
            LinkKind = DocumentLinkKind.Supports,
            Status = DocumentDataLinkStatus.AutoAccepted,
            EvidenceJson = "{\"ChargeId\":\"101\",\"Source\":\"DuplicateDoc\"}",
            RuleVersion = "1.0.0",
            ReviewReason = "Initial auto-link"
        };

        var allLinks = new List<DocumentDataLink> { survivingLink, collidingDuplicateLink };

        // Act
        var result = _canonicalizationService.CanonicalizeLateDuplicate(
            duplicateDocumentId: duplicateDocId,
            canonicalDocumentId: canonicalDocId,
            requestId: requestId,
            ingestionRunId: ingestionRunId,
            actor: "System.Canonicalization",
            reason: "Late duplicate discovery via SHA-256 batch recheck",
            allLinks: allLinks);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Single(result.CreatedSupersessions);

        var supersession = result.CreatedSupersessions[0];
        Assert.Equal(collidingDuplicateLink.DocumentDataLinkId, supersession.SupersededLinkId);
        Assert.Equal(survivingLink.DocumentDataLinkId, supersession.SurvivingLinkId);
        Assert.NotEqual(supersession.SupersededLinkId, supersession.SurvivingLinkId); // Contract check: SupersededLinkId <> SurvivingLinkId
        Assert.Equal(requestId, supersession.RequestId);
        Assert.Equal(ingestionRunId, supersession.IngestionRunId);
        Assert.Equal("System.Canonicalization", supersession.Actor);
        Assert.Contains("Late duplicate discovery", supersession.Reason);

        // Prove evidence preservation
        Assert.Contains("DuplicateDoc", supersession.EvidencePreservationJson);
        Assert.Contains("Initial auto-link", supersession.EvidencePreservationJson);

        // Prove duplicate link status transitioned to SupersededByDuplicate
        Assert.Equal(DocumentDataLinkStatus.SupersededByDuplicate, collidingDuplicateLink.Status);
        Assert.Equal(canonicalDocId, collidingDuplicateLink.CanonicalFilingDocumentId);

        // Prove surviving link remains untouched and Confirmed
        Assert.Equal(DocumentDataLinkStatus.Confirmed, survivingLink.Status);

        // Prove referential navigation
        Assert.Contains(supersession, collidingDuplicateLink.SupersededByLinks);
        Assert.Contains(supersession, survivingLink.SupersedesLinks);
    }

    [Fact]
    public void CanonicalizeLateDuplicate_Fails_When_Duplicate_Equals_Canonical()
    {
        // Act
        var result = _canonicalizationService.CanonicalizeLateDuplicate(
            duplicateDocumentId: 100,
            canonicalDocumentId: 100,
            requestId: 1,
            ingestionRunId: 1,
            actor: "User",
            reason: "Self link",
            allLinks: []);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("cannot be identical", result.ErrorMessage);
    }
}
