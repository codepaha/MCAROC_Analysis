using System.Net;
using System.Security.Cryptography;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Orders;
using MCAROC_Analysis.Services.LitigationModule.Sources.Orders;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class OrderIdentityPersistenceAndEquivalenceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(McaRequest Request, LitigationCase Case)> SeedCaseAsync(AppDbContext db, string suffix)
    {
        var client = new Client
        {
            ClientCode = "EQ" + suffix + Guid.NewGuid().ToString("N")[..6],
            ClientName = "Equivalence Test Co",
            CreatedDate = DateTime.UtcNow
        };
        db.Clients.Add(client);

        var request = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "Equivalence Test Target Company",
            Cin = "U45203OR1995PLC003982",
            RequestNumber = $"EQ-{suffix}-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var caseRow = new LitigationCase
        {
            RequestId = request.RequestId,
            Cnr = "TNKP07" + suffix,
            CaseNumber = "CP/100/2025",
            ProceedingType = "CP",
            Court = "NCLT Chennai",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(caseRow);
        await db.SaveChangesAsync();

        return (request, caseRow);
    }

    [Fact]
    public async Task Two_distinct_same_day_orders_both_survive_persistence()
    {
        await using var db = CreateContext();
        var (_, caseRow) = await SeedCaseAsync(db, "S1");

        var date = "2025-05-15";
        var hash1 = OrderContentHasher.ComputeBytesHash(Encoding.UTF8.GetBytes("Order 1 daily proceedings"));
        var hash2 = OrderContentHasher.ComputeBytesHash(Encoding.UTF8.GetBytes("Order 2 interim stay granted"));

        var order1 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Bpr",
            OrderDate = date,
            OrderType = "Daily Order",
            OrderKey = OrderKeyBuilder.ForBpr("https://bpr.example/doc1.pdf", date, "Daily Order"),
            ContentHash = hash1,
            Version = 1,
            IsSuperseded = false,
            PdfUrl = "https://bpr.example/doc1.pdf",
            CreatedUtc = DateTime.UtcNow
        };

        var order2 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Bpr",
            OrderDate = date,
            OrderType = "Interim Order",
            OrderKey = OrderKeyBuilder.ForBpr("https://bpr.example/doc2.pdf", date, "Interim Order"),
            ContentHash = hash2,
            Version = 1,
            IsSuperseded = false,
            PdfUrl = "https://bpr.example/doc2.pdf",
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationCaseOrders.AddRange(order1, order2);
        await db.SaveChangesAsync();

        var doc1 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order1.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = hash1,
            Version = 1,
            IsSuperseded = false,
            FileHash = hash1,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        var doc2 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order2.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = hash2,
            Version = 1,
            IsSuperseded = false,
            FileHash = hash2,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationOrderDocuments.AddRange(doc1, doc2);
        await db.SaveChangesAsync();

        // Run backfill service
        var backfill = new OrderIdentityBackfillService(db, NullLogger<OrderIdentityBackfillService>.Instance);
        await backfill.BackfillAsync(caseRow.LitigationCaseId);

        await using var verify = CreateContext();
        var reloaded1 = await verify.LitigationCaseOrders.FindAsync(order1.LitigationCaseOrderId);
        var reloaded2 = await verify.LitigationCaseOrders.FindAsync(order2.LitigationCaseOrderId);

        Assert.NotNull(reloaded1);
        Assert.NotNull(reloaded2);

        // Neither collapses into the other; both survive
        Assert.Null(reloaded1!.CollapsedIntoOrderId);
        Assert.False(reloaded1.IsSuperseded);

        Assert.Null(reloaded2!.CollapsedIntoOrderId);
        Assert.False(reloaded2.IsSuperseded);
    }

    [Fact]
    public async Task Cross_source_equal_hash_collapses_to_one_order()
    {
        await using var db = CreateContext();
        var (_, caseRow) = await SeedCaseAsync(db, "S2");

        var date = "2025-06-20";
        var identicalBytes = Encoding.UTF8.GetBytes("Identical court pronouncement body text");
        var contentHash = OrderContentHasher.ComputeBytesHash(identicalBytes);

        var vendorOrder = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Bpr",
            OrderDate = date,
            OrderType = "Judgment",
            OrderKey = OrderKeyBuilder.ForBpr("https://vendor.example/j1.pdf", date, "Judgment"),
            ContentHash = contentHash,
            Version = 1,
            IsSuperseded = false,
            PdfUrl = "https://vendor.example/j1.pdf",
            CreatedUtc = DateTime.UtcNow
        };

        var portalOrder = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Drt",
            OrderDate = date,
            OrderType = "final",
            OrderKey = OrderKeyBuilder.ForDrt(1, 10, "100", 2025, "final", DateOnly.Parse(date), "https://drt.gov.in/pdf1.php?file=ZHJ0Y291cnQvb3JkZXJfMTAwLnBkZg=="),
            ContentHash = contentHash,
            Version = 1,
            IsSuperseded = false,
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationCaseOrders.AddRange(vendorOrder, portalOrder);
        await db.SaveChangesAsync();

        // Vendor document has extracted text; portal document does not
        var vendorDoc = new LitigationOrderDocument
        {
            LitigationCaseOrderId = vendorOrder.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = contentHash,
            Version = 1,
            IsSuperseded = false,
            FileHash = contentHash,
            ExtractedText = "Extracted order text body",
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        var portalDoc = new LitigationOrderDocument
        {
            LitigationCaseOrderId = portalOrder.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = contentHash,
            Version = 1,
            IsSuperseded = false,
            FileHash = contentHash,
            ExtractedText = null,
            TextExtractionStatus = FilingDocumentProcessingStatus.Discovered,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationOrderDocuments.AddRange(vendorDoc, portalDoc);
        await db.SaveChangesAsync();

        var portalChunk = new LitigationOrderChunk
        {
            RequestId = caseRow.RequestId,
            LitigationCaseId = caseRow.LitigationCaseId,
            LitigationCaseOrderId = portalOrder.LitigationCaseOrderId,
            LitigationOrderDocumentId = portalDoc.LitigationOrderDocumentId,
            ChunkIndex = 0,
            ChunkText = "Portal chunk",
            IsSuperseded = false,
            Embedding = new SqlVector<float>(new float[768]),
            EmbeddingModel = "test",
            EmbeddingDimensions = 768,
            ChunkingVersion = "1.0",
            CreatedDate = DateTime.UtcNow
        };
        db.LitigationOrderChunks.Add(portalChunk);
        await db.SaveChangesAsync();

        // Run backfill to collapse cross-source identical duplicate
        var backfill = new OrderIdentityBackfillService(db, NullLogger<OrderIdentityBackfillService>.Instance);
        var modified = await backfill.BackfillAsync(caseRow.LitigationCaseId);
        Assert.True(modified > 0);

        await using var verify = CreateContext();
        var survivor = await verify.LitigationCaseOrders.FindAsync(vendorOrder.LitigationCaseOrderId);
        var collapsed = await verify.LitigationCaseOrders.FindAsync(portalOrder.LitigationCaseOrderId);
        var collapsedDoc = await verify.LitigationOrderDocuments.FindAsync(portalDoc.LitigationOrderDocumentId);
        var chunk = await verify.LitigationOrderChunks.FindAsync(portalChunk.LitigationOrderChunkId);

        Assert.NotNull(survivor);
        Assert.NotNull(collapsed);
        Assert.NotNull(collapsedDoc);
        Assert.NotNull(chunk);

        // Vendor order had extracted text, so it survived
        Assert.Null(survivor!.CollapsedIntoOrderId);
        Assert.False(survivor.IsSuperseded);

        // Portal order collapsed into vendor order
        Assert.Equal(vendorOrder.LitigationCaseOrderId, collapsed!.CollapsedIntoOrderId);
        Assert.True(collapsed.IsSuperseded);
        Assert.True(collapsedDoc!.IsSuperseded);
        Assert.True(chunk!.IsSuperseded);
    }

    [Fact]
    public async Task Changed_content_under_same_identity_creates_new_version()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "order-ver-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await using var db = CreateContext();
            var (_, caseRow) = await SeedCaseAsync(db, "S3");

            var date = "2025-07-01";
            var textV1 = "Original order text with preliminary directions and orders";
            var textV2 = "Corrected order text after review with modified directions and orders";
            var oldHash = OrderContentHasher.ComputeHtmlTextHash(textV1);
            var newHash = OrderContentHasher.ComputeHtmlTextHash(textV2);

            var currentText = textV1;
            var client = new TestOrderSourceClient
            {
                DownloadHandler = _ => OrderSourceDownloadResult.OrderText(currentText)
            };

            var order = new LitigationCaseOrder
            {
                Case = caseRow,
                Source = "Test",
                OrderDate = date,
                OrderType = "Order",
                OrderKey = $"https://example.com/order1|{date}|Order",
                PdfUrl = "https://example.com/order1",
                CreatedUtc = DateTime.UtcNow
            };
            db.LitigationCaseOrders.Add(order);
            await db.SaveChangesAsync();

            var doc = new LitigationOrderDocument
            {
                LitigationCaseOrderId = order.LitigationCaseOrderId,
                Status = LitigationOrderDocumentStatus.Pending,
                RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
                CreatedUtc = DateTime.UtcNow
            };
            db.LitigationOrderDocuments.Add(doc);
            await db.SaveChangesAsync();

            // 1. Initial download and publication of version 1 through the service
            var service1 = CreateOrderDocService(db, client, tempDir);
            await service1.DownloadAndExtractAsync(doc.LitigationOrderDocumentId, CancellationToken.None);

            await using (var verifyV1 = CreateContext())
            {
                var v1Doc = await verifyV1.LitigationOrderDocuments.SingleAsync(d => d.LitigationOrderDocumentId == doc.LitigationOrderDocumentId);
                Assert.Equal(LitigationOrderDocumentStatus.Downloaded, v1Doc.Status);
                Assert.Equal(1, v1Doc.Version);
                Assert.Equal(oldHash, v1Doc.ContentHash);
                Assert.False(v1Doc.IsSuperseded);
            }

            // Seed a chunk associated with version 1
            var chunkV1 = new LitigationOrderChunk
            {
                RequestId = caseRow.RequestId,
                LitigationCaseId = caseRow.LitigationCaseId,
                LitigationCaseOrderId = order.LitigationCaseOrderId,
                LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
                ChunkIndex = 0,
                ChunkText = "Original order text chunk",
                IsSuperseded = false,
                Embedding = new SqlVector<float>(new float[768]),
                EmbeddingModel = "test",
                EmbeddingDimensions = 768,
                ChunkingVersion = "1.0",
                CreatedDate = DateTime.UtcNow
            };
            db.LitigationOrderChunks.Add(chunkV1);
            await db.SaveChangesAsync();

            // 2. Source changes content: publication of version 2 through the service
            currentText = textV2;
            await using (var updateDb = CreateContext())
            {
                await updateDb.LitigationOrderDocuments
                    .Where(d => d.LitigationOrderDocumentId == doc.LitigationOrderDocumentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, LitigationOrderDocumentStatus.Pending));
            }

            await using (var runDb = CreateContext())
            {
                var service2 = CreateOrderDocService(runDb, client, tempDir);
                await service2.DownloadAndExtractAsync(doc.LitigationOrderDocumentId, CancellationToken.None);
            }

            // 3. Verify actual changed-content publication:
            await using var verify = CreateContext();

            // Original order updated to version 2
            var reloadedOrder = await verify.LitigationCaseOrders.FindAsync(order.LitigationCaseOrderId);
            Assert.NotNull(reloadedOrder);
            Assert.Equal(2, reloadedOrder!.Version);
            Assert.Equal(newHash, reloadedOrder.ContentHash);
            Assert.False(reloadedOrder.IsSuperseded);

            // Exactly 2 documents exist: version 2 active, version 1 archived and superseded
            var allDocs = await verify.LitigationOrderDocuments
                .Where(d => d.LitigationCaseOrderId == order.LitigationCaseOrderId)
                .OrderBy(d => d.Version)
                .ToListAsync();

            Assert.Equal(2, allDocs.Count);

            // Archived version 1
            Assert.Equal(1, allDocs[0].Version);
            Assert.True(allDocs[0].IsSuperseded);
            Assert.Equal(oldHash, allDocs[0].ContentHash);
            Assert.Equal(LitigationOrderDocumentStatus.Downloaded, allDocs[0].Status);

            // Active version 2 (the original doc row updated in place)
            Assert.Equal(2, allDocs[1].Version);
            Assert.False(allDocs[1].IsSuperseded);
            Assert.Equal(newHash, allDocs[1].ContentHash);
            Assert.Equal(doc.LitigationOrderDocumentId, allDocs[1].LitigationOrderDocumentId);
            Assert.Equal(LitigationOrderDocumentStatus.Downloaded, allDocs[1].Status);

            // Chunk from version 1 was marked superseded
            var reloadedChunk = await verify.LitigationOrderChunks.FindAsync(chunkV1.LitigationOrderChunkId);
            Assert.NotNull(reloadedChunk);
            Assert.True(reloadedChunk!.IsSuperseded);

            // Document retriever filters out superseded chunk
            var retriever = new LitigationDocumentRetriever(verify, ChatRetrievalOptions.Default);
            var activeChunks = await retriever.GetLatestOrderChunksAsync(caseRow.RequestId, [caseRow.LitigationCaseId], 10, 10, CancellationToken.None);
            Assert.Empty(activeChunks);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Backfill_is_idempotent_and_collapses_proven_duplicates()
    {
        await using var db = CreateContext();
        var (_, caseRow) = await SeedCaseAsync(db, "S4");

        var date = "2025-08-10";
        var hash = OrderContentHasher.ComputeBytesHash(Encoding.UTF8.GetBytes("Same order from two legacy imports"));

        // Legacy order lacking Source, OrderKey, SourceLocator, Version = 0
        var legacyOrder1 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = null,
            OrderDate = date,
            OrderType = "Daily Order",
            OrderKey = null,
            SourceLocator = null,
            Version = 0,
            PdfUrl = "https://vendor.example/o1.pdf",
            CreatedUtc = DateTime.UtcNow
        };

        var legacyOrder2 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = null,
            OrderDate = date,
            OrderType = "Daily Order",
            OrderKey = null,
            SourceLocator = null,
            Version = 0,
            PdfUrl = "https://vendor.example/o2.pdf",
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationCaseOrders.AddRange(legacyOrder1, legacyOrder2);
        await db.SaveChangesAsync();

        var doc1 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = legacyOrder1.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            FileHash = hash,
            ContentHash = null,
            Version = 0,
            ExtractedText = "Same order text",
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        var doc2 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = legacyOrder2.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            FileHash = hash,
            ContentHash = null,
            Version = 0,
            ExtractedText = "Same order text",
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationOrderDocuments.AddRange(doc1, doc2);
        await db.SaveChangesAsync();

        var backfill = new OrderIdentityBackfillService(db, NullLogger<OrderIdentityBackfillService>.Instance);

        // First run backfills identity and collapses duplicates
        var pass1Count = await backfill.BackfillAsync(caseRow.LitigationCaseId);
        Assert.True(pass1Count > 0);

        await using var verify1 = CreateContext();
        var o1 = await verify1.LitigationCaseOrders.FindAsync(legacyOrder1.LitigationCaseOrderId);
        var o2 = await verify1.LitigationCaseOrders.FindAsync(legacyOrder2.LitigationCaseOrderId);

        Assert.Equal("Bpr", o1!.Source);
        Assert.NotNull(o1.OrderKey);
        Assert.NotNull(o1.SourceLocator);
        Assert.Equal(1, o1.Version);
        Assert.Equal(hash, o1.ContentHash);

        Assert.Equal("Bpr", o2!.Source);
        Assert.NotNull(o2.OrderKey);
        Assert.NotNull(o2.SourceLocator);
        Assert.Equal(1, o2.Version);
        Assert.Equal(hash, o2.ContentHash);

        // Exactly one collapsed into the other
        var isOneCollapsed = (o1.CollapsedIntoOrderId == o2.LitigationCaseOrderId && o1.IsSuperseded) ||
                             (o2.CollapsedIntoOrderId == o1.LitigationCaseOrderId && o2.IsSuperseded);
        Assert.True(isOneCollapsed);

        // Second run must be completely idempotent (0 additional changes)
        var pass2Count = await backfill.BackfillAsync(caseRow.LitigationCaseId);
        Assert.Equal(0, pass2Count);
    }

    [Fact]
    public async Task Zero_AI_calls_for_re_ingested_identical_orders()
    {
        await using var db = CreateContext();
        var (_, caseRow) = await SeedCaseAsync(db, "S5");

        var date = "2025-09-01";
        var hash = OrderContentHasher.ComputeBytesHash(Encoding.UTF8.GetBytes("Final court decision text for AI call suppression test"));

        var order1 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Bpr",
            OrderDate = date,
            OrderType = "Judgment",
            OrderKey = OrderKeyBuilder.ForBpr("https://vendor.example/dec.pdf", date, "Judgment"),
            ContentHash = hash,
            Version = 1,
            IsSuperseded = false,
            PdfUrl = "https://vendor.example/dec.pdf",
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationCaseOrders.Add(order1);
        await db.SaveChangesAsync();

        var doc1 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order1.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = hash,
            FileHash = hash,
            Version = 1,
            IsSuperseded = false,
            ExtractedText = "Final court decision text for AI call suppression test",
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(doc1);
        await db.SaveChangesAsync();

        // 1. Initial CaseAnalysisInput
        var loadedCase = await db.LitigationCases.Include(c => c.Orders).FirstAsync(c => c.LitigationCaseId == caseRow.LitigationCaseId);
        var docs = new Dictionary<long, LitigationOrderDocument> { [order1.LitigationCaseOrderId] = doc1 };
        var inputInitial = LitigationCaseAnalysisInputBuilder.Build(
            loadedCase, docs, "Equivalence Test Target Company", ["Equivalence Test Target Company"], DateOnly.Parse(date));

        var initialEvidenceHash = inputInitial.EvidenceHash;
        Assert.False(string.IsNullOrWhiteSpace(initialEvidenceHash));

        // 2. Re-ingest the same order from DRT (identical content hash) -> collapses
        var order2 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Drt",
            OrderDate = date,
            OrderType = "final",
            OrderKey = OrderKeyBuilder.ForDrt(2, 5, "200", 2025, "final", DateOnly.Parse(date), "https://drt.gov.in/pdf1.php?file=ZHJ0LzIwMjUvMjAwLnBkZg=="),
            ContentHash = hash,
            Version = 1,
            CollapsedIntoOrderId = order1.LitigationCaseOrderId, // collapsed
            IsSuperseded = true,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationCaseOrders.Add(order2);
        await db.SaveChangesAsync();

        var doc2 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order2.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = hash,
            FileHash = hash,
            Version = 1,
            IsSuperseded = true,
            ExtractedText = "Final court decision text for AI call suppression test",
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(doc2);
        await db.SaveChangesAsync();

        // 3. Re-build CaseAnalysisInput after collapsed duplicate addition
        var reloadedCase = await db.LitigationCases.Include(c => c.Orders).FirstAsync(c => c.LitigationCaseId == caseRow.LitigationCaseId);
        var docsAfter = new Dictionary<long, LitigationOrderDocument>
        {
            [order1.LitigationCaseOrderId] = doc1,
            [order2.LitigationCaseOrderId] = doc2
        };

        var inputAfterDuplicate = LitigationCaseAnalysisInputBuilder.Build(
            reloadedCase, docsAfter, "Equivalence Test Target Company", ["Equivalence Test Target Company"], DateOnly.Parse(date));

        // Because the duplicate was collapsed, input orders and EvidenceHash are strictly identical
        Assert.Equal(initialEvidenceHash, inputAfterDuplicate.EvidenceHash);
        Assert.Single(inputAfterDuplicate.Orders);

        // 4. In LitigationAiAnalysisOrchestrator, live chunks also exclude collapsed/superseded orders
        var liveOrderChunks = await db.LitigationOrderChunks
            .Where(x => x.LitigationCaseOrderId == order2.LitigationCaseOrderId
                && !x.IsSuperseded
                && !db.LitigationCaseOrders.Any(o => o.LitigationCaseOrderId == x.LitigationCaseOrderId && (o.CollapsedIntoOrderId != null || o.IsSuperseded)))
            .ToListAsync();

        Assert.Empty(liveOrderChunks); // 0 extra live chunks from the collapsed order
    }

    [Fact]
    public async Task Backfill_retains_unknown_documents_without_collapsing()
    {
        await using var db = CreateContext();
        var (_, caseRow) = await SeedCaseAsync(db, "S6");

        var date = "2025-09-01";

        var order1 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Bpr",
            OrderDate = date,
            OrderType = "Daily Order",
            OrderKey = OrderKeyBuilder.ForBpr("https://vendor.example/u1.pdf", date, "Daily Order"),
            PdfUrl = "https://vendor.example/u1.pdf",
            CreatedUtc = DateTime.UtcNow
        };

        var order2 = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Bpr",
            OrderDate = date,
            OrderType = "Daily Order",
            OrderKey = OrderKeyBuilder.ForBpr("https://vendor.example/u2.pdf", date, "Daily Order"),
            PdfUrl = "https://vendor.example/u2.pdf",
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationCaseOrders.AddRange(order1, order2);
        await db.SaveChangesAsync();

        // Downloaded documents that have NO extracted text, NO file hash, and NO file on disk
        var doc1 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order1.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = null,
            FileHash = null,
            ExtractedText = null,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        var doc2 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order2.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = null,
            FileHash = null,
            ExtractedText = null,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };

        db.LitigationOrderDocuments.AddRange(doc1, doc2);
        await db.SaveChangesAsync();

        // Run backfill service
        var backfill = new OrderIdentityBackfillService(db, NullLogger<OrderIdentityBackfillService>.Instance);
        await backfill.BackfillAsync(caseRow.LitigationCaseId);

        await using var verify = CreateContext();
        var reloaded1 = await verify.LitigationCaseOrders.FindAsync(order1.LitigationCaseOrderId);
        var reloaded2 = await verify.LitigationCaseOrders.FindAsync(order2.LitigationCaseOrderId);

        Assert.NotNull(reloaded1);
        Assert.NotNull(reloaded2);

        // Neither order collapsed: content is unknown so both survive
        Assert.Null(reloaded1!.CollapsedIntoOrderId);
        Assert.False(reloaded1.IsSuperseded);
        Assert.Null(reloaded1.ContentHash);

        Assert.Null(reloaded2!.CollapsedIntoOrderId);
        Assert.False(reloaded2.IsSuperseded);
        Assert.Null(reloaded2.ContentHash);
    }

    private static LitigationOrderDocumentService CreateOrderDocService(
        AppDbContext db,
        IOrderSourceClient sourceClient,
        string tempDir)
    {
        var reservations = new StorageReservationManager(db, Options.Create(new LargeArchiveUploadOptions()), NullLogger<StorageReservationManager>.Instance);
        var extractor = new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance, tesseractExePath: @"C:\not-installed\tesseract.exe");
        var opts = new BprLitigationOptions
        {
            BaseUrl = "https://bpr.example/",
            Id = "app",
            SecretKey = "secret",
            OrderRetentionDays = 7,
            MaxOrderPdfBytes = 50 * 1024 * 1024
        };
        return new LitigationOrderDocumentService(
            db,
            [sourceClient],
            reservations,
            extractor,
            new LitigationOrderDocumentQueue(),
            new LitigationOrderChunkingQueue(),
            Options.Create(opts),
            new FakeEnv(tempDir),
            NullLogger<LitigationOrderDocumentService>.Instance);
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestOrderSourceClient : IOrderSourceClient
    {
        public Func<OrderSourceLocator, OrderSourceDownloadResult> DownloadHandler { get; set; } = _ => OrderSourceDownloadResult.Failed("Not configured");
        public bool CanHandle(OrderSourceLocator locator) => true;
        public Task<OrderSourceDownloadResult> DownloadAsync(OrderSourceLocator locator, long maxBytes = 20971520, CancellationToken ct = default) =>
            Task.FromResult(DownloadHandler(locator));
    }
}

