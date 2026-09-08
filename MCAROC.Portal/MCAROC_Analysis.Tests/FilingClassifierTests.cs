using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers all three tiers of the classification hierarchy against the real label distribution
/// tallied from the sample corpus (see plan doc), plus the true-unclassified fallback.</summary>
public class FilingClassifierTests
{
    [Theory]
    [InlineData("a8b5851ca387b0b9bc2874914c81fb22v1-Form CHG-1.pdf", FilingCategory.Charge, "Form CHG-1")]
    [InlineData("1fd79b0613b2ffb28c6e235011bc3148v1-Form 8-180314.pdf", FilingCategory.Charge, "Form 8")]
    [InlineData("d3284956c743a031a31e16585e9f09dcv1-Letter of the charge holder stating that the amount has been satisfied.pdf", FilingCategory.Charge, "Charge Satisfaction Letter")]
    [InlineData("xxxv1-Form MGT-14.pdf", FilingCategory.Compliance, "Form MGT-14")]
    [InlineData("xxxv1-Form ADT-1.pdf", FilingCategory.Compliance, "Form ADT-1")]
    [InlineData("xxxv1-MoA - Memorandum of Association-190614.pdf", FilingCategory.Constitutional, "MoA")]
    [InlineData("xxxv1-AoA - Articles of Association.pdf", FilingCategory.Constitutional, "AoA")]
    [InlineData("57684e71010d815a8aaa3b89b7a1f3f1v1-Certificate of Incorporation.pdf", FilingCategory.Constitutional, "Certificate of Incorporation")]
    public void StrongFilenameKeyword_ResolvesHighConfidence(string fileName, FilingCategory expectedCategory, string expectedFormType)
    {
        var result = FilingClassifier.Classify("Charge Documents Financial Documets", "Other Documents Eform", fileName, firstPageText: null);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedFormType, result.FormType);
        Assert.Equal(ClassificationConfidence.High, result.Confidence);
        Assert.Equal("FilenameKeyword", result.Method);
    }

    [Fact]
    public void FinancialXbrlRule_TakesPrecedenceOverGenericForm23Rule()
    {
        // "Form 23AC XBRL-070515..." contains "form 23" AND "23ac" — must resolve to Financial, not
        // Compliance's generic "Form 23" rule (a real bug risk given rule ordering).
        var result = FilingClassifier.Classify("Incorporation and Other Documents", "Other Documents Eform",
            "xxxv1-Form 23AC XBRL-070515-050515 for the FY ending on.pdf", firstPageText: null);

        Assert.Equal(FilingCategory.Financial, result.Category);
    }

    [Fact]
    public void GenericFormAttachment_FallsBackToTextHeaderWhenFilenameIsUninformative()
    {
        // "Optional Attachment-(1)" is the single most common filename in the real corpus (170
        // occurrences) and carries no signal by itself — must be resolved via first-page text instead.
        var result = FilingClassifier.Classify("Charge Documents Financial Documets", "Other Documents Attachment",
            "Optional Attachment-(1).pdf", firstPageText: "This deed evidences the creation or modification of charge over the scheduled property.");

        Assert.Equal(FilingCategory.Charge, result.Category);
        Assert.Equal(ClassificationConfidence.Medium, result.Confidence);
        Assert.Equal("TextHeader", result.Method);
    }

    [Fact]
    public void GenericAttachmentWithNoTextSignal_FallsBackToOuterFolderContext()
    {
        var result = FilingClassifier.Classify("Charge Documents Financial Documets", "Other Documents Attachment",
            "Optional Attachment-(2).pdf", firstPageText: null);

        Assert.Equal(FilingCategory.Charge, result.Category);
        Assert.Equal(ClassificationConfidence.Low, result.Confidence);
        Assert.Equal("OuterFolderFallback", result.Method);
    }

    [Fact]
    public void OuterFolderFallback_DoesNotBlindlyDefaultToFinancialDespiteFolderName()
    {
        // "Charge Documents Financial Documets" [sic] contains the word "Financial" in its own name, but
        // an unresolved file there must still fall back to Charge, not Financial.
        var result = FilingClassifier.Classify("Charge Documents Financial Documets", "Some Folder",
            "unrecognisable-name.pdf", firstPageText: null);

        Assert.Equal(FilingCategory.Charge, result.Category);
    }

    [Fact]
    public void NoSignalAtAll_IsUnclassified()
    {
        var result = FilingClassifier.Classify("Some Unrelated Folder", "Other", "unrecognisable-name.pdf", firstPageText: null);

        Assert.Equal(FilingCategory.Unclassified, result.Category);
        Assert.Equal("NoMatch", result.Method);
    }
}

public class FilingIdentityParserTests
{
    [Fact]
    public void ParsesRealNestedZipFileName()
    {
        var identity = FilingIdentityParser.Parse("70908_COASTAL_PROJECTS_U45203OR1995PLC003982");

        Assert.Equal("70908", identity.Srn);
        Assert.Equal("COASTAL PROJECTS", identity.CompanyName);
        Assert.Equal("U45203OR1995PLC003982", identity.Cin);
    }

    [Fact]
    public void ReturnsNullsForUnrecognizedFormat()
    {
        var identity = FilingIdentityParser.Parse("not-a-recognized-name");

        Assert.Null(identity.Srn);
        Assert.Null(identity.Cin);
    }
}
