using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationJudgmentFilterTests
{
    [Theory]
    [InlineData("List on 29.08.2017. (ASHWANI KUMAR) COURT MASTER", false)]
    [InlineData("Heard in part. The matters be listed on 22nd August, 2017.", false)]
    [InlineData("Adjourned to 12 March for filing of replies.", false)]
    [InlineData("Upon hearing counsel, the petition is dismissed for non-prosecution.", true)]
    [InlineData("The appeal is allowed and the order of the tribunal is set aside.", true)]
    [InlineData("Heard counsel. The respondent shall pay costs of Rs. 50,000 within four weeks.", true)]
    [InlineData("In the result, the suit is decreed with costs.", true)]
    [InlineData("The special leave petitions stand disposed of.", true)]
    public void Operative_text_decides_whether_an_order_is_a_judgment(string tail, bool expected) =>
        Assert.Equal(expected, LitigationJudgmentFilter.IsJudgment("order/judgement", "ITEM NO.4 COURT NO.11", tail, newestOrderOfDisposedCase: false));

    [Fact]
    public void An_order_that_opens_as_a_judgment_or_final_order_is_one()
    {
        Assert.True(LitigationJudgmentFilter.IsJudgment("order", "IN THE HIGH COURT. JUDGMENT. Date of decision: 4.3.2020", "", false));
        Assert.True(LitigationJudgmentFilter.IsJudgment("order", "FINAL ORDER in Company Petition 12/2020", "", false));
    }

    [Fact]
    public void An_interim_or_daily_order_is_not_a_judgment_unless_its_text_disposes_of_something()
    {
        Assert.False(LitigationJudgmentFilter.IsJudgment("daily_order", "JUDGMENT cited below. Present: counsel", "Next date 5.5.2022", false));
        Assert.False(LitigationJudgmentFilter.IsJudgment("interm_order", "Interim order. Notice issued", "List after two weeks", false));
        Assert.True(LitigationJudgmentFilter.IsJudgment("interm_order", "Interim order", "The application stands dismissed.", false));
    }

    [Fact]
    public void The_newest_order_of_a_disposed_case_is_a_judgment_even_when_its_text_says_little()
    {
        Assert.True(LitigationJudgmentFilter.IsJudgment("order", "Present: counsel", "Next date 5.5.2022", newestOrderOfDisposedCase: true));
    }

    [Fact]
    public void The_source_label_at_the_start_of_a_chunk_is_not_the_orders_own_words()
    {
        const string labelled = "[Court: SupremeCourt | Case: SLP(C) 6459/2019 | CNR: X | Order: order/judgement 12-11-2018 | Page 1]  List on 29.08.2017.";
        Assert.StartsWith("List on", LitigationJudgmentFilter.Body(labelled).TrimStart());
        // the label names every order "order/judgement"; that must not make an adjournment look like a judgment
        Assert.False(LitigationJudgmentFilter.IsJudgment("order/judgement", LitigationJudgmentFilter.Body(labelled), LitigationJudgmentFilter.Body(labelled), false));
    }

    private static LitigationOrderChunk Chunk(long doc, long order, long caseId, string date, string text) => new()
    {
        LitigationOrderDocumentId = doc, LitigationCaseOrderId = order, LitigationCaseId = caseId, OrderDate = date, OrderType = "order/judgement",
        PageNumber = 1, ChunkIndex = 0, ChunkText = text
    };

    [Fact]
    public void Only_the_newest_order_of_a_disposed_case_is_kept_among_its_adjournments()
    {
        var chunks = new[]
        {
            Chunk(1, 10, 100, "05-01-2022", "Adjourned. List on 5 February."),
            Chunk(2, 11, 100, "05-02-2022", "Adjourned. List on 5 March."),
            Chunk(3, 12, 100, "05-03-2022", "Matter taken up. No one appears."),       // newest, case disposed -> kept
            Chunk(4, 13, 200, "05-03-2022", "Adjourned. List on 5 April."),            // newest of a pending case -> skipped
        };
        var status = new Dictionary<long, LitigationCaseStatusBucket>
            { [100] = LitigationCaseStatusBucket.Disposed, [200] = LitigationCaseStatusBucket.Pending };

        var kept = LitigationJudgmentFilter.JudgmentDocumentIds(chunks, status);

        Assert.Equal([3L], kept.OrderBy(x => x));
    }

    [Fact]
    public void Dates_in_the_sources_formats_order_correctly()
    {
        var chunks = new[]
        {
            Chunk(1, 10, 100, "28-11-2019", "Adjourned."),
            Chunk(2, 11, 100, "03-02-2020", "Listed."),     // 3 Feb 2020 is later than 28 Nov 2019 even though "03" < "28" as text
        };
        var kept = LitigationJudgmentFilter.JudgmentDocumentIds(chunks, new Dictionary<long, LitigationCaseStatusBucket> { [100] = LitigationCaseStatusBucket.Disposed });
        Assert.Equal([2L], kept.OrderBy(x => x));
    }

    private static LitigationOrderChunk Page(long doc, long order, long caseId, int page, int chunk, string text) => new()
    {
        LitigationOrderDocumentId = doc, LitigationCaseOrderId = order, LitigationCaseId = caseId, OrderDate = "05-03-2022", OrderType = "order/judgement",
        PageNumber = page, ChunkIndex = chunk, ChunkText = "[Court: HighCourt | Case: 1/2020 | Order: order/judgement 05-03-2022 | Page " + page + "]  " + text
    };

    [Fact]
    public void A_dismissal_on_the_page_before_the_signature_page_is_found_in_a_pending_case_that_is_not_disposed()
    {
        // production shape: one chunk per page, the operative part on page 2 of 3 and only signatures on the last page
        var chunks = new[]
        {
            Page(1, 10, 100, 1, 0, "IN THE HIGH COURT. Present: counsel for the petitioner. Arguments heard at length."),
            Page(1, 10, 100, 2, 0, "For the reasons above the writ petition is dismissed. No order as to costs."),
            Page(1, 10, 100, 3, 0, "Signed by Registrar. Digitally signed. Date: 05.03.2022. Reason: authenticated."),
        };
        var pending = new Dictionary<long, LitigationCaseStatusBucket> { [100] = LitigationCaseStatusBucket.Pending };

        Assert.Equal([1L], LitigationJudgmentFilter.JudgmentDocumentIds(chunks, pending).OrderBy(x => x));
    }

    [Fact]
    public void The_same_dismissal_is_found_when_the_order_is_not_the_newest_of_a_disposed_case()
    {
        var chunks = new[]
        {
            Page(1, 10, 100, 1, 0, "Present: counsel."),
            Page(1, 10, 100, 2, 0, "The appeal is allowed and the impugned order is set aside."),
            Page(1, 10, 100, 3, 0, "Signature page."),
            Page(2, 11, 100, 1, 0, "Compliance report to be filed. List on 5 April."),   // a later order of the same case
        };
        chunks[3].OrderDate = "05-04-2022";
        var disposed = new Dictionary<long, LitigationCaseStatusBucket> { [100] = LitigationCaseStatusBucket.Disposed };

        Assert.Contains(1L, LitigationJudgmentFilter.JudgmentDocumentIds(chunks, disposed));
    }

    [Fact]
    public void A_long_order_is_judged_on_its_last_few_chunks_not_only_the_final_one()
    {
        var chunks = Enumerable.Range(1, 8).Select(i => Page(1, 10, 100, i, 0,
            i == 6 ? "In the result, the suit is decreed with costs." : i == 8 ? "Signature page." : "Submissions recorded.")).ToList();
        Assert.True(LitigationJudgmentFilter.TailWindow(chunks).Contains("decreed"));
        Assert.False(LitigationJudgmentFilter.TailWindow(Enumerable.Range(1, 8).Select(i => Page(1, 10, 100, i, 0, "x" + new string('y', 3500))).ToList()).Length > 3000);
    }

    [Fact]
    public void An_adjournment_with_a_trailing_signature_page_is_still_skipped()
    {
        var chunks = new[]
        {
            Page(1, 10, 100, 1, 0, "Present: counsel. The matter is adjourned. List on 12 March 2022."),
            Page(1, 10, 100, 2, 0, "Signature page."),
        };
        var pending = new Dictionary<long, LitigationCaseStatusBucket> { [100] = LitigationCaseStatusBucket.Pending };
        Assert.Empty(LitigationJudgmentFilter.JudgmentDocumentIds(chunks, pending));
    }
}
