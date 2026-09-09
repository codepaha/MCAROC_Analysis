using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;

namespace MCAROC_Analysis.Tests;

public class QuestionHintExtractorTests
{
    [Fact]
    public void DetectsChargeCategoryKeyword()
    {
        var hints = QuestionHintExtractor.Extract("What did the Kotak charge instrument say?", ["KOTAK MAHINDRA BANK"], []);

        Assert.Equal(FilingCategory.Charge, hints.Category);
        Assert.True(hints.HasSoftHints);
    }

    [Fact]
    public void DetectsKnownLenderName()
    {
        var hints = QuestionHintExtractor.Extract("What did Kotak Mahindra Bank say about the security?", ["KOTAK MAHINDRA BANK", "HDFC BANK"], []);

        Assert.Equal("KOTAK MAHINDRA BANK", hints.LenderNameKeyword);
    }

    [Fact]
    public void DetectsFormTypeToken()
    {
        var hints = QuestionHintExtractor.Extract("Summarize the CHG-1 filing.", [], []);

        Assert.Equal("CHG-1", hints.FormTypeKeyword);
    }

    [Fact]
    public void DetectsYear()
    {
        var hints = QuestionHintExtractor.Extract("What was revenue in 2022?", [], []);

        Assert.Equal(2022, hints.Year);
    }

    [Fact]
    public void DetectsKnownSrnAsHardMatch()
    {
        var hints = QuestionHintExtractor.Extract("What is filing 70908 about?", [], ["70908", "70909"]);

        Assert.Equal("70908", hints.SrnMatch);
    }

    [Fact]
    public void NoSignals_ReturnsNoHints()
    {
        var hints = QuestionHintExtractor.Extract("Tell me something interesting.", ["KOTAK MAHINDRA BANK"], ["70908"]);

        Assert.Null(hints.Category);
        Assert.Null(hints.FormTypeKeyword);
        Assert.Null(hints.LenderNameKeyword);
        Assert.Null(hints.SrnMatch);
        Assert.False(hints.HasSoftHints);
    }
}
