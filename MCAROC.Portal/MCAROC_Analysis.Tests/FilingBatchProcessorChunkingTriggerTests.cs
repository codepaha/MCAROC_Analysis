using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Tests;

/// <summary>Proves the throughput fix: chunking/embedding for a batch is triggered the moment ONE document
/// finishes OCR/classification, not gated on every document in the batch reaching a terminal state.
/// Before this fix, DocumentChunkingQueue was only ever enqueued from FilingBatchProcessor's
/// MaybeCompleteBatchAsync — meaning "Ask Documents" stayed empty for a large archive until its single
/// slowest document (often the biggest scanned-PDF OCR job) finally finished, even though most documents
/// completed minutes earlier. Uses a real PdfTextExtractor against a real (QuestPDF-generated, so no
/// Tesseract/OCR dependency — it has a native text layer) PDF rather than mocking extraction, since the
/// trigger sits inside ProcessDocumentAsync's own success path.</summary>
public class FilingBatchProcessorChunkingTriggerTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "chunking-trigger-tests-" + Guid.NewGuid().ToString("N"));

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    /// <summary>A real, parseable single-page PDF with a genuine text layer — extractable natively via
    /// PdfPig, well over MinCharsPerPageForNativeText, so this test never touches the Tesseract OCR
    /// fallback path at all.</summary>
    private string WriteExtractablePdf(string path, string bodyText)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        MCAROC_Analysis.Services.Dossier.DossierFonts.Register(WebRoot());

        var bytes = Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.Content().Text(bodyText).FontSize(11);
            });
        }).GeneratePdf();

        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task<(McaRequest Request, McaFilingBatch Batch, McaFiling Filing)> SeedBatchAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "CHK" + Guid.NewGuid().ToString("N")[..7], ClientName = "Chunk Trigger Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Chunk Trigger Co",
            RequestNumber = $"CHK-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        var outerDoc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip",
            StoredFileName = "f.zip", StoragePath = Path.Combine(_tempDir, "f.zip"), FileHash = "h", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(outerDoc);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = outerDoc.DocumentId, Status = FilingBatchStatus.Processing, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "1", NestedZipName = "1.zip", OuterCategoryFolder = "Compliance Documents" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        return (request, batch, filing);
    }

    private static FilingBatchProcessor NewProcessor(AppDbContext db, string contentRootPath, DocumentChunkingQueue chunkingQueue) =>
        new(db, contentRootPath,
            new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance, tesseractExePath: @"C:\not-installed\tesseract.exe"),
            null, new FilingProcessingQueue(), chunkingQueue, NullLogger<FilingBatchProcessor>.Instance);

    [Fact]
    public async Task Finishing_one_document_enqueues_chunking_while_a_sibling_document_is_still_unprocessed()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedBatchAsync(db);

        var pdf1Path = WriteExtractablePdf(Path.Combine(_tempDir, "doc1.pdf"),
            "Form MGT-14 filed by Chunk Trigger Co under section 179(3) of the Companies Act, board resolution attached.");
        var pdf2Path = WriteExtractablePdf(Path.Combine(_tempDir, "doc2.pdf"),
            "Form ADT-1 notice of appointment of auditor for Chunk Trigger Co for the financial year.");

        var doc1 = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "doc1.pdf", SourceFolder = "", StoragePath = pdf1Path, FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Discovered, UpdatedAt = DateTime.UtcNow
        };
        var doc2 = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "doc2.pdf", SourceFolder = "", StoragePath = pdf2Path, FileHash = "h2",
            ProcessingStatus = FilingDocumentProcessingStatus.Discovered, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.AddRange(doc1, doc2);
        await db.SaveChangesAsync();

        var chunkingQueue = new DocumentChunkingQueue();
        var processor = NewProcessor(db, _tempDir, chunkingQueue);

        // Process ONLY doc1 — doc2 stays Discovered, exactly the "large batch, one document still mid-OCR"
        // shape this fix targets.
        await processor.ProcessDocumentAsync(doc1.FilingDocumentId, CancellationToken.None);

        var updatedDoc1 = await db.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == doc1.FilingDocumentId);
        Assert.Equal(FilingDocumentProcessingStatus.Completed, updatedDoc1.ProcessingStatus);
        var updatedDoc2 = await db.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == doc2.FilingDocumentId);
        Assert.Equal(FilingDocumentProcessingStatus.Discovered, updatedDoc2.ProcessingStatus); // still untouched

        // The chunking queue must already have this batch queued — not waiting for doc2.
        long queuedBatchId = 0;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await foreach (var id in chunkingQueue.ReadAllAsync(cts.Token))
            {
                queuedBatchId = id;
                break;
            }
        }
        Assert.Equal(batch.BatchId, queuedBatchId);

        // And the orchestrator's own pending-document query — what the chunking worker actually acts on —
        // already exposes doc1 alone, confirming the batch didn't need to be fully done for this to work.
        var orchestrator = new DocumentChunkingOrchestrator(db, embeddingService: null!, chunkingQueue, NullLogger<DocumentChunkingOrchestrator>.Instance);
        var pending = await orchestrator.GetPendingDocumentIdsAsync(batch.BatchId, CancellationToken.None);
        Assert.Equal([doc1.FilingDocumentId], pending);
    }

    [Fact]
    public async Task A_document_that_fails_extraction_does_not_enqueue_chunking()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedBatchAsync(db);

        var doc = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "missing.pdf", SourceFolder = "", StoragePath = Path.Combine(_tempDir, "does-not-exist.pdf"), FileHash = "h3",
            ProcessingStatus = FilingDocumentProcessingStatus.Discovered, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var chunkingQueue = new DocumentChunkingQueue();
        var processor = NewProcessor(db, _tempDir, chunkingQueue);

        await processor.ProcessDocumentAsync(doc.FilingDocumentId, CancellationToken.None);

        var updated = await db.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.FilingDocumentId == doc.FilingDocumentId);
        Assert.NotEqual(FilingDocumentProcessingStatus.Completed, updated.ProcessingStatus);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in chunkingQueue.ReadAllAsync(cts.Token)) { }
        });
    }
}
