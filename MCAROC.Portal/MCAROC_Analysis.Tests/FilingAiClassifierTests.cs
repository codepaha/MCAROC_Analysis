using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#499 step 2: the AI fallback types a document the rules could not place only when its answer is a known type and the title it quotes is on the
/// page; the category always comes from the classifier's own list; the same PDF is asked about once; labels only.</summary>
public class FilingAiClassifierTests : IAsyncLifetime
{
    private const string ChargeCertificateText = "Government of India\nMinistry of Corporate Affairs\nCertificate of Registration of Charge\n[Pursuant to section 77(3)]";

    [Fact]
    public void A_known_type_with_a_title_quoted_from_the_page_is_accepted_with_the_lists_category()
    {
        var r = FilingAiClassifier.Check("""{"formType": "Certificate of Registration of Charge", "titleQuote": "certificate of  registration of charge"}""", ChargeCertificateText);
        Assert.NotNull(r);
        Assert.Equal(FilingCategory.Charge, r!.Category);
    }

    [Theory]
    [InlineData("""{"formType": "Loan Sanction Letter", "titleQuote": "Certificate of Registration of Charge"}""")]   // not a known type
    [InlineData("""{"formType": "Certificate of Registration of Charge", "titleQuote": "Sanction letter dated 1 April"}""")]   // quote not on the page
    [InlineData("""{"formType": "Certificate of Registration of Charge", "titleQuote": "Charge"}""")]   // too short to prove anything
    [InlineData("""{"formType": "Unknown", "titleQuote": ""}""")]
    [InlineData("""{"formType": "Certificate of Registration of Charge", "titleQuote": "Government of India"}""")]   // on the page, but shows no type (review of #504)
    [InlineData("""{"formType": "Board Resolution", "titleQuote": "Ministry of Corporate Affairs"}""")]
    [InlineData("not json")]
    public void Any_other_answer_leaves_the_document_as_it_was(string raw) => Assert.Null(FilingAiClassifier.Check(raw, ChargeCertificateText));

    [Theory]
    [InlineData("Certificate of Registration of Charge", "Certificate of Registration of Charge")]          // the type's own rule
    [InlineData("List of Allottees (PAS-5)", "LIST OF ALLOTTEES")]                                         // a rule for the type, not every word of its name
    [InlineData("Board Resolution", "Certified true copy of the resolution passed by the Board")]           // every distinctive word of the name
    [InlineData("Form DPT-3", "FORM NO. DPT-3")]
    [InlineData("Financial Statements", "Balance Sheet as at 31st March 2023")]
    public void A_quote_that_names_the_type_shows_it(string formType, string quote) => Assert.True(FilingClassifier.QuoteShowsType(formType, quote));

    [Theory]
    [InlineData("Certificate of Registration of Charge", "Government of India")]
    [InlineData("Board Resolution", "Ministry of Corporate Affairs")]
    [InlineData("Form DPT-3", "Form No. DPT-1")]
    [InlineData("Shareholder Resolution", "Resolution")]                                                    // half the name is not the name
    public void A_quote_that_does_not_name_the_type_does_not_show_it(string formType, string quote) => Assert.False(FilingClassifier.QuoteShowsType(formType, quote));

    [Fact]
    public void The_prompt_lists_every_known_type() =>
        Assert.All(FilingClassifier.KnownTypes.Keys, t => Assert.Contains($"- {t}", FilingAiClassifier.BuildPrompt("Optional Attachment-(1).pdf", "Charge Documents", "x")));

