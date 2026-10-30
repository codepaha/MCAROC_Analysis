using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>#564: "which documents are about this land?" Built from the Deccan Chronicle case: two open charges whose deeds describe the land by survey numbers, an earlier
/// charge that was satisfied (its no-claim letter describes the land by area and name only, no survey numbers), and documents that only look related: shareholder lists
/// with a street called Mathuradas Extension Road, a certificate whose number happens to be a survey number, a loan amount that shares the area's digits, and a plot
/// that shares a survey number's digits.</summary>
public class PropertyDocumentFinderTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    private const string Question = "Which documents relate to this property: the land admeasuring about 2152.25 square yards (1800 square meters) forming part of the larger property " +
        "known as \"Mathuradas Mills Compound\" bearing Survey Nos. 3/ 2840, 3/ 2841, 2842, 2843, 1/ 2844, 2845, corresponding to CTS 242 of Lower Parel?";

    private sealed record Seeded(long RequestId, long BatchId, Dictionary<string, long> Docs);

    private static async Task<Seeded> SeedAsync(AppDbContext db)
    {
        var request = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Land Co", RequestNumber = $"PD-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        var batch = new McaFilingBatch { RequestId = request.RequestId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "SRN-1", NestedZipName = "n.zip" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        var docs = new Dictionary<string, long>();
        async Task AddAsync(string name, string formType, params (int Page, string Text)[] chunks)
        {
            var doc = new McaFilingDocument { FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = name, StoragePath = "",
                FileHash = Guid.NewGuid().ToString("N"), ProcessingStatus = FilingDocumentProcessingStatus.Completed, Category = FilingCategory.Charge,
                TextExtractionMethod = TextExtractionMethod.Native, UpdatedAt = DateTime.UtcNow, FormType = formType };
            db.McaFilingDocuments.Add(doc);
            await db.SaveChangesAsync();
            docs[name] = doc.FilingDocumentId;
            var i = 0;
            foreach (var (page, text) in chunks)
                db.DocumentChunks.Add(new DocumentChunk { RequestId = request.RequestId, BatchId = batch.BatchId, FilingId = filing.FilingId, FilingDocumentId = doc.FilingDocumentId,
                    Srn = "SRN-1", Category = FilingCategory.Charge, FormType = formType, DocumentName = name, ChunkIndex = i++, PageNumber = page, ChunkText = text,
                    Embedding = new SqlVector<float>(new float[768]), EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "2.0", CreatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await AddAsync("Deed-of-Mortgage.pdf", "Instrument of Charge",
            (1, "Deed of mortgage made between the company and the lender. Boilerplate on interest and default."),
            (12, "Schedule 2: All that piece and parcel of N.A. land admeasuring about 2,152.25 sq. yards i.e. 1,800 sq. mtrs. bearing Survey Nos. 3/2840, 3/2841, 2842, 2843, C.S. No. 242, Lower Parel"));
        await AddAsync("Form-CHG-8.pdf", "Form CHG-8",
            (1, "PARTICULARS: Total land 1800 sq. mt Survey No. 3/2840, 3/2841, 2842, 2843, 1/2844, 2845 corresponding C.T.S. No. 242, Lower Parel, Mumbai"));
        // the no-claim letter that releases the land: area (with its unit) and name, no survey numbers at all
        await AddAsync("HDFC-NoC.pdf", "Charge Satisfaction Letter",
            (1, "In view of the above, HDFC has no claim, right, title or interest in respect of the piece and parcel of land admeasuring 2152.25 yards equivalent to 1800 sq mtrs " +
                "forming part of the Mathuradas Mills Compound at Lower Parel."));
        // look-alikes that must not be found as documents about this land
        await AddAsync("Shareholder-list.pdf", "List of Shareholders / Directors",
            (1, "SHAH HASMUKHLAL  102/B VASANT AISHWARYA  MATHURADAS EXTN ROAD  KANDIVLI WEST MUMBAI  folio 2840  100 shares"));
        await AddAsync("Certificate-2840.pdf", "Certificate",
            (1, "Certificate number 2840 issued on the 12th day; EC 2851 dated 28/05/2012; nothing about land."));
        await AddAsync("Shareholder-at-the-compound.pdf", "List of Shareholders / Directors",
            (1, "ANR INVESTMENTS LTD  67 IDEAL INDUSTRIAL ESTATE  2ND FLOOR MATHURADAS MILL COMPOUND  126 N M JOSHI MARG  LOWER PAREL  MUMBAI  100 shares"));
        await AddAsync("Loan-sanction.pdf", "Sanction Letter",
            (1, "Term loan of Rs 2152.25 lakhs sanctioned for working capital against hypothecation of stocks; interest at 12.5% per annum."));
        await AddAsync("Plot-document.pdf", "Instrument of Charge",
            (1, "Shop on plot no. 4242 of the industrial estate at Thane, mortgaged to the lender."));
        await AddAsync("Unrelated.pdf", "Financial Statements", (1, "Balance sheet as at 31 March. Revenue from operations. Notes to accounts."));
        return new(request.RequestId, batch.BatchId, docs);
    }

    [Fact]
    public async Task Finds_every_document_that_describes_the_land_and_none_that_only_look_related()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);

        var found = await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, Question, CancellationToken.None);

        Assert.False(found.Truncated);
        Assert.Equal(["Deed-of-Mortgage.pdf", "Form-CHG-8.pdf", "HDFC-NoC.pdf"], found.Documents.Where(d => d.Confirmed).Select(d => d.Name).Order().ToList());
        Assert.DoesNotContain(found.Documents, d => d.Name is "Shareholder-list.pdf" or "Certificate-2840.pdf" or "Shareholder-at-the-compound.pdf" or "Loan-sanction.pdf" or "Unrelated.pdf");
        var deed = found.Documents.Single(d => d.Name == "Deed-of-Mortgage.pdf");
        Assert.Equal([12], deed.Pages);                                    // only the page that describes the land, not the boilerplate page
        Assert.Contains(found.Documents.Single(d => d.Name == "HDFC-NoC.pdf").Matched, m => m.StartsWith("the area") || m == "the property's name");
    }

    [Fact]
    public async Task An_amount_that_shares_the_areas_digits_is_not_an_area_without_a_unit_of_land_measure()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);

        // the figure is given WITH its unit in the question, but only the document that also gives a unit of area (or the name / an identifier) can match: "Rs 2152.25 lakhs" cannot
        var found = await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, "Which documents mention land of 2152.25 sq yards?", CancellationToken.None);

        Assert.DoesNotContain(found.Documents, d => d.Name == "Loan-sanction.pdf");
        Assert.Contains(found.Documents, d => d.Name == "HDFC-NoC.pdf" && !d.Confirmed);   // an area with its unit, alone, is a candidate: it may be another parcel of the same size
    }

    [Fact]
    public async Task A_satisfied_charge_is_reported_with_its_status_so_the_answer_can_name_it()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var runId = (await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == s.RequestId)).LatestCompletedIngestionRunId!.Value;
        var number = "9" + Random.Shared.Next(10000000, 99999999);
        db.RocCharges.Add(new RocCharge { RequestId = s.RequestId, IngestionRunId = runId, RocChargeNumber = number, LatestChargeHolderRaw = "HOUSING DEVELOPMENT CORP. LTD", ChargeStatus = "Satisfied", CurrentAmount = 3.68m });
        db.ChargeDocumentLinks.Add(new ChargeDocumentLink { RequestId = s.RequestId, BatchId = s.BatchId, RocChargeNumber = number, FilingDocumentId = s.Docs["HDFC-NoC.pdf"], Method = ChargeDocumentLinkMethod.EmbeddedAttachment });
        await db.SaveChangesAsync();

        var facts = await new PropertyDocumentFinder(db).FactsAsync(s.RequestId, s.BatchId, Question, CancellationToken.None);

        Assert.Contains("This is every such document", facts[0].Text);
        Assert.Contains(facts, f => f.Text.StartsWith("Document: HDFC-NoC.pdf") && f.Text.Contains($"linked to charge {number} (HOUSING DEVELOPMENT CORP. LTD, Satisfied)"));
        Assert.Contains(facts, f => f.Text.StartsWith("Document: Form-CHG-8.pdf") && f.Text.Contains("not linked to a charge"));
    }

    /// <summary>One identifier can recur (a folio, a certificate number, another parcel), so it cannot establish which documents are about the property. The finder still
    /// returns the documents that carry it, openly as candidates, and says it cannot establish the list.</summary>
    [Fact]
    public async Task A_question_naming_a_single_identifier_gets_candidates_and_is_told_it_cannot_establish_the_list()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);

        var found = await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, "Which documents mention survey no. 2840?", CancellationToken.None);
        var facts = await new PropertyDocumentFinder(db).FactsAsync(s.RequestId, s.BatchId, "Which documents mention survey no. 2840?", CancellationToken.None);

        Assert.Equal("survey 2840", found.OnlyIdentifier);
        Assert.All(found.Documents, d => Assert.False(d.Confirmed));
        Assert.Equal(["Deed-of-Mortgage.pdf", "Form-CHG-8.pdf"], found.Documents.Select(d => d.Name).Order().ToList());
        Assert.DoesNotContain(found.Documents, d => d.Name is "Shareholder-list.pdf" or "Certificate-2840.pdf");   // an unlabelled number is no identifier
        Assert.Contains("only one identifier (survey 2840)", facts[0].Text);
        Assert.Contains(facts, f => f.Text.StartsWith("Candidate document (possible match only, identity not established): Deed-of-Mortgage.pdf"));
        Assert.DoesNotContain(facts, f => f.Text.Contains("This is every such document"));
    }

    [Fact]
    public async Task A_number_shared_across_kinds_of_identifier_is_only_a_candidate()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);

        var found = await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, "Which documents mention survey no. 4242?", CancellationToken.None);

        var plot = Assert.Single(found.Documents);
        Assert.Equal("Plot-document.pdf", plot.Name);
        Assert.False(plot.Confirmed);
        Assert.Contains(plot.Matched, m => m.Contains("plot 4242") && m.Contains("a different kind of identifier"));
    }

    [Theory]
    [InlineData(1, 600, "documents")]   // more qualifying documents than may be returned
    [InlineData(25, 1, "chunks")]       // more matching chunks for a term than may be read
    public async Task A_search_that_hits_a_limit_says_so_and_is_never_called_complete(int maxDocuments, int maxChunksPerTerm, string limit)
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var finder = new PropertyDocumentFinder(db, maxChunksPerTerm, maxDocuments);

        var found = await finder.FindAsync(s.RequestId, s.BatchId, Question, CancellationToken.None);
        var facts = await finder.FactsAsync(s.RequestId, s.BatchId, Question, CancellationToken.None);

        Assert.True(found.Truncated, limit);
        if (limit == "documents") Assert.Single(found.Documents);
        Assert.StartsWith("At least ", facts[0].Text);
        Assert.Contains("do not describe this list as complete", facts[0].Text);
        Assert.DoesNotContain(facts, f => f.Text.Contains("This is every such document"));
    }

    /// <summary>The facts say how complete the list is; the prompt must let the model say so when it is complete, and not when it is not.</summary>
    [Fact]
    public void The_prompt_keys_completeness_to_what_the_documents_header_says()
    {
        var prompt = ChatCompletionService.BuildPrompt("Land Co", RetrievalContext.Empty, [], "Which documents relate to this property?");

        Assert.Contains("\"N document(s) on file whose own text describes the property ... This is every such document\" is the complete list", prompt);
        Assert.Contains("list EVERY \"Document:\" fact", prompt);
        Assert.Contains("never answer that no charge was satisfied while such a fact says otherwise", prompt);
        Assert.Contains("\"At least N document(s) ... reached its limit\" is NOT complete", prompt);
        Assert.Contains("\"Candidate document (possible match only, identity not established)\" facts are only possible matches", prompt);
        Assert.Contains("\"The question names only one identifier\" means the documents cannot be established", prompt);
    }

    [Theory]
    [InlineData("How many open charges are there?")]
    [InlineData("What is the company's revenue?")]
    [InlineData("")]
    public async Task A_question_that_names_no_property_finds_nothing_and_costs_no_search(string question)
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        var found = await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, question, CancellationToken.None);
        Assert.Empty(found.Documents);
        Assert.False(found.Truncated);
    }
}
