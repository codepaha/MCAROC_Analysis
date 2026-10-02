using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Epic #195: the order-outcome classifier's prompt and fail-closed validator — no database, no model.
/// Every rejection path here is a way a plausible-looking model answer could otherwise put an unsupported outcome
/// into an "every order with a fine" list.</summary>
public class LitigationOrderClassifierTests
{
    private static LitigationOrderChunk Chunk(int page, int index, string text) => new()
    {
        LitigationOrderChunkId = page * 100 + index, RequestId = 1, LitigationOrderDocumentId = 77, LitigationCaseOrderId = 55,
        LitigationCaseId = 33, CaseNumber = "TP 255/2019", Court = "NCLT Chennai", OrderDate = "10-02-2022", OrderType = "Order",
        PageNumber = page, ChunkIndex = index, ChunkText = text
    };

    private static LitigationOrderEvidence Evidence() => LitigationOrderClassifier.BuildEvidence(
    [
        Chunk(2, 1, "The respondent shall pay costs of Rs. 50,000 within four weeks."),
        Chunk(1, 0, "The interim stay granted earlier is hereby vacated.")
    ]);

    [Fact]
    public void Evidence_IsOrderedByPageAndCarriesTheOrderIdentity()
    {
        var evidence = Evidence();

        Assert.Equal([(1, 0), (2, 1)], evidence.Excerpts.Select(e => (e.PageNumber, e.ChunkIndex)));
        Assert.Equal(77, evidence.LitigationOrderDocumentId);
        Assert.Equal(55, evidence.LitigationCaseOrderId);
        Assert.Equal(33, evidence.LitigationCaseId);
        Assert.False(evidence.Truncated);
    }

    [Fact]
    public void Evidence_OverTheBound_IsFlaggedTruncated()
    {
        var chunks = Enumerable.Range(0, LitigationOrderClassifier.MaxChunksPerOrder + 1).Select(i => Chunk(i + 1, i, "text")).ToList();

        var evidence = LitigationOrderClassifier.BuildEvidence(chunks);

        Assert.True(evidence.Truncated);
        Assert.Equal(LitigationOrderClassifier.MaxChunksPerOrder, evidence.Excerpts.Count);
    }

    [Fact]
    public void Prompt_ListsTheTaxonomy_DistinguishesNegation_AndEmbedsTheExcerpts()
    {
        var prompt = LitigationOrderClassifier.BuildPrompt(Evidence());

        Assert.StartsWith(LitigationOrderClassifier.PromptMarker, prompt);
        foreach (var name in Enum.GetNames<LitigationOrderOutcome>()) Assert.Contains(name, prompt);
        Assert.Contains("Never label this StayGranted", prompt);
        Assert.Contains("The interim stay granted earlier is hereby vacated.", prompt);
    }

    [Fact]
    public void Validate_AcceptsCitedOutcomes_AndExtractsTheFine()
    {
        var result = LitigationOrderClassifier.Validate("""
            {"status":"Completed","confidence":"High","outcomes":[
              {"type":"StayVacated","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]},
              {"type":"FinePenalty","fineAmount":50000,"evidenceReferences":[{"pageNumber":2,"chunkIndex":1}]}]}
            """, Evidence());

        Assert.True(result.IsAccepted);
        Assert.Equal(LitigationAiAnalysisItemStatus.Completed, result.Status);
        Assert.Equal([LitigationOrderOutcome.StayVacated, LitigationOrderOutcome.FinePenalty], result.Outcomes);
        Assert.Equal(50000m, result.FineAmount);
        Assert.Equal(ClassificationConfidence.High, result.Confidence);
        Assert.Contains("\"StayVacated\"", result.ClassificationJson);
    }

    [Fact]
    public void Validate_AcceptsInsufficientEvidence_WithNoOutcomes()
    {
        var result = LitigationOrderClassifier.Validate("""{"status":"InsufficientEvidence","outcomes":[]}""", Evidence());

        Assert.True(result.IsAccepted);
        Assert.Equal(LitigationAiAnalysisItemStatus.InsufficientEvidence, result.Status);
        Assert.Empty(result.Outcomes);
    }

    [Theory]
    [InlineData("not json", "Invalid JSON")]
    [InlineData("""{"status":"Maybe","outcomes":[]}""", "status")]
    [InlineData("""{"status":"Completed","outcomes":[]}""", "no outcomes")]
    [InlineData("""{"status":"InsufficientEvidence","outcomes":[{"type":"Dismissal","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}""", "also claimed")]
    [InlineData("""{"status":"Completed","outcomes":[{"type":"Acquittal","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}""", "Unsupported outcome type")]
    [InlineData("""{"status":"Completed","outcomes":[{"type":"3","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}""", "Unsupported outcome type")]
    [InlineData("""{"status":"Completed","outcomes":[{"type":"Dismissal","evidenceReferences":[]}]}""", "no evidence references")]
    [InlineData("""{"status":"Completed","outcomes":[{"type":"Dismissal","evidenceReferences":[{"pageNumber":9,"chunkIndex":0}]}]}""", "not present in the prompt")]
    [InlineData("""{"status":"Completed","outcomes":[{"type":"Dismissal","fineAmount":10,"evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}""", "non-fine")]
    [InlineData("""{"status":"Completed","outcomes":[{"type":"FinePenalty","fineAmount":-5,"evidenceReferences":[{"pageNumber":2,"chunkIndex":1}]}]}""", "positive")]
    [InlineData("""{"status":"Completed","confidence":"Certain","outcomes":[{"type":"Dismissal","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}""", "confidence")]
    [InlineData("""{"status":"Completed","outcomes":[{"type":"Dismissal","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]},{"type":"dismissal","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}""", "twice")]
    public void Validate_RejectsUnsupportedOrUncitedOutput(string raw, string expectedReason)
    {
        var result = LitigationOrderClassifier.Validate(raw, Evidence());

        Assert.False(result.IsAccepted);
        Assert.Equal(LitigationAiAnalysisItemStatus.Failed, result.Status);
        Assert.Contains(expectedReason, result.RejectReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Outcomes);
    }

