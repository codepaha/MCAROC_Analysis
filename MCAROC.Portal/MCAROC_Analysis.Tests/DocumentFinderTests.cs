using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#499: a request for a document is answered from the document index — every matching document as a link, newest first, or a plain "none" —
/// with no retrieval and no AI call. A question about what a document says is not.</summary>
public class DocumentFinderTests : IAsyncLifetime
{
    [Theory]
    [InlineData("Show me the charge registration certificates", "Certificate of Registration of Charge")]
    [InlineData("Where is the satisfaction certificate?", "Certificate of Satisfaction of Charge")]
    [InlineData("Please give the name change certificate", "Certificate of Incorporation on Change of Name")]
    [InlineData("Can you fetch the list of allottees", "List of Allottees (PAS-5)")]
    [InlineData("I need a copy of the MoA", "MoA")]
    [InlineData("download the annual return", "Form MGT-7")]
    [InlineData("send the board resolutions", "Board Resolution")]
    [InlineData("open the deed of hypothecation", "Instrument of Charge")]
    public void A_request_for_a_document_names_its_type(string question, string formType)
    {
        var asked = DocumentFinder.Parse(question);
        Assert.NotNull(asked);
        Assert.Contains(formType, asked!.FormTypes);
    }

    [Theory]
    [InlineData("What does the MoA say about borrowing powers?")]                     // about its content: the usual retrieval answers
    [InlineData("When was the charge with SBI satisfied?")]
    [InlineData("Show me the litigation summary")]                                    // asks for something, but no document type
    [InlineData("Show me what the charge registration certificate says about the interest rate")]   // asks about content (review of #506)
    [InlineData("Give me the amount in the latest charge certificate")]
    [InlineData("Show the details of the board resolution on borrowing")]
    public void Other_questions_are_not_document_requests(string question) => Assert.Null(DocumentFinder.Parse(question));

    [Fact]
    public void A_year_in_the_question_narrows_the_request() =>
        Assert.Equal(2021, DocumentFinder.Parse("fetch the annual return for 2021")!.Year);

    [Theory]
    [InlineData("Certificate of Registration of Charge-12032019.pdf", 2019, 3, 12)]
    [InlineData("Form PAS-3-271215.pdf", 2015, 12, 27)]
    [InlineData("Form PAS-3-19122022_signed.pdf", 2022, 12, 19)]
    public void The_date_in_an_MCA_file_name_is_read(string name, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), DocumentFinder.DateInName(name));

    // ---- against the database --------------------------------------------------------------------------------------------------------------

    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(long RequestId, long Older, long Newer)> SeedAsync()
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "DF" + Guid.NewGuid().ToString("N")[..8], ClientName = "Finder", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "Finder Co", RequestNumber = $"DF-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        var source = new RequestDocument { Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip", StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow };
        db.RequestDocuments.Add(source);
        await db.SaveChangesAsync();
        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = source.DocumentId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "H12345678", NestedZipName = "1.zip", OuterCategoryFolder = "Charge Documents" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();
        McaFilingDocument Doc(string name, string? formType) => new()
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = name, StoragePath = @"C:\fake\a.pdf",
            FileHash = Guid.NewGuid().ToString("N"), ProcessingStatus = FilingDocumentProcessingStatus.Completed, FormType = formType,
            Category = formType is null ? FilingCategory.Unclassified : FilingCategory.Charge, ClassificationMethod = "FilenameKeyword", UpdatedAt = DateTime.UtcNow
        };
        var older = Doc("Certificate of Registration of Charge-12032015.pdf", "Certificate of Registration of Charge");
        var newer = Doc("Certificate of Registration of Charge-25102019.pdf", "Certificate of Registration of Charge");
        var folderOnly = Doc("Optional Attachment-(2).pdf", null);
        folderOnly.Category = FilingCategory.Charge;           // placed by its folder: a category, but no type
        folderOnly.ClassificationMethod = "OuterFolderFallback";
        db.McaFilingDocuments.AddRange(older, newer, Doc("Form 8-180314.pdf", "Form 8"), Doc("Optional Attachment-(1).pdf", null), folderOnly);
        await db.SaveChangesAsync();
        return (request.RequestId, older.FilingDocumentId, newer.FilingDocumentId);
    }

    [Fact]
    public async Task The_matching_documents_are_listed_newest_first_each_a_link()
    {
        var (requestId, older, newer) = await SeedAsync();
        await using var db = Ctx();
        var answer = await new DocumentFinder(db).TryAnswerAsync(requestId, "Show me the charge registration certificates", default);

        Assert.NotNull(answer);
        Assert.False(answer!.InsufficientEvidence);
        Assert.Equal([newer, older], answer.CitedSources.Select(c => c.DocumentId!.Value).ToArray());
        Assert.All(answer.CitedSources, c => Assert.Equal("DocumentChunk", c.SourceType));   // rendered as a PDF link by the chat panel
        Assert.Contains("2 certificate of registration of charge", answer.Answer);
        Assert.Contains("25 Oct 2019", answer.Answer);
    }

    [Fact]
    public async Task When_the_filings_hold_none_it_says_so_and_mentions_the_unidentified_documents()
    {
        var (requestId, _, _) = await SeedAsync();
        await using var db = Ctx();
        var answer = await new DocumentFinder(db).TryAnswerAsync(requestId, "give the name change certificate", default);

        Assert.NotNull(answer);
        Assert.Empty(answer!.CitedSources);
        Assert.Contains("contain no certificate of incorporation on change of name", answer.Answer);
        Assert.Contains("2 document(s)", answer.Answer);   // the unclassified attachment and the folder-only one (review of #506)
    }

    [Fact]
    public async Task A_year_keeps_only_that_years_documents()
    {
        var (requestId, older, _) = await SeedAsync();
        await using var db = Ctx();
        var answer = await new DocumentFinder(db).TryAnswerAsync(requestId, "show the charge registration certificate of 2015", default);
        Assert.Equal([older], answer!.CitedSources.Select(c => c.DocumentId!.Value).ToArray());
    }

    [Fact]
    public async Task A_question_about_content_is_left_to_the_usual_retrieval()
    {
        var (requestId, _, _) = await SeedAsync();
        await using var db = Ctx();
        Assert.Null(await new DocumentFinder(db).TryAnswerAsync(requestId, "What amount does the charge registration certificate state?", default));
    }
}
