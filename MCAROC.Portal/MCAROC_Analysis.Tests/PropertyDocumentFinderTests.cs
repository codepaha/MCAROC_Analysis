using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>#564: "which documents are about this land?" Built from the Deccan Chronicle case: two open charges whose deeds describe the land by survey numbers, an earlier
/// charge that was satisfied (its no-claim letter describes the land by area and name only, no survey numbers), and documents that only look related: shareholder lists
/// with a street called Mathuradas Extension Road, and a certificate whose number happens to be a survey number.</summary>
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
        // the no-claim letter that releases the land: area and name, no survey numbers at all
        await AddAsync("HDFC-NoC.pdf", "Charge Satisfaction Letter",
            (1, "In view of the above, HDFC has no claim, right, title or interest in respect of the piece and parcel of land admeasuring 2152.25 yards equivalent to 1800 sq mtrs " +
                "forming part of the Mathuradas Mills Compound at Lower Parel."));
        // look-alikes that must not be found
        await AddAsync("Shareholder-list.pdf", "List of Shareholders / Directors",
            (1, "SHAH HASMUKHLAL  102/B VASANT AISHWARYA  MATHURADAS EXTN ROAD  KANDIVLI WEST MUMBAI  folio 2840  100 shares"));
        await AddAsync("Certificate-2840.pdf", "Certificate",
            (1, "Certificate number 2840 issued on the 12th day; EC 2851 dated 28/05/2012; nothing about land."));
        // a shareholder whose address is in the same compound, in a different building: names the compound, not this land
        await AddAsync("Shareholder-at-the-compound.pdf", "List of Shareholders / Directors",
            (1, "ANR INVESTMENTS LTD  67 IDEAL INDUSTRIAL ESTATE  2ND FLOOR MATHURADAS MILL COMPOUND  126 N M JOSHI MARG  LOWER PAREL  MUMBAI  100 shares"));
        await AddAsync("Unrelated.pdf", "Financial Statements", (1, "Balance sheet as at 31 March. Revenue from operations. Notes to accounts."));
        return new(request.RequestId, batch.BatchId, docs);
    }

    [Fact]
    public async Task Finds_every_document_that_describes_the_land_and_none_that_only_look_related()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);

        var found = await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, Question, CancellationToken.None);

        Assert.Equal(["Deed-of-Mortgage.pdf", "Form-CHG-8.pdf", "HDFC-NoC.pdf"], found.Select(d => d.Name).Order().ToList());
        var deed = found.Single(d => d.Name == "Deed-of-Mortgage.pdf");
        Assert.Equal([12], deed.Pages);                                    // only the page that describes the land, not the boilerplate page
        Assert.Contains(found.Single(d => d.Name == "HDFC-NoC.pdf").Matched, m => m.StartsWith("the area") || m == "the property's name");
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

        Assert.Contains("every such document", facts[0].Text);
        Assert.Contains(facts, f => f.Text.Contains("HDFC-NoC.pdf") && f.Text.Contains($"linked to charge {number} (HOUSING DEVELOPMENT CORP. LTD, Satisfied)"));
        Assert.Contains(facts, f => f.Text.Contains("Form-CHG-8.pdf") && f.Text.Contains("not linked to a charge"));
    }

    [Theory]
    [InlineData("How many open charges are there?")]
    [InlineData("What is the company's revenue?")]
    [InlineData("")]
    public async Task A_question_that_names_no_property_finds_nothing_and_costs_no_search(string question)
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        Assert.Empty(await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, question, CancellationToken.None));
    }

    [Fact]
    public async Task One_shared_identifier_alone_is_not_enough()
    {
        await using var db = CreateContext();
        var s = await SeedAsync(db);
        // "survey no. 2840" appears in the deed AND as a folio in the shareholder list: only the document that also names the area or a second identifier qualifies
        var found = await new PropertyDocumentFinder(db).FindAsync(s.RequestId, s.BatchId, "Which documents mention survey no. 2840?", CancellationToken.None);
        Assert.DoesNotContain(found, d => d.Name == "Shareholder-list.pdf" || d.Name == "Certificate-2840.pdf");
    }
}