    // ── #355: long judgements send their decision passages, not just their first 24 chunks ──

    private const string Recital = "The learned counsel for the petitioner submitted the background of the matter and referred to the record.";

    /// <summary>A long judgement of <paramref name="count"/> one-chunk pages, all recital except the given pages.</summary>
    private static List<LitigationOrderChunk> LongOrder(int count, params (int Page, string Text)[] decisions) =>
        Enumerable.Range(1, count)
            .Select(p => Chunk(p, 0, decisions.FirstOrDefault(d => d.Page == p).Text ?? Recital))
            .ToList();

    [Fact]
    public void Evidence_ForAnOrderWithinTheBound_IsUnchanged_AndItsPromptHasNoSelectionNote()
    {
        var chunks = LongOrder(LitigationOrderClassifier.MaxChunksPerOrder, (24, "The petition is dismissed with costs."));

        var evidence = LitigationOrderClassifier.BuildEvidence(chunks);

        Assert.Equal(Enumerable.Range(1, 24), evidence.Excerpts.Select(e => e.PageNumber));
        Assert.Equal(0, evidence.OmittedExcerpts);
        Assert.False(evidence.Truncated);
        Assert.DoesNotContain("This is a long order", LitigationOrderClassifier.BuildPrompt(evidence));
    }

    [Fact]
    public void Evidence_ForALongJudgement_KeepsBothEnds_AndTheDecisionPassageWherever_ItIs()
    {
        // The #337 fixture's long judgements decide on pages 3, 8, 10, 13, 82 and 144 — so a decision deep in the middle
        // (page 144 of 200) and one on the last page must both reach the model.
        var chunks = LongOrder(200,
            (144, "In the result, the application is allowed and the respondent is restrained from raising any construction."),
            (200, "Accordingly, the suit is decreed. No order as to costs."));

        var evidence = LitigationOrderClassifier.BuildEvidence(chunks);
        var pages = evidence.Excerpts.Select(e => e.PageNumber).ToList();

        Assert.Equal(LitigationOrderClassifier.MaxChunksPerOrder, pages.Count);
        Assert.Equal(pages.Order(), pages); // still in page order
        Assert.Contains(144, pages);
        Assert.Equal([1, 2], pages.Take(2));
        Assert.Equal([199, 200], pages.TakeLast(2));
        Assert.True(evidence.Truncated);
        Assert.Equal(200 - 24, evidence.OmittedExcerpts);

        var prompt = LitigationOrderClassifier.BuildPrompt(evidence);
        Assert.Contains("This is a long order", prompt);
        Assert.Contains("176 other excerpts were left out", prompt);
    }

    [Fact]
    public void Validate_AcceptsACitationOfASelectedMidJudgementPassage()
    {
        var evidence = LitigationOrderClassifier.BuildEvidence(LongOrder(60, (37, "The appeal is dismissed and the appellant shall pay costs of Rs. 5,000.")));

        var result = LitigationOrderClassifier.Validate(
            """{"status":"Completed","confidence":"High","outcomes":[{"type":"Dismissal","evidenceReferences":[{"pageNumber":37,"chunkIndex":0}]}]}""",
            evidence);

        Assert.Equal(LitigationAiAnalysisItemStatus.Completed, result.Status);
        Assert.Equal([LitigationOrderOutcome.Dismissal], result.Outcomes);
    }

    [Fact]
    public void DecisionCueScore_CountsDecisionLanguage_AndNotRecitals()
    {
        Assert.Equal(0, LitigationOrderClassifier.DecisionCueScore(Recital));
        Assert.True(LitigationOrderClassifier.DecisionCueScore("The plaint is rejected. The plaintiffs shall pay costs of Rs. 5,000. Draw decree accordingly.") >= 4);
    }

    [Fact]
    public void OutcomeTypes_RoundTrip_AndUnknownNamesAreSkipped()
    {
        var json = LitigationOrderClassifier.SerializeOutcomeTypes([LitigationOrderOutcome.FinePenalty, LitigationOrderOutcome.Injunction]);

        Assert.Equal([LitigationOrderOutcome.FinePenalty, LitigationOrderOutcome.Injunction], LitigationOrderClassifier.ParseOutcomeTypes(json));
        Assert.Equal([LitigationOrderOutcome.Dismissal], LitigationOrderClassifier.ParseOutcomeTypes("""["FutureOutcome","Dismissal"]"""));
        Assert.Empty(LitigationOrderClassifier.ParseOutcomeTypes("garbage"));
    }
}