    // ---- the service --------------------------------------------------------------------------------------------------------------------------

    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Answers every call; counts only the calls about this test's PDF (its text carries the token), since the shared test database can hold other
    /// tests' unplaced documents.</summary>
    private sealed class ScriptedClient(LitigationAiCallResult answer, string token) : ILitigationAiAnalysisClient
    {
        public int Calls { get; private set; }
        public Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct, string? modelId = null)
        {
            Assert.StartsWith(FilingAiClassifier.PromptMarker, prompt);
            if (prompt.Contains(token)) Calls++;
            return Task.FromResult(answer);
        }
    }

    private static readonly LitigationAiCallResult ChargeCertificateAnswer = new(true, """{"formType": "Certificate of Registration of Charge", "titleQuote": "Certificate of Registration of Charge"}""", null);

    private static FilingAiClassifier Service(AppDbContext db, ILitigationAiAnalysisClient? client)
    {
        var services = new ServiceCollection();
        if (client is not null) services.AddSingleton(client);
        return new FilingAiClassifier(db, services.BuildServiceProvider(), NullLogger<FilingAiClassifier>.Instance);
    }

    /// <summary>Two unplaced documents that are the same PDF (one hash), each with a chunk; returns their ids.</summary>
    private static async Task<(long A, long B, string TextPath, string Token)> SeedAsync()
    {
        var token = "REF" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var textPath = Path.Combine(Path.GetTempPath(), $"aicls-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(textPath, ChargeCertificateText + " Charge reference " + token);
        await using var db = Ctx();
        var client = new Client { ClientCode = "AC" + Guid.NewGuid().ToString("N")[..8], ClientName = "AI classify", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "AI Classify Co", RequestNumber = $"AC-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        var source = new RequestDocument { Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip", StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow };
        db.RequestDocuments.Add(source);
        await db.SaveChangesAsync();
        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = source.DocumentId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "S" + Guid.NewGuid().ToString("N")[..8], NestedZipName = "1.zip", OuterCategoryFolder = "Charge Documents" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();
        var hash = Guid.NewGuid().ToString("N");
        McaFilingDocument Doc() => new()
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = "Optional Attachment-(1).pdf",
            StoragePath = @"C:\fake\a.pdf", FileHash = hash, ExtractedTextPath = textPath, ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            Category = FilingCategory.Charge, ClassificationMethod = "OuterFolderFallback", ClassifierVersion = FilingClassifier.Version,
            AiExtractionStatus = AiExtractionStatus.NotApplicable, UpdatedAt = DateTime.UtcNow
        };
        var a = Doc();
        var b = Doc();
        db.McaFilingDocuments.AddRange(a, b);
        await db.SaveChangesAsync();
        foreach (var d in new[] { a, b })
            db.DocumentChunks.Add(new DocumentChunk
            {
                RequestId = request.RequestId, FilingDocumentId = d.FilingDocumentId, FilingId = filing.FilingId, BatchId = batch.BatchId, Srn = filing.Srn,
                Category = FilingCategory.Charge, ChunkText = "x", ChunkIndex = 0, PageNumber = 1, CreatedDate = DateTime.UtcNow,
                Embedding = new Microsoft.Data.SqlTypes.SqlVector<float>(new float[768])
            });
        await db.SaveChangesAsync();
        return (a.FilingDocumentId, b.FilingDocumentId, textPath, token);
    }

    private static async Task DrainAsync(ILitigationAiAnalysisClient? client, int maxCallsPerDay = int.MaxValue)
    {
        for (var i = 0; i < 10; i++)
        {
            await using var db = Ctx();
            if ((await Service(db, client).ClassifyBatchAsync(500, default, maxCallsPerDay)).Seen == 0) return;
        }
    }

    [Fact]
    public async Task The_same_pdf_is_asked_about_once_and_both_documents_and_their_chunks_get_the_type()
    {
        var (a, b, path, token) = await SeedAsync();
        try
        {
            var client = new ScriptedClient(ChargeCertificateAnswer, token);
            await DrainAsync(client);

            await using var verify = Ctx();
            foreach (var id in new[] { a, b })
            {
                var d = await verify.McaFilingDocuments.AsNoTracking().SingleAsync(x => x.FilingDocumentId == id);
                Assert.Equal(("Certificate of Registration of Charge", FilingAiClassifier.Method), (d.FormType, d.ClassificationMethod));
                Assert.NotNull(d.AiClassifiedUtc);
                Assert.Equal(AiExtractionStatus.NotApplicable, d.AiExtractionStatus);   // labels only
                Assert.Equal("Certificate of Registration of Charge", (await verify.DocumentChunks.AsNoTracking().SingleAsync(c => c.FilingDocumentId == id)).FormType);
            }
            Assert.Equal(1, client.Calls);   // both copies were in one batch: the second reused the first's answer
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_failed_call_leaves_the_document_untried_and_without_a_client_nothing_is_marked()
    {
        var (a, _, path, token) = await SeedAsync();
        try
        {
            var failing = new ScriptedClient(new LitigationAiCallResult(false, "", "503 UNAVAILABLE"), token);
            await DrainAsync(failing);
            Assert.True(failing.Calls >= 1);
            Assert.True(failing.Calls <= 10);                      // at most one call per pass, never one per copy
            await DrainAsync(null);
            await using var verify = Ctx();
            var d = await verify.McaFilingDocuments.AsNoTracking().SingleAsync(x => x.FilingDocumentId == a);
            Assert.Null(d.AiClassifiedUtc);                       // tried again on a later pass
            Assert.Equal("OuterFolderFallback", d.ClassificationMethod);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_pdf_answered_without_a_type_is_not_asked_about_again()
    {
        var (_, b, path, token) = await SeedAsync();
        try
        {
            var unknown = new ScriptedClient(new LitigationAiCallResult(true, """{"formType": "Unknown", "titleQuote": ""}""", null), token);
            await DrainAsync(unknown);
            Assert.Equal(1, unknown.Calls);
            await using (var db = Ctx())   // a later pass, or a refresh importing the same PDF again
                await db.McaFilingDocuments.Where(d => d.FilingDocumentId == b).ExecuteUpdateAsync(u => u.SetProperty(d => d.AiClassifiedUtc, (DateTime?)null));
            var again = new ScriptedClient(ChargeCertificateAnswer, token);
            await DrainAsync(again);
            Assert.Equal(0, again.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_daily_cap_stops_the_calls_and_leaves_the_documents_for_later()
    {
        var (a, _, path, token) = await SeedAsync();
        try
        {
            var client = new ScriptedClient(ChargeCertificateAnswer, token);
            await DrainAsync(client, maxCallsPerDay: 0);
            Assert.Equal(0, client.Calls);
            await using var verify = Ctx();
            Assert.Null((await verify.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == a)).AiClassifiedUtc);
        }
        finally { File.Delete(path); }
    }
}
