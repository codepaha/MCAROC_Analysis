using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#359: a client refresh re-imports the whole MCA export as a new batch of the same request. PDFs this request
/// already processed in an earlier batch reuse their extracted text, their filing's Gemini extraction (only when the
/// filing's documents are exactly the same) and their embeddings — only new or changed work is paid for. Real SQL
/// Server; the text extractor, Gemini and the embedding model are counting fakes.</summary>
public sealed class McaRefreshReuseTests : IAsyncLifetime, IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "mca-refresh-reuse-" + Guid.NewGuid().ToString("N"));

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>"Extracts" text derived from the file name, so a test can tell extraction from reuse.</summary>
    private sealed class CountingExtractor() : PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance, "")
    {
        public readonly List<string> Extracted = [];

        public override Task<PdfExtractionResult> ExtractAsync(string pdfPath, string tempDir, CancellationToken ct)
        {
            Extracted.Add(Path.GetFileName(pdfPath));
            return Task.FromResult(new PdfExtractionResult($"--- Page 1 (native) ---\nText of {Path.GetFileName(pdfPath)}. Form CHG-1 particulars of charge.",
                1, 1, 0, TextExtractionMethod.Native, FilingDocumentProcessingStatus.TextExtracted, null));
        }
    }

    private sealed class CountingGemini : VertexAiExtractionService
    {
        public readonly List<string> Called = [];

        public override Task<ExtractionOutcome> ExtractAsync(string srn, FilingCategory category, string? dominantFormType,
            IReadOnlyList<FilingDocumentContext> documents, CancellationToken ct)
        {
            Called.Add(srn);
            return Task.FromResult(new ExtractionOutcome(SchemaNameFor(category, dominantFormType), $$"""{"srn":"{{srn}}","documents":{{documents.Count}}}""", "raw",
                ExtractionValidationStatus.Valid, null, ExtractionStatus.Success, null));
        }
    }

    private FilingBatchProcessor Processor(AppDbContext db, CountingExtractor extractor, CountingGemini gemini) =>
        new(db, _contentRoot, extractor, gemini, new FilingProcessingQueue(), new DocumentChunkingQueue(), NullLogger<FilingBatchProcessor>.Instance);

    private static async Task<long> SeedRequestAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1, EntityType = EntityType.Company, CompanyName = "Refresh Co", RequestNumber = $"RFR-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    /// <summary>A batch with the given filings (SRN → documents as (file name, hash)), every document Discovered — the
    /// state unpacking leaves them in.</summary>
    private static async Task<long> SeedBatchAsync(AppDbContext db, long requestId, params (string Srn, (string Name, string Hash)[] Docs)[] filings)
    {
        var batch = new McaFilingBatch { RequestId = requestId, SourceDocumentId = 0, Status = FilingBatchStatus.Processing, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        foreach (var (srn, docs) in filings)
        {
            var filing = new McaFiling
            {
                BatchId = batch.BatchId, RequestId = requestId, Srn = srn, NestedZipName = srn + ".zip",
                OuterCategoryFolder = "Charge Documents", IdentityMatchesRequest = true
            };
            db.McaFilings.Add(filing);
            await db.SaveChangesAsync();
            foreach (var (name, hash) in docs)
                db.McaFilingDocuments.Add(new McaFilingDocument
                {
                    FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = requestId, OriginalFileName = name,
                    SourceFolder = "", StoragePath = name, FileHash = hash, ProcessingStatus = FilingDocumentProcessingStatus.Discovered,
                    UpdatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }
        return batch.BatchId;
    }

    /// <summary>Runs every document of the batch, then every filing's extraction — what the worker does.</summary>
    private async Task ProcessBatchAsync(long batchId, CountingExtractor extractor, CountingGemini gemini)
    {
        await using var db = CreateContext();
        var processor = Processor(db, extractor, gemini);
        foreach (var id in await db.McaFilingDocuments.Where(d => d.BatchId == batchId).Select(d => d.FilingDocumentId).ToListAsync())
            await processor.ProcessDocumentAsync(id, CancellationToken.None);
        foreach (var id in await db.McaFilings.Where(f => f.BatchId == batchId).Select(f => f.FilingId).ToListAsync())
            await processor.ExtractFilingAsync(id, CancellationToken.None);
    }

    private static string H() => Guid.NewGuid().ToString("N").ToUpperInvariant();

    [Fact]
    public async Task A_refresh_batch_reuses_unchanged_documents_and_filings_and_only_pays_for_new_ones()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        var (a, b, c) = (H(), H(), H());
        var first = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a), ("Form CHG-1 b.pdf", b)]));
        var extractor = new CountingExtractor();
        var gemini = new CountingGemini();
        await ProcessBatchAsync(first, extractor, gemini);
        Assert.Equal(2, extractor.Extracted.Count);
        Assert.Equal(["SRN-X"], gemini.Called);

        // The refresh: the same filing unchanged, plus a new filing.
        var second = await SeedBatchAsync(db, requestId,
            ("SRN-X", [("Form CHG-1 a.pdf", a), ("Form CHG-1 b.pdf", b)]),
            ("SRN-Y", [("Form CHG-1 c.pdf", c)]));
        extractor.Extracted.Clear();
        gemini.Called.Clear();
        await ProcessBatchAsync(second, extractor, gemini);

        Assert.Equal(["Form CHG-1 c.pdf"], extractor.Extracted); // only the new PDF is extracted
        Assert.Equal(["SRN-Y"], gemini.Called);                  // only the new filing costs a Gemini call

        await using var verify = CreateContext();
        var firstDocs = await verify.McaFilingDocuments.AsNoTracking().Where(d => d.BatchId == first).ToDictionaryAsync(d => d.FileHash);
        var secondDocs = await verify.McaFilingDocuments.AsNoTracking().Where(d => d.BatchId == second).ToDictionaryAsync(d => d.FileHash);
        Assert.Equal(firstDocs[a].FilingDocumentId, secondDocs[a].ReusedFromDocumentId);
        Assert.Equal(firstDocs[b].FilingDocumentId, secondDocs[b].ReusedFromDocumentId);
        Assert.Null(secondDocs[c].ReusedFromDocumentId);
        Assert.All(secondDocs.Values, d => Assert.Equal(FilingDocumentProcessingStatus.Completed, d.ProcessingStatus));
        Assert.Equal(FilingCategory.Charge, secondDocs[a].Category); // classification still ran on the copied text
        Assert.NotEqual(firstDocs[a].ExtractedTextPath, secondDocs[a].ExtractedTextPath); // the new batch has its own text file
        Assert.Equal(await File.ReadAllTextAsync(firstDocs[a].ExtractedTextPath!), await File.ReadAllTextAsync(secondDocs[a].ExtractedTextPath!));

        var extractions = await (from e in verify.McaFilingExtractions.AsNoTracking()
                                 join f in verify.McaFilings on e.FilingId equals f.FilingId
                                 select new { f.BatchId, f.Srn, e.ExtractionId, e.ReusedFromExtractionId, e.ExtractedJson, e.Status })
            .Where(x => x.BatchId == first || x.BatchId == second).ToListAsync();
        var original = extractions.Single(x => x.BatchId == first);
        var copied = extractions.Single(x => x.BatchId == second && x.Srn == "SRN-X");
        Assert.Equal(original.ExtractionId, copied.ReusedFromExtractionId);
        Assert.Equal(original.ExtractedJson, copied.ExtractedJson);
        Assert.Null(extractions.Single(x => x.Srn == "SRN-Y").ReusedFromExtractionId);
        Assert.All(secondDocs.Values, d => Assert.Equal(AiExtractionStatus.Success, d.AiExtractionStatus));

        // A third refresh still points at the original call and the original extraction, never a copy.
        var third = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a), ("Form CHG-1 b.pdf", b)]));
        await ProcessBatchAsync(third, extractor, gemini);
        Assert.Equal(firstDocs[a].FilingDocumentId,
            (await verify.McaFilingDocuments.AsNoTracking().SingleAsync(d => d.BatchId == third && d.FileHash == a)).ReusedFromDocumentId);
        Assert.Equal(original.ExtractionId, await (from e in verify.McaFilingExtractions join f in verify.McaFilings on e.FilingId equals f.FilingId
                                                   where f.BatchId == third select e.ReusedFromExtractionId).SingleAsync());
    }

    [Fact]
    public async Task A_filing_whose_documents_changed_gets_a_fresh_Gemini_call()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        var (a, b) = (H(), H());
        var first = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]));
        var gemini = new CountingGemini();
        await ProcessBatchAsync(first, new CountingExtractor(), gemini);

        // The refresh added an attachment to the same filing.
        var second = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a), ("Form CHG-1 b.pdf", b)]));
        gemini.Called.Clear();
        var extractor = new CountingExtractor();
        await ProcessBatchAsync(second, extractor, gemini);

        Assert.Equal(["SRN-X"], gemini.Called);
        Assert.Equal(["Form CHG-1 b.pdf"], extractor.Extracted); // the unchanged PDF's text is still reused
        await using var verify = CreateContext();
        Assert.Null(await (from e in verify.McaFilingExtractions join f in verify.McaFilings on e.FilingId equals f.FilingId
                           where f.BatchId == second select e.ReusedFromExtractionId).SingleAsync());
    }

    /// <summary>PR #363 review: the same documents at the same versions, but extracted under a different schema than the
    /// one this filing's recomputed classification selects now — category-specific fields must not carry over.</summary>
    [Fact]
    public async Task An_extraction_made_under_a_different_schema_is_never_reused()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        var a = H();
        var first = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]));
        await ProcessBatchAsync(first, new CountingExtractor(), new CountingGemini());
        await using (var tamper = CreateContext())
            await tamper.McaFilingExtractions.Where(e => tamper.McaFilings.Any(f => f.FilingId == e.FilingId && f.BatchId == first))
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.SchemaName, FilingSchemaNames.Constitutional));

        var second = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]));
        var gemini = new CountingGemini();
        await ProcessBatchAsync(second, new CountingExtractor(), gemini);

        Assert.Equal(["SRN-X"], gemini.Called);
        await using var verify = CreateContext();
        var extraction = await (from e in verify.McaFilingExtractions join f in verify.McaFilings on e.FilingId equals f.FilingId
                                where f.BatchId == second select e).SingleAsync();
        Assert.Null(extraction.ReusedFromExtractionId);
        Assert.Equal(FilingSchemaNames.Charge, extraction.SchemaName);
    }

    [Fact]
    public async Task Failures_older_versions_and_missing_text_are_never_reused()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        var (a, b) = (H(), H());
        var first = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]), ("SRN-Z", [("Form CHG-1 b.pdf", b)]));
        await ProcessBatchAsync(first, new CountingExtractor(), new CountingGemini());

        await using (var tamper = CreateContext())
        {
            // b's extracted text is gone (e.g. retired), and SRN-X's extraction was made with an older prompt.
            var bText = await tamper.McaFilingDocuments.Where(d => d.BatchId == first && d.FileHash == b).Select(d => d.ExtractedTextPath).SingleAsync();
            File.Delete(bText!);
            await tamper.McaFilingExtractions.Where(e => tamper.McaFilings.Any(f => f.FilingId == e.FilingId && f.BatchId == first && f.Srn == "SRN-X"))
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.PromptVersion, "0.9"));
            // ...and SRN-Z's extraction failed.
            await tamper.McaFilingExtractions.Where(e => tamper.McaFilings.Any(f => f.FilingId == e.FilingId && f.BatchId == first && f.Srn == "SRN-Z"))
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, ExtractionStatus.Failed));
        }

        var second = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]), ("SRN-Z", [("Form CHG-1 b.pdf", b)]));
        var extractor = new CountingExtractor();
        var gemini = new CountingGemini();
        await ProcessBatchAsync(second, extractor, gemini);

        Assert.Equal(["Form CHG-1 b.pdf"], extractor.Extracted); // missing text → extracted afresh; a's text still reused
        Assert.Equal(["SRN-X", "SRN-Z"], gemini.Called.Order());   // older prompt and failed call → both fresh
    }

    [Fact]
    public async Task A_document_whose_extraction_failed_earlier_is_extracted_again()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        var a = H();
        var first = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]));
        await ProcessBatchAsync(first, new CountingExtractor(), new CountingGemini());
        await using (var tamper = CreateContext())
            await tamper.McaFilingDocuments.Where(d => d.BatchId == first).ExecuteUpdateAsync(u => u.SetProperty(d => d.ProcessingStatus, FilingDocumentProcessingStatus.Failed));

        var second = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]));
        var extractor = new CountingExtractor();
        await ProcessBatchAsync(second, extractor, new CountingGemini());

        Assert.Equal(["Form CHG-1 a.pdf"], extractor.Extracted);
    }

    private sealed class CountingEmbeddings : EmbeddingService
    {
        public int Calls;

        public override Task<List<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(texts.Select((_, i) => { var v = new float[Dimensions]; v[i % Dimensions] = 1f; return v; }).ToList());
        }
    }

    [Fact]
    public async Task A_refreshed_copy_of_an_already_chunked_pdf_reuses_its_embeddings()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        var (a, c) = (H(), H());
        var first = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]));
        await ProcessBatchAsync(first, new CountingExtractor(), new CountingGemini());
        var second = await SeedBatchAsync(db, requestId, ("SRN-X", [("Form CHG-1 a.pdf", a)]), ("SRN-Y", [("Form CHG-1 c.pdf", c)]));
        await ProcessBatchAsync(second, new CountingExtractor(), new CountingGemini());

        var embeddings = new CountingEmbeddings();
        await using var chunkDb = CreateContext(); // a worker's own context, as in production
        var chunker = new DocumentChunkingOrchestrator(chunkDb, embeddings, new DocumentChunkingQueue(), NullLogger<DocumentChunkingOrchestrator>.Instance);
        foreach (var id in await chunker.GetPendingDocumentIdsAsync(first, CancellationToken.None))
            await chunker.ChunkDocumentAsync(id, CancellationToken.None);
        Assert.Equal(1, embeddings.Calls);

        foreach (var id in await chunker.GetPendingDocumentIdsAsync(second, CancellationToken.None))
            await chunker.ChunkDocumentAsync(id, CancellationToken.None);
        Assert.Equal(2, embeddings.Calls); // only the new PDF c was embedded

        await using var verify = CreateContext();
        var firstChunks = await verify.DocumentChunks.AsNoTracking().Where(x => x.BatchId == first).OrderBy(x => x.ChunkIndex).ToListAsync();
        var reused = await verify.DocumentChunks.AsNoTracking()
            .Where(x => x.BatchId == second && verify.McaFilingDocuments.Any(d => d.FilingDocumentId == x.FilingDocumentId && d.FileHash == a))
            .OrderBy(x => x.ChunkIndex).ToListAsync();
        Assert.Equal(firstChunks.Count, reused.Count);
        Assert.Equal(firstChunks.Select(x => x.Embedding.Memory.ToArray()), reused.Select(x => x.Embedding.Memory.ToArray()));
        Assert.All(reused, x => Assert.Equal(second, x.BatchId)); // stamped as the new batch's own chunks
    }
}
