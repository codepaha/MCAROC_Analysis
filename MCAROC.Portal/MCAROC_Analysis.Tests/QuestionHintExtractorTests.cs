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

    [Theory]
    [InlineData("How many open charges are there?", true, true)]
    [InlineData("What is the revenue growth over three years?", true, false)]
    [InlineData("What percentage of shares do promoters hold?", true, false)]
    [InlineData("Count the cases mentioning fraud.", true, true)]
    [InlineData("What is the number of cases mentioning fraud?", true, true)]
    [InlineData("What is the total number of cases mentioning fraud?", true, true)]
    [InlineData("What is the total revenue?", true, false)]
    [InlineData("List all the lenders", false, true)]
    [InlineData("Which ones of the cases are in NCLT? Give every case.", false, true)]
    [InlineData("Who is the auditor?", false, false)]
    [InlineData("Summarize the CHG-1 filing.", false, false)]
    public void DetectsQuantityAndCompleteListQuestions(string question, bool quantity, bool completeList)
    {
        var hints = QuestionHintExtractor.Extract(question, [], []);

        Assert.Equal(quantity, hints.AsksForQuantity);
        Assert.Equal(completeList, hints.AsksForCompleteList);
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
        Assert.Empty(hints.LexicalTerms!);
    }

    [Theory]
    [InlineData("What is the status of TP 255/2019?", "TP 255/2019")]
    [InlineData("Summarize WP/11227/2019 for me", "WP/11227/2019")]
    [InlineData("What happened in CP(IB) No. 123/2020?", "CP(IB) No. 123/2020")]
    [InlineData("Was IA No. 45 of 2021 allowed?", "IA No. 45 of 2021")]
    [InlineData("Explain O.S. No. 12 of 2018", "O.S. No. 12 of 2018")]
    public void CaseNumbers_BecomeLexicalTerms(string question, string expected)
    {
        var hints = QuestionHintExtractor.Extract(question, [], []);

        Assert.Contains(expected, hints.LexicalTerms!);
    }

    [Theory]
    [InlineData("Which orders cite Section 7 of the IBC?", "Section 7")]
    [InlineData("Anything under section 13(2) of SARFAESI?", "section 13(2)")]
    [InlineData("Is Sec. 29A relevant here?", "Sec. 29A")]
    public void SectionReferences_BecomeLexicalTerms(string question, string expected)
    {
        var hints = QuestionHintExtractor.Extract(question, [], []);

        Assert.Contains(expected, hints.LexicalTerms!);
    }

    [Fact]
    public void QuotedPhrases_BecomeLexicalTerms_StraightOrCurlyQuotes()
    {
        var hints = QuestionHintExtractor.Extract("Where does it say \"stay is vacated\" or \u201Cpossession of the property\u201D?", [], []);

        Assert.Equal(["stay is vacated", "possession of the property"], hints.LexicalTerms!);
    }

    [Fact]
    public void OrdinaryQuestions_YieldNoLexicalTerms()
    {
        // Years, plain words and lower-case prose must not be mistaken for case numbers or sections.
        var hints = QuestionHintExtractor.Extract("What was revenue in 2022 and how did the second charge in 2019 change?", [], []);

        Assert.Empty(hints.LexicalTerms!);
    }

    [Fact]
    public void LexicalTerms_AreDeduplicatedAndCapped()
    {
        var hints = QuestionHintExtractor.Extract(
            "TP 1/2019, TP 1/2019, TP 2/2019, TP 3/2019, TP 4/2019, TP 5/2019, TP 6/2019", [], []);

        Assert.Equal(QuestionHintExtractor.MaxLexicalTerms, hints.LexicalTerms!.Count);
        Assert.Equal(hints.LexicalTerms.Count, hints.LexicalTerms.Distinct().Count());
    }

    [Theory]
    [InlineData("Pull every order with a fine", LitigationOrderOutcome.FinePenalty)]
    [InlineData("Which NCLT cases imposed a penalty?", LitigationOrderOutcome.FinePenalty)]
    [InlineData("List orders directing possession of the property", LitigationOrderOutcome.PossessionOrder)]
    [InlineData("Any injunction against the company?", LitigationOrderOutcome.Injunction)]
    [InlineData("Which petitions were dismissed?", LitigationOrderOutcome.Dismissal)]
    [InlineData("Which cases were settled?", LitigationOrderOutcome.DisposedSettled)]
    [InlineData("Was any interim relief granted?", LitigationOrderOutcome.InterimRelief)]
    [InlineData("How many hearings were adjourned?", LitigationOrderOutcome.AdjournedNoSubstantiveOrder)]
    [InlineData("Is there a stay on the proceedings?", LitigationOrderOutcome.StayGranted)]
    public void OrderOutcomeQuestions_AreRecognised(string question, LitigationOrderOutcome expected)
    {
        var hints = QuestionHintExtractor.Extract(question, [], []);

        Assert.Contains(expected, hints.OrderOutcomes!);
    }

    [Theory]
    [InlineData("Show orders where the stay was vacated")]
    [InlineData("Was the stay lifted by the High Court?")]
    [InlineData("Which orders set aside the stay?")]
    public void StayReversal_MapsToStayVacated_NeverStayGranted(string question)
    {
        var hints = QuestionHintExtractor.Extract(question, [], []);

        Assert.Contains(LitigationOrderOutcome.StayVacated, hints.OrderOutcomes!);
        Assert.DoesNotContain(LitigationOrderOutcome.StayGranted, hints.OrderOutcomes!);
    }

    [Theory]
    [InlineData("Is the company's financial position fine?")]
    [InlineData("Who holds possession of the hypothecated stock?")]
    [InlineData("Was the loan settled in 2021?")]
    [InlineData("What was revenue in 2022?")]
    public void OrdinaryWords_WithoutALegalContext_AreNotOrderOutcomes(string question)
    {
        var hints = QuestionHintExtractor.Extract(question, [], []);

        Assert.Empty(hints.OrderOutcomes!);
    }

    [Fact]
    public void SeveralOutcomes_InOneQuestion_AreAllRecognised()
    {
        var hints = QuestionHintExtractor.Extract("List the orders with a fine or an injunction", [], []);

        Assert.Equal([LitigationOrderOutcome.FinePenalty, LitigationOrderOutcome.Injunction], hints.OrderOutcomes!);
    }
}
