using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#497 PR D: the chat is given every change of the company's name as facts — whatever the question — dated by the certificate of change of
/// name where one was read, with a date the name history disagrees on said, and a certificate-backed fact citing the certificate's page.</summary>
public class NameChangeChatFactsTests : IAsyncLifetime
{
    // The 2014 rules wording, as extracted from request 11's certificate.
    private const string Certificate = """
        --- Page 1 (native) ---
        GOVERNMENT OF INDIAMINISTRY OF CORPORATE AFFAIRSCertificate of Incorporation pursuant to change of name [Pursuant to rule 29 of the Companies (Incorporation) Rules, 2014]I hereby certify that the name of the company has been changed from KE HEALTHCARE PRIVATE LIMITED to LIFEXSCIENCES PRIVATE LIMITED with effect from the date of this certificate and that the company is Company limited byshares.Given under my hand at ROC, CPC this  ELEVENTH day of APRIL  TWO THOUSAND TWENTY FIVEAssistant Registrar of Companies
        """;

    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "namechat-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<(long RequestId, long CertificateId)> SeedAsync(bool withCertificate)
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "NF" + Guid.NewGuid().ToString("N")[..8], ClientName = "Name facts", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "LIFEX SCIENCES PRIVATE LIMITED", RequestNumber = $"NF-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        db.CompanyNameHistories.AddRange(
            new CompanyNameHistory { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, DisplayOrder = 1, PreviousName = "CINQUE AYURVEDA SCIENCES PRIVATE LIMITED", TillDate = new DateOnly(2020, 10, 1) },
            new CompanyNameHistory { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, DisplayOrder = 2, PreviousName = "KE HEALTHCARE PRIVATE LIMITED", TillDate = new DateOnly(2025, 4, 10) });
        await db.SaveChangesAsync();
        if (!withCertificate) return (request.RequestId, 0);

        var source = new RequestDocument { RequestId = request.RequestId, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip", StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow };
        db.RequestDocuments.Add(source);
        await db.SaveChangesAsync();
        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = source.DocumentId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "AB7654321", NestedZipName = "1.zip", OuterCategoryFolder = "Incorporation Documents" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();
        var path = Path.Combine(_dir, "certificate.txt");
        await File.WriteAllTextAsync(path, Certificate);
        var certificate = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId, OriginalFileName = "Certificate-of-change-of-name.pdf", StoragePath = @"C:\fake\a.pdf",
            FileHash = Guid.NewGuid().ToString("N"), ProcessingStatus = FilingDocumentProcessingStatus.Completed, FormType = NameChangeCertificateLoader.CertificateFormType,
            Category = FilingCategory.Constitutional, ClassificationMethod = "FilenameKeyword", UpdatedAt = DateTime.UtcNow, ExtractedTextPath = path
        };
        db.McaFilingDocuments.Add(certificate);
        await db.SaveChangesAsync();
        return (request.RequestId, certificate.FilingDocumentId);
    }

    private static Task<List<StructuredFact>> DigestAsync(AppDbContext db, long requestId, string question) =>
        new StructuredFactsProvider(db).BuildDigestAsync(requestId, QuestionHintExtractor.Extract(question, [], []), default);

    [Fact]
    public async Task Every_name_change_is_a_fact_and_a_certified_one_cites_the_certificate_page()
    {
        var (requestId, certificateId) = await SeedAsync(withCertificate: true);
        await using var db = Ctx();
        var facts = await DigestAsync(db, requestId, "When did the company change its name, and what was it called before?");

        Assert.Contains(facts, f => f.Text == "The company has changed its name 2 time(s); its current name is LIFEX SCIENCES PRIVATE LIMITED. Former names, oldest first: CINQUE AYURVEDA SCIENCES PRIVATE LIMITED; KE HEALTHCARE PRIVATE LIMITED.");
        var history = Assert.Single(facts, f => f.Text.Contains("from \"CINQUE AYURVEDA SCIENCES PRIVATE LIMITED\""));
        Assert.Equal("Name change: renamed from \"CINQUE AYURVEDA SCIENCES PRIVATE LIMITED\" to \"KE HEALTHCARE PRIVATE LIMITED\" on 1 Oct 2020 (from the name history; no certificate of change of name was read for it).", history.Text);
        Assert.Null(history.DocumentId);

        // The certificate dates the change a day after the history's "in use until": within a day, they agree.
        var certified = Assert.Single(facts, f => f.Text.Contains("from \"KE HEALTHCARE PRIVATE LIMITED\""));
        Assert.Equal("Name change: renamed from \"KE HEALTHCARE PRIVATE LIMITED\" to \"LIFEX SCIENCES PRIVATE LIMITED\" on 11 Apr 2025 (the name history and the certificate of change of name (page 1) agree).", certified.Text);
        Assert.Equal((certificateId, 1, "Certificate-of-change-of-name.pdf"), (certified.DocumentId!.Value, certified.PageNumber!.Value, certified.DocumentName));
    }

    [Fact]
    public async Task The_name_changes_are_given_for_a_question_about_something_else_and_without_a_certificate()
    {
        var (requestId, _) = await SeedAsync(withCertificate: false);
        await using var db = Ctx();
        var facts = await DigestAsync(db, requestId, "Which charges are still open?");
        var changes = facts.Where(f => f.Text.StartsWith("Name change:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, f => Assert.Contains("no certificate of change of name was read", f.Text));
        Assert.All(changes, f => Assert.Equal(nameof(CompanyNameHistory), f.EntityType));
    }

    [Fact]
    public void A_fact_read_from_a_document_page_is_cited_with_that_page()
    {
        var source = new RetrievedSource("F1", SourceType.StructuredFact, "Name change: renamed from \"A\" to \"B\" on 11 Apr 2025.", "Read from Certificate.pdf, page 1",
            EntityType: nameof(McaFilingDocument), EntityId: 77, DocumentId: 77, PageNumber: 1, DocumentName: "Certificate.pdf");

        var result = ChatCompletionService.Validate("""{ "answer": "It was renamed on 11 Apr 2025.", "citedTags": ["F1"], "insufficientEvidence": false }""", [source]);

        var citation = Assert.Single(result.CitedSources);
        Assert.Equal(("StructuredFact", 77L, 1, "Certificate.pdf"), (citation.SourceType, citation.DocumentId!.Value, citation.PageNumber!.Value, citation.DocumentName));
    }
}
