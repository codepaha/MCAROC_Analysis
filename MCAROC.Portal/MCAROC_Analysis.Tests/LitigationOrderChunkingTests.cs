using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        AppDbContext db, long litigationCaseOrderId, string extractedText)
    {
        var document = new LitigationOrderDocument
        {
            LitigationCaseOrderId = litigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5),
            ExtractedText = extractedText,
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
        }
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
            new LitigationDocumentRetriever(buildDb, ChatRetrievalOptions.Default), stub);

        var context = await contextBuilder.BuildAsync(request.RequestId, "Is the charge enforceable?", CancellationToken.None);

        var litigationSource = Assert.Single(context.Sources, s => s.Type == SourceType.LitigationChunk);
        Assert.StartsWith("L", litigationSource.Tag);
        Assert.Equal(litigationCase.LitigationCaseId, litigationSource.LitigationCaseId);
        Assert.Equal(order.LitigationCaseOrderId, litigationSource.LitigationCaseOrderId);
        Assert.Equal(document.LitigationOrderDocumentId, litigationSource.DocumentId);
        Assert.Equal(1, litigationSource.PageNumber);
        Assert.NotNull(litigationSource.ChunkId);
    }
}
