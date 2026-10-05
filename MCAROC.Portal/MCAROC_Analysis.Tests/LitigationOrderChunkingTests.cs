using System.Data.Common;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers #244/LIT-04: LitigationOrderChunkingOrchestrator's claim/chunk/embed/persist lifecycle,
/// failure/retry/terminal behavior, startup recovery, and LitigationDocumentRetriever's request-scoping
/// guarantee. Mirrors ChunkingObservabilityAndRetryTests'/EmbeddingCountValidationTests' shapes closely on
/// purpose — same real .\SQLEXPRESS test DB, same stub-EmbeddingService-by-subclassing approach (EmbeddingService's
/// Embed* methods are virtual and it has a protected parameterless ctor specifically for this).</summary>
public class LitigationOrderChunkingTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static AppDbContext CreateContextWithInterceptor(DbCommandInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).AddInterceptors(interceptor).Options);

    /// <summary>Genuine ADO.NET-level interleaving hook, zero production code changes — same idiom as
    /// CalculationDiscrepancyWorkflowServiceTests' ThrowOnTableWriteInterceptor, but runs a side effect and
    /// lets the command proceed instead of throwing. Fires exactly once, the instant the first command
    /// matching (tableName, "UPDATE") is about to execute — for RecoverStaleWorkAsync that is its one guarded
    /// reset UPDATE, which only ever runs strictly after its own read (a SELECT, never matched here) has
    /// already completed. Running the concurrent reclaim from here lands it deterministically in that exact
    /// window, rather than relying on unpredictable real-world async scheduling.</summary>
    private sealed class RunOnceBeforeFirstUpdateInterceptor(string tableName, Action onFirstUpdate) : DbCommandInterceptor
    {
        private bool _fired;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired && command.CommandText.Contains(tableName, StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                _fired = true;
                onFirstUpdate();
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class StubEmbeddingService(Func<int, List<float[]>> map, float[]? fixedQueryVector = null) : EmbeddingService
    {
        public override Task<List<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult(map(texts.Count));

        // A fixed query vector (when supplied) guarantees a predictable, non-flaky cosine distance against the
        // document embeddings a test asserts on — see DocumentRetrieverTests' own unit-basis-vector approach
        // for the same reasoning. Two independent random vectors in 768 dims have an expected cosine distance
        // right around ChatRetrievalOptions.Default's MaxCosineDistance threshold, which makes a test that
        // relies on both being independently random a coin flip.
        public override Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) =>
            Task.FromResult(fixedQueryVector ?? DeterministicVector(text));
    }

    private static float[] Axis(int dim, float sign = 1f)
    {
        var v = new float[EmbeddingService.Dimensions];
        v[dim] = sign;
        return v;
    }

    /// <summary>A fixed, deterministic 768-dim vector derived from the text's hash — good enough to exercise
    /// VECTOR_DISTANCE ordering/filtering without needing a real embedding model in tests. Two identical inputs
    /// always produce identical vectors; different inputs produce different ones (with high probability).</summary>
    private static float[] DeterministicVector(string seedText)
    {
        var seed = seedText.GetHashCode();
        var rnd = new Random(seed);
        var v = new float[EmbeddingService.Dimensions];
        for (var i = 0; i < v.Length; i++) v[i] = (float)(rnd.NextDouble() * 2 - 1);
        return EmbeddingService.Normalize(v.Select(f => (double)f).ToList());
    }

    private static async Task<(McaRequest Request, LitigationCase Case, LitigationCaseOrder Order)> SeedOrderAsync(
        AppDbContext db, string suffix, long? requestIdOverride = null)
    {
        McaRequest request;
        if (requestIdOverride is { } existingId)
        {
            request = await db.Requests.FirstAsync(r => r.RequestId == existingId);
        }
        else
        {
            var client = new Client { ClientCode = "LOC" + suffix + Guid.NewGuid().ToString("N")[..6], ClientName = "Chunk Test Co", CreatedDate = DateTime.UtcNow };
            db.Clients.Add(client);
            request = new McaRequest
            {
                Client = client, EntityType = EntityType.Company, CompanyName = "Litigation Chunk Test Co",
                Cin = "U45203OR1995PLC003982", RequestNumber = $"LOC-{suffix}-{Guid.NewGuid():N}",
                RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
        }

        var caseRow = new LitigationCase
        {
            RequestId = request.RequestId, Cnr = "TNKP07000" + suffix + Guid.NewGuid().ToString("N")[..4],
            CaseNumber = "OS/123/2025/" + suffix, Court = "District Court, Chennai", ProceedingType = "OS",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(caseRow);
        await db.SaveChangesAsync();

        var order = new LitigationCaseOrder
        {
            Case = caseRow, PdfUrl = $"https://bpr.example/orders/{suffix}.pdf", OrderDate = "09-01-2025", OrderType = "Judgment", CreatedUtc = DateTime.UtcNow
        };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (request, caseRow, order);
    }

    private static async Task<LitigationOrderDocument> SeedDownloadedDocumentAsync(
        AppDbContext db, long litigationCaseOrderId, string extractedText, string? fileHash = null)
    {
        var document = new LitigationOrderDocument
        {
            LitigationCaseOrderId = litigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5),
            ExtractedText = extractedText,
            FileHash = fileHash,
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            TextExtractionMethod = TextExtractionMethod.Native,
            ChunkingStatus = ChunkingStatus.Pending,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    // ── Happy path ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChunkOrderDocumentAsync_chunks_embeds_and_denormalizes_case_and_order_fields()
    {
        await using var db = CreateContext();
        var (request, litigationCase, order) = await SeedOrderAsync(db, "H1");
        var text = $"--- Page 1 (native) ---\n{new string('a', 1500)}\n\n--- Page 2 (native) ---\n{new string('b', 1500)}";
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId, text);

        var stub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => new float[EmbeddingService.Dimensions]).ToList());
        var queue = new LitigationOrderChunkingQueue();
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var verify = CreateContext();
        var reloadedDoc = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(ChunkingStatus.Chunked, reloadedDoc.ChunkingStatus);
        Assert.Equal(0, reloadedDoc.ChunkRetryCount);
        Assert.Null(reloadedDoc.ChunkingLastError);

        var chunks = await verify.LitigationOrderChunks.AsNoTracking()
            .Where(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
            .OrderBy(c => c.ChunkIndex).ToListAsync();
        Assert.True(chunks.Count >= 2); // one chunk per page at minimum given the >MaxChars split
        foreach (var chunk in chunks)
        {
            Assert.Equal(request.RequestId, chunk.RequestId);
            Assert.Equal(order.LitigationCaseOrderId, chunk.LitigationCaseOrderId);
            Assert.Equal(litigationCase.LitigationCaseId, chunk.LitigationCaseId);
            Assert.Equal(litigationCase.CaseNumber, chunk.CaseNumber);
            Assert.Equal(litigationCase.Cnr, chunk.Cnr);
            Assert.Equal(litigationCase.Court, chunk.Court);
            Assert.Equal(order.OrderType, chunk.OrderType);
            Assert.Equal(order.OrderDate, chunk.OrderDate);
            Assert.True(chunk.PageNumber is 1 or 2);
            Assert.Equal(EmbeddingService.ModelId, chunk.EmbeddingModel);
            Assert.Equal(EmbeddingService.Dimensions, chunk.EmbeddingDimensions);
            Assert.Equal(LitigationOrderChunkingOrchestrator.ChunkingVersion, chunk.ChunkingVersion);
            Assert.StartsWith($"[Court: {litigationCase.Court} | Case: {litigationCase.CaseNumber} | CNR: {litigationCase.Cnr} | Order: {order.OrderType} {order.OrderDate} | Page {chunk.PageNumber}]\n\n", chunk.ChunkText);
        }
    }

    // ── #350: identical order sheets reuse embeddings ──────────────────────────────────────────────

    [Fact]
    public async Task ChunkOrderDocumentAsync_reuses_embeddings_of_the_same_pdf_under_another_order_of_the_request()
    {
        await using var db = CreateContext();
        var (request, parentCase, parentOrder) = await SeedOrderAsync(db, "D1");
        var (_, iaCase, iaOrder) = await SeedOrderAsync(db, "D2", request.RequestId);
        // An IA filed under the parent proceeding shares the same court, case number, and order metadata
        iaCase.ProceedingType = "IA";
        iaCase.CaseNumber = parentCase.CaseNumber;
        iaCase.Court = parentCase.Court;
        iaCase.Cnr = parentCase.Cnr;
        iaOrder.OrderType = parentOrder.OrderType;
        iaOrder.OrderDate = parentOrder.OrderDate;
        await db.SaveChangesAsync();

        var text = $"--- Page 1 (native) ---\n{new string('a', 1500)}\n\n--- Page 2 (native) ---\nOrder: stay granted.";
        var hash = Guid.NewGuid().ToString("N");
        var parentDoc = await SeedDownloadedDocumentAsync(db, parentOrder.LitigationCaseOrderId, text, hash);
        var iaDoc = await SeedDownloadedDocumentAsync(db, iaOrder.LitigationCaseOrderId, text, hash);

        var embedCalls = 0;
        var stub = new StubEmbeddingService(n => { embedCalls++; return Enumerable.Range(0, n).Select(i => Axis(i + 1)).ToList(); });
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(parentDoc.LitigationOrderDocumentId, CancellationToken.None);
        Assert.Equal(1, embedCalls);
        await orchestrator.ChunkOrderDocumentAsync(iaDoc.LitigationOrderDocumentId, CancellationToken.None);
        Assert.Equal(1, embedCalls); // the IA's copy of the sheet cost nothing

        await using var verify = CreateContext();
        Assert.Equal(ChunkingStatus.Chunked, (await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == iaDoc.LitigationOrderDocumentId)).ChunkingStatus);
        var parentChunks = await verify.LitigationOrderChunks.AsNoTracking().Where(c => c.LitigationOrderDocumentId == parentDoc.LitigationOrderDocumentId).OrderBy(c => c.ChunkIndex).ToListAsync();
        var iaChunks = await verify.LitigationOrderChunks.AsNoTracking().Where(c => c.LitigationOrderDocumentId == iaDoc.LitigationOrderDocumentId).OrderBy(c => c.ChunkIndex).ToListAsync();
        Assert.Equal(parentChunks.Count, iaChunks.Count);
        for (var i = 0; i < iaChunks.Count; i++)
        {
            Assert.Equal(parentChunks[i].ChunkText, iaChunks[i].ChunkText);
            Assert.Equal(parentChunks[i].Embedding.Memory.ToArray(), iaChunks[i].Embedding.Memory.ToArray());
            // Labelled as the IA's own order, not the parent's.
            Assert.Equal(iaCase.LitigationCaseId, iaChunks[i].LitigationCaseId);
            Assert.Equal(iaOrder.LitigationCaseOrderId, iaChunks[i].LitigationCaseOrderId);
            Assert.Equal(iaCase.CaseNumber, iaChunks[i].CaseNumber);
        }
    }

    [Fact]
    public async Task ChunkOrderDocumentAsync_embeds_afresh_when_context_headers_differ_even_with_same_pdf_text()
    {
        await using var db = CreateContext();
        var (request, caseA, orderA) = await SeedOrderAsync(db, "DH1");
        var (_, caseB, orderB) = await SeedOrderAsync(db, "DH2", request.RequestId);
        var text = "--- Page 1 (native) ---\nThe Tribunal ordered notice to the respondents returnable by four weeks.";
        var hash = Guid.NewGuid().ToString("N");
        var docA = await SeedDownloadedDocumentAsync(db, orderA.LitigationCaseOrderId, text, hash);
        var docB = await SeedDownloadedDocumentAsync(db, orderB.LitigationCaseOrderId, text, hash);

        var embedCalls = 0;
        var stub = new StubEmbeddingService(n => { embedCalls++; return Enumerable.Range(0, n).Select(i => Axis(i)).ToList(); });
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(docA.LitigationOrderDocumentId, CancellationToken.None);
        await orchestrator.ChunkOrderDocumentAsync(docB.LitigationOrderDocumentId, CancellationToken.None);

        Assert.Equal(2, embedCalls); // Distinct case numbers yield distinct context headers -> embedded afresh
    }

    [Fact]
    public async Task ChunkOrderDocumentAsync_embeds_afresh_when_the_same_pdf_belongs_to_another_request_or_the_text_differs()
    {
        await using var db = CreateContext();
        var (otherRequest, _, otherOrder) = await SeedOrderAsync(db, "E1");
        var (request, _, order) = await SeedOrderAsync(db, "E2");
        var (_, _, secondOrder) = await SeedOrderAsync(db, "E3", request.RequestId);
        var text = "--- Page 1 (native) ---\nThe company petition is dismissed for non-prosecution by the petitioner.";
        var hash = Guid.NewGuid().ToString("N");
        var otherDoc = await SeedDownloadedDocumentAsync(db, otherOrder.LitigationCaseOrderId, text, hash);
        var doc = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId, text, hash);
        // Same recorded hash but different extracted text (e.g. re-extracted with OCR) — never reused.
        var differentText = await SeedDownloadedDocumentAsync(db, secondOrder.LitigationCaseOrderId, text + " Costs of Rs. 5,000.", hash);

        var embedCalls = 0;
        var stub = new StubEmbeddingService(n => { embedCalls++; return Enumerable.Range(0, n).Select(_ => Axis(0)).ToList(); });
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(otherDoc.LitigationOrderDocumentId, CancellationToken.None);
        await orchestrator.ChunkOrderDocumentAsync(doc.LitigationOrderDocumentId, CancellationToken.None);
        await orchestrator.ChunkOrderDocumentAsync(differentText.LitigationOrderDocumentId, CancellationToken.None);

        Assert.Equal(3, embedCalls);
    }

    [Fact]
    public async Task ChunkOrderDocumentAsync_renews_the_lease_between_embedding_batches_for_a_multi_batch_document()
    {
        // Regression for PR #254 review round 2: EmbedDocumentsAsync loops sequential Vertex batches
        // internally, so a single call for a large document's full chunk set could legitimately run past a
        // fixed lease window purely due to volume, not a crash. This proves the fix — the lease's expiry
        // actually moves forward between the orchestrator's own per-batch embedding calls, not just that
        // batching happens to occur.
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "RN1");
        // 40 one-page chunks — well over the 32-per-call renewal batch size — forces two real embedding calls.
        var pages = string.Concat(Enumerable.Range(1, 40).Select(i =>
            $"--- Page {i} (native) ---\nOrder page {i} text, padded well past the fifty-character chunk minimum threshold.\n\n"));
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId, pages);

        var callCount = 0;
        DateTime? leaseExpiryAtFirstBatch = null;
        DateTime? leaseExpiryAtSecondBatch = null;
        var stub = new StubEmbeddingService(n =>
        {
            callCount++;
            // Read the lease's CURRENT expiry at the moment each batch call starts — the first read reflects
            // only the original claim (no renewal has run yet); the second read happens after the first
            // batch's own renewal already ran. If renewal never renewed anything, both reads would show the
            // exact same, unchanged expiry.
            using var readDb = CreateContext();
            var expiry = readDb.LitigationOrderDocuments.AsNoTracking()
                .First(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId).ChunkingLeaseExpiresUtc;
            if (callCount == 1) leaseExpiryAtFirstBatch = expiry;
            else if (callCount == 2) leaseExpiryAtSecondBatch = expiry;
            return Enumerable.Range(0, n).Select(_ => Axis(0)).ToList();
        });
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        Assert.Equal(2, callCount); // 40 chunks / 32-per-batch = 2 real embedding round-trips
        Assert.NotNull(leaseExpiryAtFirstBatch);
        Assert.NotNull(leaseExpiryAtSecondBatch);
        Assert.True(leaseExpiryAtSecondBatch > leaseExpiryAtFirstBatch); // renewal after batch 1 moved the expiry forward

        await using var verify = CreateContext();
        var reloaded = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(ChunkingStatus.Chunked, reloaded.ChunkingStatus);
        Assert.Equal(40, await verify.LitigationOrderChunks.CountAsync(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId));
    }

    [Fact]
    public async Task ChunkOrderDocumentAsync_a_second_claim_on_an_already_chunked_document_is_a_no_op()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "H2");
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nSome order text here, long enough to clear the fifty-character minimum chunk size.");

        var callCount = 0;
        var stub = new StubEmbeddingService(n =>
        {
            Interlocked.Increment(ref callCount);
            return Enumerable.Range(0, n).Select(_ => new float[EmbeddingService.Dimensions]).ToList();
        });
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);
        Assert.Equal(1, callCount);

        // Second call: ChunkingStatus is now Chunked, not Pending — the claim's WHERE clause matches 0 rows.
        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);
        Assert.Equal(1, callCount); // never re-embedded
    }

    // ── Failure / retry / terminal ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChunkOrderDocumentAsync_retries_twice_then_terminally_fails_on_persistent_embedding_mismatch()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "F1");
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nOrder text for the failure test, padded well past the fifty-character chunk minimum.");

        // Always returns one vector too few — every attempt fails with a VectorMismatch-classified error.
        var stub = new StubEmbeddingService(n => n > 0 ? Enumerable.Range(0, n - 1).Select(_ => new float[EmbeddingService.Dimensions]).ToList() : []);
        var queue = new LitigationOrderChunkingQueue();
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);
        await db.Entry(document).ReloadAsync();
        Assert.Equal(ChunkingStatus.Pending, document.ChunkingStatus);
        Assert.Equal(1, document.ChunkRetryCount);
        Assert.Null(document.ChunkingFailedUtc);
        Assert.NotNull(document.ChunkingLastError);

        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);
        await db.Entry(document).ReloadAsync();
        Assert.Equal(ChunkingStatus.Pending, document.ChunkingStatus);
        Assert.Equal(2, document.ChunkRetryCount);
        Assert.Null(document.ChunkingFailedUtc);

        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);
        await db.Entry(document).ReloadAsync();
        Assert.Equal(ChunkingStatus.Failed, document.ChunkingStatus); // terminal at MaxChunkRetryCount = 3
        Assert.Equal(3, document.ChunkRetryCount);
        Assert.NotNull(document.ChunkingFailedUtc);

        await using var verify = CreateContext();
        Assert.False(await verify.LitigationOrderChunks.AnyAsync(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId));
    }

    [Fact]
    public async Task ChunkOrderDocumentAsync_fails_retryably_without_persisting_when_extracted_text_is_missing()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "F2");
        // ExtractedText left null — a document that reached Downloaded+TextExtracted only via a malformed seed;
        // the orchestrator must fail cleanly, not throw an unhandled NRE trying to chunk null text.
        var document = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5), ExtractedText = null,
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted, ChunkingStatus = ChunkingStatus.Pending, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(document);
        await db.SaveChangesAsync();

        var stub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => new float[EmbeddingService.Dimensions]).ToList());
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await db.Entry(document).ReloadAsync();
        Assert.Equal(ChunkingStatus.Pending, document.ChunkingStatus); // retryable, retry count bumped
        Assert.Equal(1, document.ChunkRetryCount);
    }

    // ── Recovery ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverStaleWorkAsync_resets_only_expired_or_unleased_InProgress_rows_and_enqueues_never_enqueued_eligible_documents()
    {
        await using var db = CreateContext();
        var queue = new LitigationOrderChunkingQueue();
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, new StubEmbeddingService(_ => []), queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        // Unleased InProgress: left that way by a crash before the lease fields ever existed, or by a claim
        // path that never set them — no lease at all is treated the same as an expired one (demonstrably
        // abandoned, not merely "someone else might still own it").
        var (_, _, unleaseOrder) = await SeedOrderAsync(db, "R1");
        var unleaseDoc = await SeedDownloadedDocumentAsync(db, unleaseOrder.LitigationCaseOrderId, "--- Page 1 (native) ---\ntext");
        unleaseDoc.ChunkingStatus = ChunkingStatus.InProgress;
        await db.SaveChangesAsync();

        // Expired-lease InProgress: a genuine crash mid-claim — the lease token exists but its expiry has
        // already passed.
        var (_, _, expiredOrder) = await SeedOrderAsync(db, "R2");
        var expiredDoc = await SeedDownloadedDocumentAsync(db, expiredOrder.LitigationCaseOrderId, "--- Page 1 (native) ---\ntext");
        expiredDoc.ChunkingStatus = ChunkingStatus.InProgress;
        expiredDoc.ChunkingLeaseToken = Guid.NewGuid();
        expiredDoc.ChunkingLeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        // Live-lease InProgress: another (still-alive) attempt genuinely owns this right now — PR #254 review
        // round 1's exact scenario. Recovery must never reset or immediately re-enqueue this; it schedules a
        // one-time re-check for when the lease is actually due to expire instead.
        var (_, _, liveOrder) = await SeedOrderAsync(db, "R3");
        var liveDoc = await SeedDownloadedDocumentAsync(db, liveOrder.LitigationCaseOrderId, "--- Page 1 (native) ---\ntext");
        liveDoc.ChunkingStatus = ChunkingStatus.InProgress;
        liveDoc.ChunkingLeaseToken = Guid.NewGuid();
        // Generous relative to the immediate-drain window below — real DB round-trips seeding the other rows
        // above can themselves eat into a too-tight margin.
        liveDoc.ChunkingLeaseExpiresUtc = DateTime.UtcNow.AddSeconds(2);
        await db.SaveChangesAsync();

        // Never enqueued: eligible (Downloaded + TextExtracted) but still Pending, simulating a crash between
        // publish and the enqueue call.
        var (_, _, pendingOrder) = await SeedOrderAsync(db, "R4");
        var pendingDoc = await SeedDownloadedDocumentAsync(db, pendingOrder.LitigationCaseOrderId, "--- Page 1 (native) ---\ntext");

        // Not eligible: still InProgress on the DOWNLOAD side (never finished extraction) — must not be swept
        // by the "Downloaded + TextExtracted" half of the sweep, though it WOULD be caught if it were also
        // ChunkingStatus.InProgress; here it's Pending on both axes, so it's simply not yet eligible.
        var (_, _, notDownloadedOrder) = await SeedOrderAsync(db, "R5");
        var notDownloadedDoc = new LitigationOrderDocument
        {
            LitigationCaseOrderId = notDownloadedOrder.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.InProgress,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5), ChunkingStatus = ChunkingStatus.Pending, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(notDownloadedDoc);
        await db.SaveChangesAsync();

        var count = await orchestrator.RecoverStaleWorkAsync(CancellationToken.None);
        Assert.True(count >= 3);

        await db.Entry(unleaseDoc).ReloadAsync();
        Assert.Equal(ChunkingStatus.Pending, unleaseDoc.ChunkingStatus); // reset — no lease at all
        await db.Entry(expiredDoc).ReloadAsync();
        Assert.Equal(ChunkingStatus.Pending, expiredDoc.ChunkingStatus); // reset — lease had expired
        await db.Entry(liveDoc).ReloadAsync();
        Assert.Equal(ChunkingStatus.InProgress, liveDoc.ChunkingStatus); // untouched — lease still live

        var immediate = await DrainAvailableAsync(queue, TimeSpan.FromMilliseconds(300));
        Assert.Contains(unleaseDoc.LitigationOrderDocumentId, immediate);
        Assert.Contains(expiredDoc.LitigationOrderDocumentId, immediate);
        Assert.Contains(pendingDoc.LitigationOrderDocumentId, immediate);
        Assert.DoesNotContain(notDownloadedDoc.LitigationOrderDocumentId, immediate);
        Assert.DoesNotContain(liveDoc.LitigationOrderDocumentId, immediate); // live lease — not yet

        var eventual = await DrainAvailableAsync(queue, TimeSpan.FromSeconds(3));
        Assert.Contains(liveDoc.LitigationOrderDocumentId, eventual); // shows up once its lease expires
    }

    [Fact]
    public async Task RecoverStaleWorkAsync_guarded_reset_never_overwrites_a_row_reclaimed_between_its_read_and_its_write()
    {
        // Regression for PR #254 review round 3: RecoverStaleWorkAsync reads a snapshot of expired/unleased
        // InProgress rows, then bulk-resets them to Pending. Between that read and that write, another worker
        // can atomically reclaim one of those SAME rows (fresh token, fresh unexpired lease, ChunkingStatus
        // staying InProgress throughout) — a WHERE clause guarding only on ChunkingStatus would still match
        // and clobber that fresh claim. A DbCommandInterceptor forces the concurrent reclaim to run exactly
        // the instant recovery's own guarded UPDATE is about to execute — i.e., strictly after its read has
        // already captured this row as a reclaim candidate — proving the write's own re-checked guard, not
        // mere luck, is what protects the winner.
        await using var seedDb = CreateContext();
        var (_, _, order) = await SeedOrderAsync(seedDb, "RC1");
        var document = await SeedDownloadedDocumentAsync(seedDb, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nText from a row recovery will observe as expired, long enough to clear the fifty-character chunk minimum.");

        // Seed InProgress with an already-expired lease — recovery's own read will correctly add this id to
        // its reclaim-candidate set.
        await seedDb.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, ChunkingStatus.InProgress)
                .SetProperty(d => d.ChunkingLeaseToken, Guid.NewGuid())
                .SetProperty(d => d.ChunkingLeaseOwner, "stale-worker")
                .SetProperty(d => d.ChunkingLeaseExpiresUtc, DateTime.UtcNow.AddMinutes(-5)));

        var winnerToken = Guid.NewGuid();
        var winnerExpiry = DateTime.UtcNow.AddMinutes(10);
        var interceptor = new RunOnceBeforeFirstUpdateInterceptor("LitigationOrderDocuments", () =>
        {
            // The winner's real, independent claim — a fresh token and a fresh, unexpired lease, landing on
            // an entirely separate connection from recovery's own.
            using var winnerDb = CreateContext();
            var claimed = winnerDb.LitigationOrderDocuments
                .Where(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId && d.ChunkingStatus == ChunkingStatus.InProgress)
                .ExecuteUpdate(s => s
                    .SetProperty(d => d.ChunkingLeaseToken, winnerToken)
                    .SetProperty(d => d.ChunkingLeaseOwner, "winner-worker")
                    .SetProperty(d => d.ChunkingLeaseExpiresUtc, winnerExpiry));
            Assert.Equal(1, claimed); // sanity: the simulated reclaim itself must have actually landed
        });

        await using var recoveryDb = CreateContextWithInterceptor(interceptor);
        var queue = new LitigationOrderChunkingQueue();
        var orchestrator = new LitigationOrderChunkingOrchestrator(recoveryDb, new StubEmbeddingService(_ => []), queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.RecoverStaleWorkAsync(CancellationToken.None);

        await using var verify = CreateContext();
        var reloaded = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(ChunkingStatus.InProgress, reloaded.ChunkingStatus); // never reset — the winner's claim stands
        Assert.Equal(winnerToken, reloaded.ChunkingLeaseToken); // winner's token intact, not clobbered
        Assert.Equal(winnerExpiry.ToString("s"), reloaded.ChunkingLeaseExpiresUtc?.ToString("s")); // winner's expiry intact

        var enqueued = await DrainAvailableAsync(queue, TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(document.LitigationOrderDocumentId, enqueued); // recovery must never enqueue the reclaimed id
    }

    // ── Lease fencing / multi-instance takeover (PR #254 review round 1) ─────────────────────────────

    [Fact]
    public async Task A_second_instances_startup_recovery_never_resets_or_re_enqueues_a_document_worker_A_still_legitimately_owns()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "MC1");
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nText that worker A is still actively (and slowly) embedding right now.");

        // Worker A claims via the real claim path and then blocks mid-embed — a TaskCompletionSource this
        // test controls stands in for "still actively calling Vertex AI."
        var aIsEmbedding = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var stubA = new StubEmbeddingService(n =>
        {
            aIsEmbedding.TrySetResult();
            releaseA.Task.GetAwaiter().GetResult(); // blocks here until this test releases it
            return Enumerable.Range(0, n).Select(_ => Axis(0)).ToList();
        });
        var orchestratorA = new LitigationOrderChunkingOrchestrator(db, stubA, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        var aTask = orchestratorA.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await aIsEmbedding.Task.WaitAsync(TimeSpan.FromSeconds(5)); // A has claimed and is now mid-embed

        Guid? leaseTokenBeforeRecovery;
        await using (var readDb = CreateContext())
        {
            var reloaded = await readDb.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
            Assert.Equal(ChunkingStatus.InProgress, reloaded.ChunkingStatus);
            Assert.NotNull(reloaded.ChunkingLeaseToken);
            Assert.True(reloaded.ChunkingLeaseExpiresUtc > DateTime.UtcNow); // live lease
            leaseTokenBeforeRecovery = reloaded.ChunkingLeaseToken;
        }

        // A second, independent instance's startup recovery — its own context, its own queue.
        await using var dbB = CreateContext();
        var queueB = new LitigationOrderChunkingQueue();
        var orchestratorB = new LitigationOrderChunkingOrchestrator(dbB, new StubEmbeddingService(_ => []), queueB, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        await orchestratorB.RecoverStaleWorkAsync(CancellationToken.None);

        await using (var readDb = CreateContext())
        {
            var reloaded = await readDb.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
            Assert.Equal(ChunkingStatus.InProgress, reloaded.ChunkingStatus); // untouched
            Assert.Equal(leaseTokenBeforeRecovery, reloaded.ChunkingLeaseToken); // still A's token
        }
        var immediatelyEnqueuedByB = await DrainAvailableAsync(queueB, TimeSpan.FromMilliseconds(200));
        Assert.DoesNotContain(document.LitigationOrderDocumentId, immediatelyEnqueuedByB); // B never re-enqueued it

        releaseA.SetResult(); // let A finish normally
        await aTask.WaitAsync(TimeSpan.FromSeconds(5));

        await using var verify = CreateContext();
        var final = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(ChunkingStatus.Chunked, final.ChunkingStatus); // A's own, undisturbed completion succeeded
    }

    [Fact]
    public async Task A_crashed_workers_live_lease_becomes_claimable_and_reaches_Chunked_via_the_delayed_recheck_without_another_app_restart()
    {
        // Regression for PR #254 review round 2: RecoverStaleWorkAsync's delayed path (ScheduleRetry, for a
        // row whose lease was still live at sweep time) used to only ever re-enqueue the id once that lease
        // expired — it never performed the actual InProgress -> Pending reclaim itself, and the claim at the
        // time only ever admitted Pending. So if the original worker had genuinely crashed while its lease was
        // live, the row stayed stuck InProgress forever after that lease expired, waiting on some unrelated
        // future app restart's recovery sweep to notice it again. This proves the fix (the claim itself now
        // also admits an expired-lease InProgress row): recovery runs once, BEFORE the crashed worker's lease
        // expires, and the document still reaches Chunked once that lease is due — with no second recovery
        // sweep and no app restart in between.
        await using var seedDb = CreateContext();
        var (_, _, order) = await SeedOrderAsync(seedDb, "CR1");
        var document = await SeedDownloadedDocumentAsync(seedDb, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nText from a worker that crashed while its lease was still live, long enough to clear the fifty-character chunk minimum.");

        // Simulate a crashed worker's claim directly (never renewed, never completed) — its own independent
        // context, discarded immediately, standing in for a process that died right after claiming.
        await using (var crashedWorkerDb = CreateContext())
        {
            await crashedWorkerDb.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ChunkingStatus, ChunkingStatus.InProgress)
                    .SetProperty(d => d.ChunkingLeaseToken, Guid.NewGuid())
                    .SetProperty(d => d.ChunkingLeaseOwner, "crashed-worker")
                    .SetProperty(d => d.ChunkingLeaseExpiresUtc, DateTime.UtcNow.AddSeconds(2))); // still live below
        }

        await using var recoveryDb = CreateContext();
        var queue = new LitigationOrderChunkingQueue();
        var recoveryOrchestrator = new LitigationOrderChunkingOrchestrator(
            recoveryDb, new StubEmbeddingService(_ => []), queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        // Run recovery BEFORE the crashed worker's lease expires — the exact scenario described in review: the
        // lease is still live at this instant, so the row is left alone and instead scheduled for a one-time
        // delayed re-check.
        await recoveryOrchestrator.RecoverStaleWorkAsync(CancellationToken.None);

        await using (var readDb = CreateContext())
        {
            var reloaded = await readDb.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
            Assert.Equal(ChunkingStatus.InProgress, reloaded.ChunkingStatus); // untouched by the immediate path
        }
        var immediate = await DrainAvailableAsync(queue, TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain(document.LitigationOrderDocumentId, immediate); // not yet — lease still live

        // Wait past the crashed worker's lease expiry — the delayed re-check fires and enqueues the id.
        var eventual = await DrainAvailableAsync(queue, TimeSpan.FromSeconds(3));
        Assert.Contains(document.LitigationOrderDocumentId, eventual);

        // A worker dequeues it and calls ChunkOrderDocumentAsync exactly as it normally would — no further
        // recovery sweep, no app restart. Its own independent context; the claim's widened predicate reclaims
        // the now-expired-lease InProgress row directly.
        await using var reclaimDb = CreateContext();
        var reclaimStub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => Axis(0)).ToList());
        var reclaimOrchestrator = new LitigationOrderChunkingOrchestrator(
            reclaimDb, reclaimStub, queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        await reclaimOrchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var verify = CreateContext();
        var final = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(ChunkingStatus.Chunked, final.ChunkingStatus);
        Assert.True(await verify.LitigationOrderChunks.AnyAsync(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId));
    }

    [Fact]
    public async Task ChunkOrderDocumentAsync_a_stale_workers_late_completion_after_a_mid_flight_lease_takeover_never_overwrites_the_winners_chunk_set()
    {
        // Regression for PR #254 review round 1: worker A claims, then (while still slowly embedding) its
        // lease genuinely expires and a second instance's recovery sweep resets the row so worker B can claim,
        // chunk and publish ITS OWN chunk set. A then finishes its own (now-stale) embedding and tries to
        // publish too. Before the fix, the claim was a plain status flip with no fencing token at all, so A's
        // completion would have unconditionally deleted B's already-published chunks and inserted its own —
        // this proves the fix: A's lease-guarded completion matches 0 rows, so its entire transaction (the
        // delete of B's chunks AND the insert of A's own) rolls back, leaving B's chunk rows — down to their
        // own identity values — completely untouched.
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "TK1");
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nOriginal order text from worker A, long enough to clear the fifty-character chunk minimum.");

        List<long>? bChunkIdsAfterPublish = null;
        var stubA = new StubEmbeddingService(n =>
        {
            // The interleaving point: force-expire A's already-minted lease on an INDEPENDENT connection
            // (exactly what real wall-clock elapsed time while A was still slowly embedding would produce),
            // then run a full, real worker B — its own recovery sweep, its own orchestrator, its own context —
            // synchronously right here, before control ever returns to A's own embedding call.
            using (var takeoverDb = CreateContext())
            {
                takeoverDb.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
                    .ExecuteUpdate(s => s.SetProperty(d => d.ChunkingLeaseExpiresUtc, DateTime.UtcNow.AddSeconds(-1)));
            }

            using var dbB = CreateContext();
            var stubB = new StubEmbeddingService(m => Enumerable.Range(0, m).Select(_ => Axis(1)).ToList());
            var orchestratorB = new LitigationOrderChunkingOrchestrator(dbB, stubB, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
            // B's own recovery resets the now-expired row from InProgress back to Pending so its own claim can
            // succeed — exactly the real startup-recovery path a second instance would run. Only OUR document
            // is then driven through B's orchestrator directly by id (not via the queue, which is irrelevant
            // to what this test proves) — the shared, real test DB means recovery's sweep can also touch
            // unrelated InProgress rows left by other tests, matching LitigationOrderDocumentServiceTests' own
            // "query is global to the shared test DB" precedent.
            orchestratorB.RecoverStaleWorkAsync(CancellationToken.None).GetAwaiter().GetResult();
            orchestratorB.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None).GetAwaiter().GetResult();

            using (var readDb = CreateContext())
            {
                bChunkIdsAfterPublish = readDb.LitigationOrderChunks
                    .Where(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
                    .Select(c => c.LitigationOrderChunkId).OrderBy(id => id).ToList();
            }

            return Enumerable.Range(0, n).Select(_ => Axis(0)).ToList(); // A's own (now-stale) embeddings
        });

        var orchestratorA = new LitigationOrderChunkingOrchestrator(db, stubA, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        await orchestratorA.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None); // A's own claim already happened before this call started

        Assert.NotNull(bChunkIdsAfterPublish);
        Assert.NotEmpty(bChunkIdsAfterPublish);

        await using var verify = CreateContext();
        var reloadedDoc = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(ChunkingStatus.Chunked, reloadedDoc.ChunkingStatus); // B's completion stands
        Assert.Null(reloadedDoc.ChunkingLastError); // never overwritten with an error either

        var finalChunkIds = await verify.LitigationOrderChunks
            .Where(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
            .Select(c => c.LitigationOrderChunkId).OrderBy(id => id).ToListAsync();
        Assert.Equal(bChunkIdsAfterPublish, finalChunkIds); // exact same rows — A's late write never committed
    }

    private static async Task<List<long>> DrainAvailableAsync(LitigationOrderChunkingQueue queue, TimeSpan window)
    {
        var ids = new List<long>();
        using var cts = new CancellationTokenSource(window);
        try
        {
            await foreach (var id in queue.ReadAllAsync(cts.Token))
                ids.Add(id);
        }
        catch (OperationCanceledException) { /* window elapsed — return whatever arrived */ }
        return ids;
    }

    // ── Retention: expiry of the original never removes already-indexed text/provenance ─────────────

    [Fact]
    public async Task An_expired_order_documents_chunks_and_extracted_text_remain_intact_and_retrievable()
    {
        await using var db = CreateContext();
        var (request, _, order) = await SeedOrderAsync(db, "E1");
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nOrder text that must survive expiry of the source PDF, padded past the fifty-character chunk minimum.");

        // A fixed unit vector shared by the stored chunk and the query guarantees distance 0 — well under the
        // MaxCosineDistance threshold — rather than relying on two independent random vectors happening to
        // land close enough (see DocumentRetrieverTests' own unit-basis-vector approach for the same reasoning).
        var stub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => Axis(0)).ToList(), fixedQueryVector: Axis(0));
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        // Simulate the retention window later expiring / the on-disk file being purged: only Status and
        // StoragePath change, mirroring what LitigationOrderDocumentService's own expiry path touches — it
        // never touches ExtractedText or LitigationOrderChunks.
        await db.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, LitigationOrderDocumentStatus.Expired)
                .SetProperty(d => d.StoragePath, (string?)null));

        await using var verify = CreateContext();
        var reloadedDoc = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderDocumentStatus.Expired, reloadedDoc.Status);
        Assert.NotNull(reloadedDoc.ExtractedText); // original PDF gone, extracted text still retained
        Assert.Equal(ChunkingStatus.Chunked, reloadedDoc.ChunkingStatus);

        var chunkCount = await verify.LitigationOrderChunks.CountAsync(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.True(chunkCount > 0); // chunks untouched by the expiry write

        // And still retrievable via LitigationDocumentRetriever for this request.
        var retriever = new LitigationDocumentRetriever(verify, ChatRetrievalOptions.Default);
        var matches = await retriever.SearchRequestOrdersAsync(request.RequestId, Axis(0), CancellationToken.None);
        Assert.Contains(matches, m => m.Chunk.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
    }

    // ── Cross-request isolation (critical acceptance criterion) ──────────────────────────────────────

    [Fact]
    public async Task SearchRequestOrdersAsync_never_returns_a_chunk_belonging_to_a_different_request()
    {
        await using var db = CreateContext();
        var (requestA, _, orderA) = await SeedOrderAsync(db, "X1");
        var docA = await SeedDownloadedDocumentAsync(db, orderA.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nRequest A's confidential litigation order text, padded past the fifty-character chunk minimum.");

        var (requestB, _, orderB) = await SeedOrderAsync(db, "X2");
        var docB = await SeedDownloadedDocumentAsync(db, orderB.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nRequest B's confidential litigation order text, padded past the fifty-character chunk minimum.");

        // Same fixed unit vector for both requests' chunks and the query — guarantees distance 0 for
        // everything, so any leakage across requests would show up as an extra result, not get masked by an
        // unlucky distance-threshold miss.
        var stub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => Axis(0)).ToList(), fixedQueryVector: Axis(0));
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        await orchestrator.ChunkOrderDocumentAsync(docA.LitigationOrderDocumentId, CancellationToken.None);
        await orchestrator.ChunkOrderDocumentAsync(docB.LitigationOrderDocumentId, CancellationToken.None);

        await using var verify = CreateContext();
        var retriever = new LitigationDocumentRetriever(verify, ChatRetrievalOptions.Default);

        var matchesForA = await retriever.SearchRequestOrdersAsync(requestA.RequestId, Axis(0), CancellationToken.None);
        Assert.NotEmpty(matchesForA);
        Assert.All(matchesForA, m => Assert.Equal(requestA.RequestId, m.Chunk.RequestId));
        Assert.DoesNotContain(matchesForA, m => m.Chunk.LitigationOrderDocumentId == docB.LitigationOrderDocumentId);

        var matchesForB = await retriever.SearchRequestOrdersAsync(requestB.RequestId, Axis(0), CancellationToken.None);
        Assert.NotEmpty(matchesForB);
        Assert.All(matchesForB, m => Assert.Equal(requestB.RequestId, m.Chunk.RequestId));
        Assert.DoesNotContain(matchesForB, m => m.Chunk.LitigationOrderDocumentId == docA.LitigationOrderDocumentId);
    }

    // ── #194 hybrid retrieval (full-text + vector, RRF) ──────────────────────────────────────────────

    [SkippableFact]
    public async Task SearchRequestOrdersAsync_finds_an_exact_case_number_that_semantic_search_alone_misses()
    {
        await FullTextTestSupport.RequireFullTextIndexAsync("dbo.LitigationOrderChunks");
        await using var db = CreateContext();
        var (request, _, order) = await SeedOrderAsync(db, "FT");
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId,
            "--- Page 1 (native) ---\nIn TP 255/2019 the Tribunal heard IA 12/2020 and reserved orders on the stay application.");

        // Orthogonal to the query vector: cosine distance 1, past the 0.5 threshold below, so only the lexical
        // path can surface this chunk.
        var stub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => Axis(1)).ToList());
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var verify = CreateContext();
        var chunkId = await verify.LitigationOrderChunks.Where(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
            .Select(c => c.LitigationOrderChunkId).FirstAsync();
        await FullTextTestSupport.WaitUntilIndexedAsync("LitigationOrderChunks", "LitigationOrderChunkId", chunkId, "\"TP 255/2019\"");

        var retriever = new LitigationDocumentRetriever(verify, ChatRetrievalOptions.Default with { MaxCosineDistance = 0.5 });

        var semanticOnly = await retriever.SearchRequestOrdersAsync(request.RequestId, Axis(0), CancellationToken.None);
        Assert.Empty(semanticOnly);

        var hybrid = await retriever.SearchRequestOrdersAsync(request.RequestId, Axis(0), CancellationToken.None, ["TP 255/2019"]);
        var match = Assert.Single(hybrid);
        Assert.Equal(chunkId, match.Chunk.LitigationOrderChunkId);
        Assert.Equal(request.RequestId, match.Chunk.RequestId);
    }

    // ── Citation shape (RetrievalContextBuilder -> RetrievedSource) ──────────────────────────────────

    [Fact]
    public async Task RetrievalContextBuilder_surfaces_litigation_chunks_with_case_order_and_page_identifying_fields()
    {
        await using var db = CreateContext();
        var (request, litigationCase, order) = await SeedOrderAsync(db, "C1");
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId, "--- Page 1 (native) ---\nThe court held that the charge is valid and enforceable against the company.");

        var stub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => Axis(0)).ToList(), fixedQueryVector: Axis(0));
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);
        await orchestrator.ChunkOrderDocumentAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var buildDb = CreateContext();
        var contextBuilder = new RetrievalContextBuilder(
            buildDb, new StructuredFactsProvider(buildDb), new DocumentRetriever(buildDb, ChatRetrievalOptions.Default),
            new LitigationDocumentRetriever(buildDb, ChatRetrievalOptions.Default), stub, new LitigationOrderOutcomeQuery(buildDb));

        var context = await contextBuilder.BuildAsync(request.RequestId, "Is the charge enforceable?", CancellationToken.None);

        var litigationSource = Assert.Single(context.Sources, s => s.Type == SourceType.LitigationChunk);
        Assert.StartsWith("L", litigationSource.Tag);
        Assert.Equal(litigationCase.LitigationCaseId, litigationSource.LitigationCaseId);
        Assert.Equal(order.LitigationCaseOrderId, litigationSource.LitigationCaseOrderId);
        Assert.Equal(document.LitigationOrderDocumentId, litigationSource.DocumentId);
        Assert.Equal(1, litigationSource.PageNumber);
        Assert.NotNull(litigationSource.ChunkId);
    }

    // ── #361: Per-chunk context header & Re-embed path tests ──────────────────────────────────────────

    [Fact]
    public void BuildContextHeader_formats_full_and_partial_metadata()
    {
        // Full metadata
        var full = LitigationOrderChunkingOrchestrator.BuildContextHeader(
            "District Court, Chennai", "OS/123/2025", "TNKP070001234", "Judgment", "09-01-2025", 1);
        Assert.Equal("[Court: District Court, Chennai | Case: OS/123/2025 | CNR: TNKP070001234 | Order: Judgment 09-01-2025 | Page 1]", full);

        // Missing CNR
        var noCnr = LitigationOrderChunkingOrchestrator.BuildContextHeader(
            "High Court of Madras", "WP/456/2024", null, "Order", "15-02-2024", 3);
        Assert.Equal("[Court: High Court of Madras | Case: WP/456/2024 | Order: Order 15-02-2024 | Page 3]", noCnr);

        // Missing OrderType but has date
        var noOrderType = LitigationOrderChunkingOrchestrator.BuildContextHeader(
            "NCLT Mumbai", "CP 100/2023", "NCLT123", null, "20-03-2023", 2);
        Assert.Equal("[Court: NCLT Mumbai | Case: CP 100/2023 | CNR: NCLT123 | Order: 20-03-2023 | Page 2]", noOrderType);

        // Minimal (only page)
        var minimal = LitigationOrderChunkingOrchestrator.BuildContextHeader(null, null, null, null, null, 5);
        Assert.Equal("[Page 5]", minimal);
    }

    [Fact]
    public async Task RechunkOutdatedDocumentsAsync_upgrades_v1_documents_to_v2_with_context_header_atomically()
    {
        await using var db = CreateContext();
        var (request, litigationCase, order) = await SeedOrderAsync(db, "RE1");
        var text = "--- Page 1 (native) ---\nThe high court granted an interim injunction restraining alienation.";
        var document = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId, text);

        // Seed an existing "1.0" chunk set without context headers
        var v1Chunk = new LitigationOrderChunk
        {
            RequestId = request.RequestId,
            LitigationOrderDocumentId = document.LitigationOrderDocumentId,
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            LitigationCaseId = litigationCase.LitigationCaseId,
            CaseNumber = litigationCase.CaseNumber,
            Cnr = litigationCase.Cnr,
            Court = litigationCase.Court,
            OrderType = order.OrderType,
            OrderDate = order.OrderDate,
            ChunkIndex = 0,
            PageNumber = 1,
            ChunkText = "The high court granted an interim injunction restraining alienation.",
            Embedding = new SqlVector<float>(Axis(0)),
            EmbeddingModel = EmbeddingService.ModelId,
            EmbeddingDimensions = EmbeddingService.Dimensions,
            ChunkingVersion = "1.0",
            CreatedDate = DateTime.UtcNow.AddDays(-10)
        };
        db.LitigationOrderChunks.Add(v1Chunk);
        document.ChunkingStatus = ChunkingStatus.Chunked;
        await db.SaveChangesAsync();

        var queue = new LitigationOrderChunkingQueue();
        var stub = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => Axis(2)).ToList());
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        // Verify detection of outdated version
        var outdatedIds = await orchestrator.GetOutdatedDocumentIdsAsync(10, CancellationToken.None, request.RequestId);
        Assert.Contains(document.LitigationOrderDocumentId, outdatedIds);

        // Execute rechunk
        var rechunkedCount = await orchestrator.RechunkOutdatedDocumentsAsync(10, CancellationToken.None, request.RequestId);
        Assert.Equal(1, rechunkedCount);

        // Verify document is now Chunked with 2.0 chunks carrying the context header
        await using var verify = CreateContext();
        var reloadedDoc = await verify.LitigationOrderDocuments.AsNoTracking().FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(ChunkingStatus.Chunked, reloadedDoc.ChunkingStatus);

        var chunks = await verify.LitigationOrderChunks.AsNoTracking()
            .Where(c => c.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
            .ToListAsync();
        var chunk = Assert.Single(chunks);
        Assert.Equal("2.0", chunk.ChunkingVersion);
        Assert.StartsWith($"[Court: {litigationCase.Court} | Case: {litigationCase.CaseNumber} | CNR: {litigationCase.Cnr} | Order: {order.OrderType} {order.OrderDate} | Page 1]\n\n", chunk.ChunkText);
        Assert.Equal(Axis(2), chunk.Embedding.Memory.ToArray());

        // Document is no longer outdated
        var remainingOutdated = await orchestrator.GetOutdatedDocumentIdsAsync(10, CancellationToken.None, request.RequestId);
        Assert.DoesNotContain(document.LitigationOrderDocumentId, remainingOutdated);
    }

    [Fact]
    public async Task FindSameTextEmbeddingsAsync_refuses_v1_chunks_when_active_version_is_v2()
    {
        await using var db = CreateContext();
        var (request, parentCase, parentOrder) = await SeedOrderAsync(db, "V1");
        var text = "--- Page 1 (native) ---\nSole arbitrator passed award dismissing all claims.";
        var hash = Guid.NewGuid().ToString("N");
        var doc1 = await SeedDownloadedDocumentAsync(db, parentOrder.LitigationCaseOrderId, text, hash);

        // doc1 was chunked with version "1.0"
        var v1Chunk = new LitigationOrderChunk
        {
            RequestId = request.RequestId,
            LitigationOrderDocumentId = doc1.LitigationOrderDocumentId,
            LitigationCaseOrderId = parentOrder.LitigationCaseOrderId,
            LitigationCaseId = parentCase.LitigationCaseId,
            CaseNumber = parentCase.CaseNumber,
            Cnr = parentCase.Cnr,
            Court = parentCase.Court,
            OrderType = parentOrder.OrderType,
            OrderDate = parentOrder.OrderDate,
            ChunkIndex = 0,
            PageNumber = 1,
            ChunkText = text,
            Embedding = new SqlVector<float>(Axis(1)),
            EmbeddingModel = EmbeddingService.ModelId,
            EmbeddingDimensions = EmbeddingService.Dimensions,
            ChunkingVersion = "1.0",
            CreatedDate = DateTime.UtcNow.AddDays(-5)
        };
        db.LitigationOrderChunks.Add(v1Chunk);
        doc1.ChunkingStatus = ChunkingStatus.Chunked;
        await db.SaveChangesAsync();

        // Seed doc2 with identical text and hash under the same request
        var (_, _, secondOrder) = await SeedOrderAsync(db, "V2", request.RequestId);
        var doc2 = await SeedDownloadedDocumentAsync(db, secondOrder.LitigationCaseOrderId, text, hash);

        var embedCalls = 0;
        var stub = new StubEmbeddingService(n => { embedCalls++; return Enumerable.Range(0, n).Select(_ => Axis(2)).ToList(); });
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, stub, new LitigationOrderChunkingQueue(), NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        await orchestrator.ChunkOrderDocumentAsync(doc2.LitigationOrderDocumentId, CancellationToken.None);

        // doc2 must embed afresh because doc1 was at version "1.0", which cannot be reused for "2.0"
        Assert.Equal(1, embedCalls);

        await using var verify = CreateContext();
        var doc2Chunk = await verify.LitigationOrderChunks.AsNoTracking().SingleAsync(c => c.LitigationOrderDocumentId == doc2.LitigationOrderDocumentId);
        Assert.Equal("2.0", doc2Chunk.ChunkingVersion);
        Assert.Equal(Axis(2), doc2Chunk.Embedding.Memory.ToArray());
    }

    [Fact]
    public async Task SearchRequestOrdersAsync_filters_by_active_embedding_model_and_dimensions()
    {
        await using var db = CreateContext();
        var (request, litigationCase, order) = await SeedOrderAsync(db, "MF1");
        var doc = await SeedDownloadedDocumentAsync(db, order.LitigationCaseOrderId, "--- Page 1 (native) ---\nOrder text for model filter test.");

        // Obsolete chunk with a different embedding model
        var obsoleteChunk = new LitigationOrderChunk
        {
            RequestId = request.RequestId,
            LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            LitigationCaseId = litigationCase.LitigationCaseId,
            CaseNumber = litigationCase.CaseNumber,
            Cnr = litigationCase.Cnr,
            Court = litigationCase.Court,
            OrderType = order.OrderType,
            OrderDate = order.OrderDate,
            ChunkIndex = 0,
            PageNumber = 1,
            ChunkText = "[Court: District Court | Case: OS/1 | Page 1]\n\nObsolete chunk",
            Embedding = new SqlVector<float>(Axis(0)),
            EmbeddingModel = "legacy-model-v0",
            EmbeddingDimensions = EmbeddingService.Dimensions,
            ChunkingVersion = "1.0",
            CreatedDate = DateTime.UtcNow.AddDays(-30)
        };

        // Obsolete chunk with active model but incompatible dimensions (e.g. 512 vs active 768)
        var obsoleteDimensionsChunk = new LitigationOrderChunk
        {
            RequestId = request.RequestId,
            LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            LitigationCaseId = litigationCase.LitigationCaseId,
            CaseNumber = litigationCase.CaseNumber,
            Cnr = litigationCase.Cnr,
            Court = litigationCase.Court,
            OrderType = order.OrderType,
            OrderDate = order.OrderDate,
            ChunkIndex = 1,
            PageNumber = 1,
            ChunkText = "[Court: District Court | Case: OS/1 | Page 1]\n\nObsolete dimensions chunk",
            Embedding = new SqlVector<float>(Axis(0)),
            EmbeddingModel = EmbeddingService.ModelId,
            EmbeddingDimensions = 512,
            ChunkingVersion = "2.0",
            CreatedDate = DateTime.UtcNow.AddDays(-15)
        };

        // Active chunk with current embedding model and active dimensions
        var activeChunk = new LitigationOrderChunk
        {
            RequestId = request.RequestId,
            LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            LitigationCaseId = litigationCase.LitigationCaseId,
            CaseNumber = litigationCase.CaseNumber,
            Cnr = litigationCase.Cnr,
            Court = litigationCase.Court,
            OrderType = order.OrderType,
            OrderDate = order.OrderDate,
            ChunkIndex = 2,
            PageNumber = 1,
            ChunkText = "[Court: District Court | Case: OS/1 | Page 1]\n\nActive chunk",
            Embedding = new SqlVector<float>(Axis(0)),
            EmbeddingModel = EmbeddingService.ModelId,
            EmbeddingDimensions = EmbeddingService.Dimensions,
            ChunkingVersion = "2.0",
            CreatedDate = DateTime.UtcNow
        };

        db.LitigationOrderChunks.AddRange(obsoleteChunk, obsoleteDimensionsChunk, activeChunk);
        doc.ChunkingStatus = ChunkingStatus.Chunked;
        await db.SaveChangesAsync();

        await using var verify = CreateContext();
        var retriever = new LitigationDocumentRetriever(verify, ChatRetrievalOptions.Default);
        var matches = await retriever.SearchRequestOrdersAsync(request.RequestId, Axis(0), CancellationToken.None);

        // Vector search only returns chunks from the active EmbeddingModel AND active EmbeddingDimensions
        var match = Assert.Single(matches);
        Assert.Equal(EmbeddingService.ModelId, match.Chunk.EmbeddingModel);
        Assert.Equal(EmbeddingService.Dimensions, match.Chunk.EmbeddingDimensions);
        Assert.Equal(activeChunk.ChunkText, match.Chunk.ChunkText);
    }

    [Fact]
    public async Task EnqueueOutdatedDocumentsAsync_resets_and_enqueues_documents_with_outdated_dimensions_same_model()
    {
        await using var db = CreateContext();
        var (request, caseA, orderA) = await SeedOrderAsync(db, "ENDIM1");
        var doc = await SeedDownloadedDocumentAsync(db, orderA.LitigationCaseOrderId, "--- Page 1 (native) ---\ndim text");
        var chunk = new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
            LitigationCaseOrderId = orderA.LitigationCaseOrderId, LitigationCaseId = caseA.LitigationCaseId,
            ChunkIndex = 0, PageNumber = 1, ChunkText = "dim text", Embedding = new SqlVector<float>(Axis(0)),
            EmbeddingModel = EmbeddingService.ModelId, EmbeddingDimensions = 512,
            ChunkingVersion = LitigationOrderChunkingOrchestrator.ChunkingVersion, CreatedDate = DateTime.UtcNow
        };
        db.LitigationOrderChunks.Add(chunk);
        doc.ChunkingStatus = ChunkingStatus.Chunked;
        await db.SaveChangesAsync();

        var queue = new LitigationOrderChunkingQueue();
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, new StubEmbeddingService(_ => []), queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        var enqueuedCount = await orchestrator.EnqueueOutdatedDocumentsAsync(10, CancellationToken.None, request.RequestId);
        Assert.Equal(1, enqueuedCount);

        await db.Entry(doc).ReloadAsync();
        Assert.Equal(ChunkingStatus.Pending, doc.ChunkingStatus);

        var drained = await DrainAvailableAsync(queue, TimeSpan.FromMilliseconds(200));
        Assert.Contains(doc.LitigationOrderDocumentId, drained);
    }

    [Fact]
    public async Task EnqueueOutdatedDocumentsAsync_resets_and_enqueues_outdated_documents()
    {
        await using var db = CreateContext();
        var (request, caseA, orderA) = await SeedOrderAsync(db, "EN1");
        var docV1 = await SeedDownloadedDocumentAsync(db, orderA.LitigationCaseOrderId, "--- Page 1 (native) ---\nv1 text");
        var chunkV1 = new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = docV1.LitigationOrderDocumentId,
            LitigationCaseOrderId = orderA.LitigationCaseOrderId, LitigationCaseId = caseA.LitigationCaseId,
            ChunkIndex = 0, PageNumber = 1, ChunkText = "v1 text", Embedding = new SqlVector<float>(Axis(0)),
            EmbeddingModel = EmbeddingService.ModelId, EmbeddingDimensions = EmbeddingService.Dimensions,
            ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
        };
        db.LitigationOrderChunks.Add(chunkV1);
        docV1.ChunkingStatus = ChunkingStatus.Chunked;

        var (_, caseB, orderB) = await SeedOrderAsync(db, "EN2", request.RequestId);
        var docV2 = await SeedDownloadedDocumentAsync(db, orderB.LitigationCaseOrderId, "--- Page 1 (native) ---\nv2 text");
        var chunkV2 = new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = docV2.LitigationOrderDocumentId,
            LitigationCaseOrderId = orderB.LitigationCaseOrderId, LitigationCaseId = caseB.LitigationCaseId,
            ChunkIndex = 0, PageNumber = 1, ChunkText = "[Header]\n\nv2 text", Embedding = new SqlVector<float>(Axis(0)),
            EmbeddingModel = EmbeddingService.ModelId, EmbeddingDimensions = EmbeddingService.Dimensions,
            ChunkingVersion = "2.0", CreatedDate = DateTime.UtcNow
        };
        db.LitigationOrderChunks.Add(chunkV2);
        docV2.ChunkingStatus = ChunkingStatus.Chunked;
        await db.SaveChangesAsync();

        var queue = new LitigationOrderChunkingQueue();
        var orchestrator = new LitigationOrderChunkingOrchestrator(db, new StubEmbeddingService(_ => []), queue, NullLogger<LitigationOrderChunkingOrchestrator>.Instance);

        var enqueuedCount = await orchestrator.EnqueueOutdatedDocumentsAsync(10, CancellationToken.None, request.RequestId);
        Assert.Equal(1, enqueuedCount);

        // docV1 is reset to Pending; docV2 remains Chunked
        await db.Entry(docV1).ReloadAsync();
        await db.Entry(docV2).ReloadAsync();
        Assert.Equal(ChunkingStatus.Pending, docV1.ChunkingStatus);
        Assert.Equal(ChunkingStatus.Chunked, docV2.ChunkingStatus);

        var drained = await DrainAvailableAsync(queue, TimeSpan.FromMilliseconds(200));
        Assert.Contains(docV1.LitigationOrderDocumentId, drained);
        Assert.DoesNotContain(docV2.LitigationOrderDocumentId, drained);
    }

    [Theory]
    [InlineData("NCLT")]
    [InlineData("NCLT [Mumbai Bench]")]
    [InlineData("NCLT | Mumbai Bench")]
    [InlineData("NCLT [Mumbai | Bench]")]
    public void DropDuplicateText_suppresses_duplicate_passages_sharing_body_across_parent_case_and_IA(string court)
    {
        const string body = "The application is dismissed and the order of the tribunal is affirmed.";
        var parentHeader = LitigationOrderChunkingOrchestrator.BuildContextHeader(court, "CP 1/2025", null, "Order", "2025-01-01", 1);
        var iaHeader = LitigationOrderChunkingOrchestrator.BuildContextHeader(court, "IA 2/2025", null, "Order", "2025-01-01", 1);

        var parentChunk = new LitigationOrderChunk
        {
            LitigationOrderChunkId = 1,
            ChunkText = $"{parentHeader}\n\n{body}"
        };
        var iaChunk = new LitigationOrderChunk
        {
            LitigationOrderChunkId = 2,
            ChunkText = $"{iaHeader}\n\n{body}"
        };

        var matches = new[]
        {
            new LitigationOrderChunkMatch(parentChunk, 0.1),
            new LitigationOrderChunkMatch(iaChunk, 0.1)
        };

        var deduplicated = LitigationDocumentRetriever.DropDuplicateText(matches, 10);
        var single = Assert.Single(deduplicated);
        Assert.Equal(1, single.Chunk.LitigationOrderChunkId);
        // Selected provenance header on the retained chunk is preserved
        Assert.StartsWith(parentHeader, single.Chunk.ChunkText);
    }

    [Fact]
    public void ExtractBodyText_does_not_strip_bracketed_legal_order_text()
    {
        const string numbered = "[1] The petitioner approached the High Court under Article 226.";
        Assert.Equal(numbered, LitigationDocumentRetriever.ExtractBodyText(numbered));

        const string oral = "[Oral Order]\n\nThe matter is adjourned to next week.";
        Assert.Equal(oral, LitigationDocumentRetriever.ExtractBodyText(oral));

        const string coram = "[Coram: Hon'ble Mr. Justice ABC]\n\nNotice issued.";
        Assert.Equal(coram, LitigationDocumentRetriever.ExtractBodyText(coram));

        const string orderPrefixOnly = "[Order: Dismissed]\n\nNotice issued.";
        Assert.Equal(orderPrefixOnly, LitigationDocumentRetriever.ExtractBodyText(orderPrefixOnly));

        const string courtPrefixOnly = "[Court: High Court]\n\nNotice issued.";
        Assert.Equal(courtPrefixOnly, LitigationDocumentRetriever.ExtractBodyText(courtPrefixOnly));

        const string header = "[Court: High Court | Case: WP 123/2024 | Page 1]\n\nSubstantive order text.";
        Assert.Equal("Substantive order text.", LitigationDocumentRetriever.ExtractBodyText(header));

        const string bracketedHeader = "[Court: NCLT [Mumbai Bench] | Case: CP 1/2025 | Page 1]\n\nSubstantive order text.";
        Assert.Equal("Substantive order text.", LitigationDocumentRetriever.ExtractBodyText(bracketedHeader));

        const string pipedHeader = "[Court: NCLT | Mumbai Bench | Case: CP 1/2025 | Page 1]\n\nSubstantive order text.";
        Assert.Equal("Substantive order text.", LitigationDocumentRetriever.ExtractBodyText(pipedHeader));

        const string pipedBracketHeader = "[Court: NCLT [Mumbai | Bench] | Case: CP [IB] 1/2025 | Order: Order [Interim] 2025-01-01 | Page 1]\n\nSubstantive order text.";
        Assert.Equal("Substantive order text.", LitigationDocumentRetriever.ExtractBodyText(pipedBracketHeader));

        const string headerWithBracketedLegalBody = "[Court: NCLT [Mumbai Bench] | Page 1]\n\n[1] The petitioner approached the High Court under Article 226.";
        Assert.Equal("[1] The petitioner approached the High Court under Article 226.", LitigationDocumentRetriever.ExtractBodyText(headerWithBracketedLegalBody));
    }

    [Theory]
    [InlineData("District Court, Chennai")]
    [InlineData("NCLT [Mumbai Bench]")]
    [InlineData("NCLT | Mumbai Bench")]
    [InlineData("NCLT [Mumbai | Bench]")]
    public async Task GetLatestOrderChunksAsync_and_GetDecisionPassagesAsync_preserve_both_cases_when_sharing_identical_text_and_deduplicate_within_scope(string court)
    {
        await using var db = CreateContext();
        var (request, parentCase, parentOrder) = await SeedOrderAsync(db, "DEDUP1");
        var (_, iaCase, iaOrder) = await SeedOrderAsync(db, "DEDUP2", request.RequestId);

        parentCase.Court = court;
        iaCase.Court = court;
        await db.SaveChangesAsync();

        const string sharedBody = "The application is dismissed and relief is declined.";
        var parentHeader = LitigationOrderChunkingOrchestrator.BuildContextHeader(parentCase.Court, parentCase.CaseNumber, parentCase.Cnr, parentOrder.OrderType, parentOrder.OrderDate, 1);
        var iaHeader = LitigationOrderChunkingOrchestrator.BuildContextHeader(iaCase.Court, iaCase.CaseNumber, iaCase.Cnr, iaOrder.OrderType, iaOrder.OrderDate, 1);

        var parentDoc = await SeedDownloadedDocumentAsync(db, parentOrder.LitigationCaseOrderId, "parent text");
        var iaDoc = await SeedDownloadedDocumentAsync(db, iaOrder.LitigationCaseOrderId, "ia text");

        // Two chunks in parentOrder sharing the same body text (testing intra-order / intra-case deduplication)
        db.LitigationOrderChunks.Add(new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = parentDoc.LitigationOrderDocumentId,
            LitigationCaseOrderId = parentOrder.LitigationCaseOrderId, LitigationCaseId = parentCase.LitigationCaseId,
            CaseNumber = parentCase.CaseNumber, Court = parentCase.Court, OrderType = parentOrder.OrderType, OrderDate = parentOrder.OrderDate,
            ChunkIndex = 0, PageNumber = 1, ChunkText = $"{parentHeader}\n\n{sharedBody}",
            Embedding = new SqlVector<float>(Axis(0)), EmbeddingModel = EmbeddingService.ModelId,
            EmbeddingDimensions = EmbeddingService.Dimensions, ChunkingVersion = "2.0", CreatedDate = DateTime.UtcNow
        });
        db.LitigationOrderChunks.Add(new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = parentDoc.LitigationOrderDocumentId,
            LitigationCaseOrderId = parentOrder.LitigationCaseOrderId, LitigationCaseId = parentCase.LitigationCaseId,
            CaseNumber = parentCase.CaseNumber, Court = parentCase.Court, OrderType = parentOrder.OrderType, OrderDate = parentOrder.OrderDate,
            ChunkIndex = 1, PageNumber = 2, ChunkText = $"{parentHeader}\n\n{sharedBody}",
            Embedding = new SqlVector<float>(Axis(0)), EmbeddingModel = EmbeddingService.ModelId,
            EmbeddingDimensions = EmbeddingService.Dimensions, ChunkingVersion = "2.0", CreatedDate = DateTime.UtcNow
        });

        // One chunk in iaOrder sharing the same body text under a different case
        db.LitigationOrderChunks.Add(new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = iaDoc.LitigationOrderDocumentId,
            LitigationCaseOrderId = iaOrder.LitigationCaseOrderId, LitigationCaseId = iaCase.LitigationCaseId,
            CaseNumber = iaCase.CaseNumber, Court = iaCase.Court, OrderType = iaOrder.OrderType, OrderDate = iaOrder.OrderDate,
            ChunkIndex = 0, PageNumber = 1, ChunkText = $"{iaHeader}\n\n{sharedBody}",
            Embedding = new SqlVector<float>(Axis(0)), EmbeddingModel = EmbeddingService.ModelId,
            EmbeddingDimensions = EmbeddingService.Dimensions, ChunkingVersion = "2.0", CreatedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var retriever = new LitigationDocumentRetriever(db, ChatRetrievalOptions.Default);
        var caseIds = new[] { parentCase.LitigationCaseId, iaCase.LitigationCaseId };

        // Latest orders: parentOrder deduplicates duplicate chunks internally, and iaOrder is preserved
        var latest = await retriever.GetLatestOrderChunksAsync(request.RequestId, caseIds, 5, 2, CancellationToken.None);
        Assert.Equal(2, latest.Count);
        Assert.Contains(latest, c => c.LitigationCaseOrderId == parentOrder.LitigationCaseOrderId);
        Assert.Contains(latest, c => c.LitigationCaseOrderId == iaOrder.LitigationCaseOrderId);

        // Decision passages: parentCase deduplicates duplicate passages internally, and iaCase is preserved
        var decision = await retriever.GetDecisionPassagesAsync(request.RequestId, caseIds, 5, CancellationToken.None);
        Assert.Equal(2, decision.Count);
        Assert.Contains(decision, c => c.LitigationCaseId == parentCase.LitigationCaseId);
        Assert.Contains(decision, c => c.LitigationCaseId == iaCase.LitigationCaseId);
    }

    [Fact]
    public async Task Worker_continuously_drains_outdated_documents_across_batches_without_restart_and_recovers_interrupted_work()
    {
        await using var db = CreateContext();
        var (request, _, _) = await SeedOrderAsync(db, "WK_INIT");

        // Seed 5 outdated documents (with version "1.0"), with sweep batch limit = 2 (5 > 2)
        var outdatedDocs = new List<LitigationOrderDocument>();
        for (var i = 1; i <= 5; i++)
        {
            var (_, c, o) = await SeedOrderAsync(db, $"WK_{i}", request.RequestId);
            var doc = await SeedDownloadedDocumentAsync(db, o.LitigationCaseOrderId, $"--- Page 1 (native) ---\nThis is order text for document number {i} that is definitely longer than fifty characters to ensure valid chunking.");
            db.LitigationOrderChunks.Add(new LitigationOrderChunk
            {
                RequestId = request.RequestId,
                LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
                LitigationCaseOrderId = o.LitigationCaseOrderId,
                LitigationCaseId = c.LitigationCaseId,
                ChunkIndex = 0, PageNumber = 1,
                ChunkText = $"Outdated doc {i} text",
                Embedding = new SqlVector<float>(Axis(0)),
                EmbeddingModel = EmbeddingService.ModelId,
                EmbeddingDimensions = EmbeddingService.Dimensions,
                ChunkingVersion = "1.0",
                CreatedDate = DateTime.UtcNow.AddDays(-10)
            });
            doc.ChunkingStatus = ChunkingStatus.Chunked;
            outdatedDocs.Add(doc);
        }
        await db.SaveChangesAsync();
        var targetDocIds = outdatedDocs.Select(d => d.LitigationOrderDocumentId).ToList();
        var seededChunksCount = await db.LitigationOrderChunks.CountAsync(c => targetDocIds.Contains(c.LitigationOrderDocumentId));
        Assert.Equal(5, seededChunksCount);

        var queue = new LitigationOrderChunkingQueue();
        var stubEmbedding = new StubEmbeddingService(n => Enumerable.Range(0, n).Select(_ => Axis(0)).ToList());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => CreateContext());
        services.AddSingleton(queue);
        services.AddSingleton<EmbeddingService>(stubEmbedding);
        services.AddScoped<LitigationOrderChunkingOrchestrator>();
        var sp = services.BuildServiceProvider();

        var options = new LitigationChunkingOptions
        {
            MaxConcurrentChunking = 2,
            OutdatedBatchSize = 2,
            QueueBackpressureLimit = 2,
            SweepInterval = TimeSpan.FromMinutes(10),
            BatchThrottleInterval = TimeSpan.FromMilliseconds(50),
            RequestId = request.RequestId
        };

        var worker = new LitigationOrderChunkingWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            queue,
            NullLogger<LitigationOrderChunkingWorker>.Instance,
            Microsoft.Extensions.Options.Options.Create(options));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // All 5 documents must be drained and re-embedded to version "2.0" without restart
        var timeout = DateTime.UtcNow.AddSeconds(20);
        var allDrained = false;
        while (DateTime.UtcNow < timeout)
        {
            await using var verifyDb = CreateContext();
            var migratedCount = await verifyDb.LitigationOrderChunks.AsNoTracking()
                .Where(c => targetDocIds.Contains(c.LitigationOrderDocumentId) && c.ChunkingVersion == "2.0")
                .Select(c => c.LitigationOrderDocumentId)
                .Distinct()
                .CountAsync();

            if (migratedCount == 5)
            {
                allDrained = true;
                break;
            }
            await Task.Delay(100);
        }

        Assert.True(allDrained, "Continuous draining must process all 5 outdated documents across batches without restart.");
        await worker.StopAsync(CancellationToken.None);

        // Seed doc 6, set to Pending (simulating work enqueued or mid-restart before completion)
        var (_, case6, order6) = await SeedOrderAsync(db, "RST", request.RequestId);
        var doc6 = await SeedDownloadedDocumentAsync(db, order6.LitigationCaseOrderId, "--- Page 1 (native) ---\nThis is order text for document 6 restart recovery that is definitely longer than fifty characters.");
        doc6.ChunkingStatus = ChunkingStatus.Pending;
        await db.SaveChangesAsync();

        // Fresh worker on a fresh queue (simulating complete application restart)
        var restartQueue = new LitigationOrderChunkingQueue();
        var restartServices = new ServiceCollection();
        restartServices.AddLogging();
        restartServices.AddScoped(_ => CreateContext());
        restartServices.AddSingleton(restartQueue);
        restartServices.AddSingleton<EmbeddingService>(stubEmbedding);
        restartServices.AddScoped<LitigationOrderChunkingOrchestrator>();
        var restartSp = restartServices.BuildServiceProvider();

        var restartWorker = new LitigationOrderChunkingWorker(
            restartSp.GetRequiredService<IServiceScopeFactory>(),
            restartQueue,
            NullLogger<LitigationOrderChunkingWorker>.Instance,
            Microsoft.Extensions.Options.Options.Create(options));

        using var restartCts = new CancellationTokenSource();
        await restartWorker.StartAsync(restartCts.Token);

        var doc6Timeout = DateTime.UtcNow.AddSeconds(20);
        var doc6Migrated = false;
        while (DateTime.UtcNow < doc6Timeout)
        {
            await using var verifyDb = CreateContext();
            var chunk = await verifyDb.LitigationOrderChunks.AsNoTracking()
                .FirstOrDefaultAsync(c => c.LitigationOrderDocumentId == doc6.LitigationOrderDocumentId && c.ChunkingVersion == "2.0");
            if (chunk is not null)
            {
                doc6Migrated = true;
                break;
            }
            await Task.Delay(100);
        }

        Assert.True(doc6Migrated, "Document 6 must be recovered on restart and chunked to version 2.0.");
        await restartWorker.StopAsync(CancellationToken.None);
    }
}

