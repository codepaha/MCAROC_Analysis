using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class DocumentLinkManifestBypassTests
{
    private readonly DocumentLinkCanonicalizationService _canonicalizationService = new();

    [Fact]
    public void Manifest_Duplicate_Documents_Bypass_Linking_Hierarchy()
    {
        // Arrange: A document detected as a duplicate at manifest time has DuplicateOfDocumentId populated.
        var canonicalDoc = new McaFilingDocument
        {
            FilingDocumentId = 10,
            OriginalFileName = "CHG_Form.pdf",
            DuplicateOfDocumentId = null
        };

        var duplicateDoc = new McaFilingDocument
        {
            FilingDocumentId = 11,
            OriginalFileName = "CHG_Form_Copy.pdf",
            DuplicateOfDocumentId = 10
        };

        // Act & Assert
        Assert.True(_canonicalizationService.ShouldProcessForLinking(canonicalDoc));
        Assert.False(_canonicalizationService.ShouldProcessForLinking(duplicateDoc));
    }
}
