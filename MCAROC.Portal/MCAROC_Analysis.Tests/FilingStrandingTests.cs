using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Nothing may leave a filing's documents half-done, because a request's filings never finish and nobody is there to nudge them
/// (owner, 2026-10-13: no manual intervention). Both cases were found in the Altigreen test: a restart during OCR left two documents
/// TextExtracting for good, and a model reply shaped as an array threw past the AI claim and left four documents InProgress.</summary>
public class FilingStrandingTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class ThrowingExtraction : VertexAiExtractionService
    {
        public override Task<ExtractionOutcome> ExtractAsync(string srn, FilingCategory category, string? dominantFormType,
            IReadOnlyList<FilingDocumentContext> documents, CancellationToken ct) =>
            throw new InvalidOperationException("The requested operation requires an element of type 'Object', but the target element has type 'Array'.");
    }

    private static async Task<(McaFilingBatch Batch, McaFiling Filing)> SeedFilingAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = $"FS{Guid.NewGuid():N}"[..10], ClientName = "Stranding Test Client", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Stranding Test Co",
            RequestNumber = $"FS-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        var source = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip",
            StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(source);
        await db.SaveChangesAsync();
        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = source.DocumentId, Status = FilingBatchStatus.Processing, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "S" + Guid.NewGuid().ToString("N")[..8], NestedZipName = "1.zip", OuterCategoryFolder = "Charge" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();
        return (batch, filing);
    }

    private static FilingBatchProcessor Processor(AppDbContext db, FilingProcessingQueue queue, VertexAiExtractionService? ai = null) =>
        new(db, Path.GetTempPath(), null, ai, queue, new DocumentChunkingQueue(), NullLogger<FilingBatchProcessor>.Instance);

    [Fact]
    public async Task A_document_claimed_by_the_previous_process_is_processed_again_after_a_restart()
    {
        await using var db = CreateContext();
        var (batch, filing) = await SeedFilingAsync(db);
        var document = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = batch.RequestId, OriginalFileName = "a.pdf",
            StoragePath = @"C:\fake\a.pdf", FileHash = Guid.NewGuid().ToString("N"),
            ProcessingStatus = FilingDocumentProcessingStatus.TextExtracting, ProcessingStartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(document);
        await db.SaveChangesAsync();

        var queue = new FilingProcessingQueue();
        await Processor(db, queue).RecoverStaleWorkAsync(default);

        await using var verify = CreateContext();
        // Discovered again, so ProcessDocumentAsync's claim (Discovered only) takes it; left TextExtracting, the re-enqueue did nothing.
        Assert.Equal(FilingDocumentProcessingStatus.Discovered,
            (await verify.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == document.FilingDocumentId)).ProcessingStatus);
        var queued = new List<long>();
        while (queue.Count(FilingWorkKind.Document) > 0)
            queued.Add(((ProcessDocumentWorkItem)await queue.DequeueAsync(FilingWorkKind.Document, default)).FilingDocumentId);
        Assert.Contains(document.FilingDocumentId, queued);
    }

    [Fact]
    public async Task An_extraction_that_throws_fails_the_filing_instead_of_stranding_its_documents()
    {
        await using var db = CreateContext();
        var (batch, filing) = await SeedFilingAsync(db);
        var textPath = Path.Combine(Path.GetTempPath(), $"stranding-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(textPath, "Form CHG-1 particulars of charge");
        try
        {
            var document = new McaFilingDocument
            {
                FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = batch.RequestId, OriginalFileName = "chg1.pdf",
                StoragePath = @"C:\fake\chg1.pdf", FileHash = Guid.NewGuid().ToString("N"), Category = FilingCategory.Charge, FormType = "CHG-1",
                ProcessingStatus = FilingDocumentProcessingStatus.Completed, AiExtractionStatus = AiExtractionStatus.Pending,
                ExtractedTextPath = textPath, UpdatedAt = DateTime.UtcNow
            };
            db.McaFilingDocuments.Add(document);
            await db.SaveChangesAsync();

            await Processor(db, new FilingProcessingQueue(), new ThrowingExtraction()).ExtractFilingAsync(filing.FilingId, default);

            await using var verify = CreateContext();
            Assert.Equal(AiExtractionStatus.Failed,
                (await verify.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == document.FilingDocumentId)).AiExtractionStatus);
            var extraction = await verify.McaFilingExtractions.AsNoTracking().SingleAsync(e => e.FilingId == filing.FilingId);
            Assert.Equal(ExtractionStatus.Failed, extraction.Status);
        }
        finally
        {
            File.Delete(textPath);
        }
    }

    [Fact]
    public void A_reply_wrapped_in_a_one_element_array_is_read_as_its_object()
    {
        const string raw = """[{"chargeAmount":{"value":"1000000"}}]""";
        var outcome = VertexAiExtractionService.Validate("charge", raw);
        Assert.Equal(ExtractionStatus.Success, outcome.Status);
        Assert.Equal("""{"chargeAmount":{"value":"1000000"}}""", outcome.ExtractedJson);
        Assert.Equal(raw, outcome.RawModelResponse);
    }

    [Theory]
    [InlineData("""[{"a":{"value":1}},{"b":{"value":2}}]""")]
    [InlineData("""["just text"]""")]
    [InlineData("42")]
    public void Any_other_shape_is_a_failed_extraction_not_an_exception(string raw)
    {
        var outcome = VertexAiExtractionService.Validate("charge", raw);
        Assert.Equal(ExtractionStatus.Failed, outcome.Status);
        Assert.Equal(ExtractionValidationStatus.Invalid, outcome.ValidationStatus);
        Assert.Null(outcome.ExtractedJson);
    }
}
