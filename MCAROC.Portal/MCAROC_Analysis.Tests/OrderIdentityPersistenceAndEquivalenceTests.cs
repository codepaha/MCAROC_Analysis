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
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
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
        await using var db = CreateContext();
        var (_, caseRow) = await SeedCaseAsync(db, "S3");

        var date = "2025-07-01";
        var oldHash = OrderContentHasher.ComputeBytesHash(Encoding.UTF8.GetBytes("Original order text with preliminary directions"));
        var newHash = OrderContentHasher.ComputeBytesHash(Encoding.UTF8.GetBytes("Corrected order text after review with modified directions"));

        var order = new LitigationCaseOrder
        {
            Case = caseRow,
            Source = "Bpr",
            OrderDate = date,
            OrderType = "Order",
            OrderKey = OrderKeyBuilder.ForBpr("https://bpr.example/order_v1.pdf", date, "Order"),
            ContentHash = oldHash,
            Version = 1,
            IsSuperseded = false,
            PdfUrl = "https://bpr.example/order_v1.pdf",
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();

        var docV1 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = oldHash,
            Version = 1,
            IsSuperseded = false,
            FileHash = oldHash,
            ExtractedText = "Original order text with preliminary directions",
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(docV1);
        await db.SaveChangesAsync();

        var chunkV1 = new LitigationOrderChunk
        {
            RequestId = caseRow.RequestId,
            LitigationCaseId = caseRow.LitigationCaseId,
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            LitigationOrderDocumentId = docV1.LitigationOrderDocumentId,
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

        // Simulate downloading a new version with changed content under the same order identity
        var decision = OrderVersionRule.Evaluate(order.ContentHash, order.Version, newHash);
        Assert.Equal(OrderVersionDecisionType.Superseded, decision.Decision);
        Assert.Equal(2, decision.VersionNumber);
        Assert.True(decision.PreviousMarkedSuperseded);

        // Mark previous document and chunks superseded
        await db.LitigationOrderDocuments
            .Where(d => d.LitigationCaseOrderId == order.LitigationCaseOrderId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsSuperseded, true));

        await db.LitigationOrderChunks
            .Where(c => c.LitigationCaseOrderId == order.LitigationCaseOrderId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsSuperseded, true));

        // Create new document version
        var docV2 = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentHash = newHash,
            Version = decision.VersionNumber,
            IsSuperseded = false,
            FileHash = newHash,
            ExtractedText = "Corrected order text after review with modified directions",
            TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(7),
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(docV2);

        order.Version = decision.VersionNumber;
        order.ContentHash = newHash;
        await db.SaveChangesAsync();

        var chunkV2 = new LitigationOrderChunk
        {
            RequestId = caseRow.RequestId,
            LitigationCaseId = caseRow.LitigationCaseId,
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            LitigationOrderDocumentId = docV2.LitigationOrderDocumentId,
            ChunkIndex = 0,
            ChunkText = "Corrected order text chunk",
            IsSuperseded = false,
            Embedding = new SqlVector<float>(new float[768]),
            EmbeddingModel = "test",
            EmbeddingDimensions = 768,
            ChunkingVersion = "1.0",
            CreatedDate = DateTime.UtcNow
        };
        db.LitigationOrderChunks.Add(chunkV2);
        await db.SaveChangesAsync();

        // Verify retrieval filters out superseded chunks
        await using var verify = CreateContext();
        var retriever = new LitigationDocumentRetriever(verify, ChatRetrievalOptions.Default);
        var activeChunks = await retriever.GetLatestOrderChunksAsync(caseRow.RequestId, [caseRow.LitigationCaseId], 10, 10, CancellationToken.None);

        Assert.Single(activeChunks);
        Assert.Equal("Corrected order text chunk", activeChunks[0].ChunkText);
        Assert.False(activeChunks[0].IsSuperseded);

        var allDocs = await verify.LitigationOrderDocuments
            .Where(d => d.LitigationCaseOrderId == order.LitigationCaseOrderId)
            .OrderBy(d => d.Version)
            .ToListAsync();

        Assert.Equal(2, allDocs.Count);
        Assert.True(allDocs[0].IsSuperseded); // Version 1 kept, marked superseded
        Assert.Equal(1, allDocs[0].Version);
        Assert.False(allDocs[1].IsSuperseded); // Version 2 active
        Assert.Equal(2, allDocs[1].Version);
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
}

