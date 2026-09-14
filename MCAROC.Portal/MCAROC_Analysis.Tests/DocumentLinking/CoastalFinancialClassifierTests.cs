using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class CoastalFinancialClassifierTests
{
    private const string MixedOuterFolder = "Charge Documents Financial Documets";
    private const string PureFinancialOuterFolder = "Financial Documents";
    private const string IncorporationOuterFolder = "Incorporation and Other Documents";

    [Theory]
    [InlineData("9c6b9ffd6fa22d18a630d9394ce4c9d4v1-Form AOC-4(XBRL)-10072018_signed.pdf", "Form AOC-4")]
    [InlineData("2d423197c44eaf5f75e65e09f4be987fv1-XBRL document in respect Consolidated financial statement.pdf", "XBRL Financial Statement")]
    [InlineData("55654ee1041d74675f9802b42fed02b4v1-FormSchV-070212 for the FY ending on-310311.pdf", "Form Sch-V")]
    [InlineData("7b039e6218ee542a822a5392cbfa86cbv1-Form 23AC XBRL-070515-050515 for the FY ending on-310314.pdf", "Form 23AC/23ACA (XBRL)")]
    [InlineData("c536cea3f8f45c02c91869cf22c39679v1-Form 23ACA XBRL-060515-050515 for the FY ending on-310314.pdf", "Form 23AC/23ACA (XBRL)")]
    public void StrongFilenameKeywords_ResolveToFinancial_HighConfidence(string fileName, string expectedFormType)
    {
        var result = CoastalFinancialClassifier.Classify(MixedOuterFolder, "Other", fileName, firstPageText: null);

        Assert.Equal(FilingCategory.Financial, result.Category);
        Assert.Equal(expectedFormType, result.FormType);
        Assert.Equal(ClassificationConfidence.High, result.Confidence);
        Assert.Equal("FilenameKeyword", result.Method);
    }

    [Theory]
    [InlineData("57b3b85d2eef939cf9d276cd174dd28cv1-Approval letter of extension of financial year of AGM.pdf")]
    [InlineData("e40a5f42bb8587007db57788b371e24bv1-Approval letter of extension of financial year of AGM.pdf")]
    public void ExtensionOfFinancialYear_ResolvesToCompliance_NotFinancial(string fileName)
    {
        var result = CoastalFinancialClassifier.Classify(MixedOuterFolder, "Other", fileName, firstPageText: null);

        Assert.Equal(FilingCategory.Compliance, result.Category);
        Assert.Equal("AGM Extension Approval", result.FormType);
    }

    [Theory]
    [InlineData("Optional Attachment-(1).pdf", "COASTAL PROJECTS LIMITED Standalone Balance sheet [Abstract]", FilingCategory.Financial, "Balance Sheet")]
    [InlineData("Statement.pdf", "Statement of Profit and Loss for the year ended", FilingCategory.Financial, "Profit and Loss Statement")]
    [InlineData("Document.pdf", "Profit and loss account for the period", FilingCategory.Financial, "Profit and Loss Statement")]
    [InlineData("Report.pdf", "Standalone Financial Statements for period 01/04/2014 to 31/03/2015", FilingCategory.Financial, "Financial Statement")]
    [InlineData("Report.pdf", "Independent Auditor's Report to the Members", FilingCategory.Financial, "Auditor's Report")]
    [InlineData("Generic.pdf", "This deed evidences the creation or modification of charge", FilingCategory.Charge, "Charge Instrument")]
    public void TextHeaderRules_ResolveWithMediumConfidence(string fileName, string firstPageText, FilingCategory expectedCategory, string expectedFormType)
    {
        var result = CoastalFinancialClassifier.Classify(MixedOuterFolder, "Other", fileName, firstPageText);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedFormType, result.FormType);
        Assert.Equal(ClassificationConfidence.Medium, result.Confidence);
        Assert.Equal("TextHeader", result.Method);
    }

    [Fact]
    public void PureFinancialFolder_FallsBackToFinancial()
    {
        var result = CoastalFinancialClassifier.Classify(PureFinancialOuterFolder, "Other", "unrecognisable-statement.pdf", firstPageText: null);

        Assert.Equal(FilingCategory.Financial, result.Category);
        Assert.Equal(ClassificationConfidence.Low, result.Confidence);
        Assert.Equal("OuterFolderFallback", result.Method);
    }

    [Theory]
    [InlineData("Optional Attachment-(1).pdf", ClassificationConfidence.Low)]
    [InlineData("Optional Attachment-(2).pdf", ClassificationConfidence.Low)]
    [InlineData("1.pdf", ClassificationConfidence.Low)]
    [InlineData("Form 8-180314.pdf", ClassificationConfidence.High)]
    [InlineData("GenericUnrecognised.pdf", ClassificationConfidence.Low)]
    public void MixedOuterFolder_WithoutFinancialSignal_FallsBackToCharge_NeverFinancial(string fileName, ClassificationConfidence expectedConfidence)
    {
        // "Charge Documents Financial Documets" contains the word "Financial", but files without
        // financial filename keyword or text header must NEVER resolve to Financial.
        var result = CoastalFinancialClassifier.Classify(MixedOuterFolder, "Other", fileName, firstPageText: null);

        Assert.NotEqual(FilingCategory.Financial, result.Category);
        Assert.Equal(FilingCategory.Charge, result.Category);
        Assert.Equal(expectedConfidence, result.Confidence);
    }

    [Fact]
    public void IncorporationFolder_FallsBackToConstitutional()
    {
        var result = CoastalFinancialClassifier.Classify(IncorporationOuterFolder, "Other", "unrecognised.pdf", firstPageText: null);

        Assert.Equal(FilingCategory.Constitutional, result.Category);
        Assert.Equal(ClassificationConfidence.Low, result.Confidence);
    }
}
