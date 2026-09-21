using System.Net;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers LitigationOrderDocumentService's crash-safety, concurrency-safety, retention/expiry and
/// PDF-signature-validation guarantees (#243/LIT-03) — mirrors LitigationCasePersistenceServiceTests'/
/// LitigationSearchJobServiceTests' shapes closely on purpose: same lease/claim pattern, same
/// "real service, stubbed HTTP" testing style, same real-concurrency-via-two-AppDbContexts approach.</summary>
public class LitigationOrderDocumentServiceTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "litigation-order-doc-tests-" + Guid.NewGuid().ToString("N"));

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

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

    private static readonly BprLitigationOptions DefaultOptions = new()
    {
        BaseUrl = "https://bpr.example/", Id = "app", SecretKey = "secret", OrderRetentionDays = 7, MaxOrderPdfBytes = 50 * 1024 * 1024
    };

    // A fake DNS map, never real network/DNS — see BprLitigationClientTests' own copy of this reasoning.
    // "bpr.example" resolves to an RFC 5737 documentation address (public-looking, reserved, never blocked).
    private static Task<IPAddress[]> FakeResolver(string host, CancellationToken ct) =>
        string.Equals(host, "bpr.example", StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") })
            : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));

    private LitigationOrderDocumentService NewService(AppDbContext db, StubHandler handler, LitigationOrderDocumentQueue? queue = null, BprLitigationOptions? options = null)
    {
        var opts = options ?? DefaultOptions;
        var client = new BprLitigationClient(
            new HttpClient(handler) { BaseAddress = new Uri(opts.BaseUrl) }, Options.Create(opts), NullLogger<BprLitigationClient>.Instance, FakeResolver);
        var reservations = new StorageReservationManager(db, Options.Create(new LargeArchiveUploadOptions()), NullLogger<StorageReservationManager>.Instance);
        var extractor = new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance, tesseractExePath: @"C:\not-installed\tesseract.exe");
        return new LitigationOrderDocumentService(
            db, client, reservations, extractor, queue ?? new LitigationOrderDocumentQueue(), Options.Create(opts),
            new FakeEnv(_tempDir), NullLogger<LitigationOrderDocumentService>.Instance);
    }

    private static void StubAuthenticate(StubHandler handler) =>
        handler.OnPath("sec/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"jwt":"token-1"}""", Encoding.UTF8, "application/json")
        });

    /// <summary>A real, parseable single-page PDF with a genuine text layer — extractable natively via
    /// PdfPig, well over MinCharsPerPageForNativeText, so tests never touch the Tesseract OCR fallback at
    /// all. Same approach as FilingBatchProcessorChunkingTriggerTests.</summary>
    private static byte[] ExtractablePdfBytes(string bodyText)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        MCAROC_Analysis.Services.Dossier.DossierFonts.Register(WebRoot());
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.Content().Text(bodyText).FontSize(11);
            });
        }).GeneratePdf();
    }

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    private static async Task<(McaRequest Request, LitigationCase Case, LitigationCaseOrder Order)> SeedOrderAsync(
        AppDbContext db, string suffix, string pdfUrl)
    {
        var client = new Client { ClientCode = "LITD" + suffix + Guid.NewGuid().ToString("N")[..6], ClientName = "Order Doc Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Order Doc Test Company",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"LITD-{suffix}-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var caseRow = new LitigationCase
        {
            RequestId = request.RequestId, Cnr = "TNKP0700013320" + suffix, ProceedingType = "OS",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(caseRow);
        await db.SaveChangesAsync();

        var order = new LitigationCaseOrder
        {
            Case = caseRow, PdfUrl = pdfUrl, OrderDate = "09-01-2025", OrderType = "Judgment", CreatedUtc = DateTime.UtcNow
        };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();

        return (request, caseRow, order);
    }

    private static async Task<LitigationOrderDocument> SeedDocumentAsync(
        AppDbContext db, long litigationCaseOrderId, DateTime retainedUntilUtc, LitigationOrderDocumentStatus status = LitigationOrderDocumentStatus.Pending)
    {
        var document = new LitigationOrderDocument
        {
            LitigationCaseOrderId = litigationCaseOrderId, Status = status, RetainedUntilUtc = retainedUntilUtc, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    // ── Happy path ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAndExtractAsync_downloads_validates_and_extracts_text_from_a_valid_PDF()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "A1", "https://bpr.example/orders/a1.pdf");
        var document = await SeedDocumentAsync(db, order.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5));

        // Well over MinCharsPerPageForNativeText (80) so this exercises native extraction, not the OCR
        // fallback — same technique as FilingBatchProcessorChunkingTriggerTests.
        var pdfBytes = ExtractablePdfBytes(
            "This is the genuine order text for case A1, retained well past the eighty character native-text threshold so OCR never runs.");
        var handler = new StubHandler();
        StubAuthenticate(handler);
        handler.OnPath("orders/a1.pdf", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pdfBytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") } }
        });
        var service = NewService(db, handler);

        await service.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationOrderDocuments.FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderDocumentStatus.Downloaded, reloaded.Status);
        Assert.NotNull(reloaded.StoragePath);
        Assert.True(File.Exists(reloaded.StoragePath));
        Assert.Equal(pdfBytes.LongLength, reloaded.FileSizeBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdfBytes)), reloaded.FileHash);
        Assert.Equal(FilingDocumentProcessingStatus.TextExtracted, reloaded.TextExtractionStatus);
        Assert.Equal(TextExtractionMethod.Native, reloaded.TextExtractionMethod);
        Assert.Contains("genuine order text for case A1", reloaded.ExtractedText);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_marks_Failed_with_a_reason_on_a_non_PDF_response()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "A2", "https://bpr.example/orders/a2.pdf");
        var document = await SeedDocumentAsync(db, order.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5));

        var handler = new StubHandler();
        StubAuthenticate(handler);
        handler.OnPath("orders/a2.pdf", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>expired link</html>", Encoding.UTF8, "text/html")
        });
        var service = NewService(db, handler);

        await service.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationOrderDocuments.FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderDocumentStatus.Failed, reloaded.Status); // retryable, never falsely "complete"
        Assert.Contains("PDF signature", reloaded.FailureReason);
        Assert.Null(reloaded.StoragePath);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_marks_Failed_when_the_response_exceeds_MaxOrderPdfBytes()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "A3", "https://bpr.example/orders/a3.pdf");
        var document = await SeedDocumentAsync(db, order.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5));

        var handler = new StubHandler();
        StubAuthenticate(handler);
        handler.OnPath("orders/a3.pdf", _ =>
        {
            var content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46, 0x2D]);
            content.Headers.ContentLength = 10_000_000;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var tinyCapOptions = new BprLitigationOptions
        {
            BaseUrl = DefaultOptions.BaseUrl, Id = DefaultOptions.Id, SecretKey = DefaultOptions.SecretKey,
            OrderRetentionDays = 7, MaxOrderPdfBytes = 1024
        };
        var service = NewService(db, handler, options: tinyCapOptions);

        await service.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationOrderDocuments.FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderDocumentStatus.Failed, reloaded.Status);
        Assert.Contains("1,024-byte cap", reloaded.FailureReason);
    }

    // ── Retention / expiry ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAndExtractAsync_expires_immediately_without_attempting_a_download_once_retention_has_passed()
    {
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "B1", "https://bpr.example/orders/b1.pdf");
        var document = await SeedDocumentAsync(db, order.LitigationCaseOrderId, DateTime.UtcNow.AddMinutes(-1)); // already past

        var handler = new StubHandler(); // no routes stubbed — any call would return 404 "no stub for"
        var service = NewService(db, handler);

        await service.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        Assert.Empty(handler.Requests); // never even tried — no wasted vendor call for a hopeless attempt
        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationOrderDocuments.FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderDocumentStatus.Expired, reloaded.Status);
    }

    // ── Concurrency ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAndExtractAsync_two_concurrent_contexts_only_one_wins_the_claim()
    {
        await using var setupDb = CreateContext();
        var (_, _, order) = await SeedOrderAsync(setupDb, "C1", "https://bpr.example/orders/c1.pdf");
        var document = await SeedDocumentAsync(setupDb, order.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5));

        var pdfBytes = ExtractablePdfBytes("Concurrency test order text.");
        var callCount = 0;
        var handlerA = new StubHandler();
        StubAuthenticate(handlerA);
        handlerA.OnPath("orders/c1.pdf", _ =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdfBytes) };
        });
        var handlerB = new StubHandler();
        StubAuthenticate(handlerB);
        handlerB.OnPath("orders/c1.pdf", _ =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdfBytes) };
        });

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();
        var serviceA = NewService(dbA, handlerA);
        var serviceB = NewService(dbB, handlerB);

        await Task.WhenAll(
            serviceA.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None),
            serviceB.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None));

        // Exactly one attempt wins the lease (RowVersion-protected TryClaimAsync) — the loser's own
        // DownloadAndExtractAsync sees claimable=false and returns without ever calling the vendor.
        Assert.Equal(1, callCount);
        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationOrderDocuments.FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderDocumentStatus.Downloaded, reloaded.Status);
    }

    [Fact]
    public async Task DownloadAndExtractAsync_a_stale_workers_late_completion_after_a_mid_flight_takeover_never_corrupts_the_winners_file()
    {
        // Regression for PR #253 review finding 2: worker A claims, then (while still slowly mid-download)
        // its lease genuinely expires and a second worker B takes over, downloads, and publishes ITS OWN
        // bytes. A then finishes its own download and tries to publish too. Before the fix, both wrote to the
        // SAME fixed path ({id}.pdf) and only the DB row was RowVersion-protected — whichever of A/B wrote
        // its file to disk LAST could leave the file mismatched with whatever hash the database (correctly)
        // recorded. This proves the fix: per-claim file paths mean A and B can never write the same file, and
        // A's lease-guarded publish is rejected outright (0 rows), so A's own file is deleted and never
        // touches the row B already published — DB hash and on-disk bytes always agree.
        await using var db = CreateContext();
        var (_, _, order) = await SeedOrderAsync(db, "S1", "https://bpr.example/orders/s1.pdf");
        var document = await SeedDocumentAsync(db, order.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5));

        var pdfBytesA = ExtractablePdfBytes(
            "Stale worker A's bytes — must never end up as the published file on disk even though A finishes its own download.");
        var pdfBytesB = ExtractablePdfBytes(
            "Winning worker B's bytes — this is what must be published and must exactly match the database's recorded hash.");

        var handlerA = new StubHandler();
        StubAuthenticate(handlerA);
        handlerA.OnPath("orders/s1.pdf", _ =>
        {
            // The interleaving point itself: A's lease is backdated to already-expired on an independent
            // connection — exactly what real wall-clock elapsed time while A was still (slowly) downloading
            // would produce — then a full, real worker B claims and completes synchronously, right here,
            // before control ever returns to A.
            using (var takeoverDb = CreateContext())
            {
                takeoverDb.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId)
                    .ExecuteUpdate(s => s.SetProperty(d => d.LeaseExpiresUtc, DateTime.UtcNow.AddSeconds(-1)));
            }

            var handlerB = new StubHandler();
            StubAuthenticate(handlerB);
            handlerB.OnPath("orders/s1.pdf", __ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdfBytesB) });
            using var dbB = CreateContext();
            var serviceB = NewService(dbB, handlerB);
            serviceB.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None).GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdfBytesA) };
        });

        var serviceA = NewService(db, handlerA); // A's own claim already happened before this call started
        await serviceA.DownloadAndExtractAsync(document.LitigationOrderDocumentId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationOrderDocuments.FirstAsync(d => d.LitigationOrderDocumentId == document.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderDocumentStatus.Downloaded, reloaded.Status);
        var expectedHashB = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdfBytesB));
        Assert.Equal(expectedHashB, reloaded.FileHash); // B's hash, never A's
        Assert.NotNull(reloaded.StoragePath);
        var onDisk = await File.ReadAllBytesAsync(reloaded.StoragePath!);
        Assert.Equal(pdfBytesB, onDisk); // the file ON DISK matches what the DB records — no A/B mismatch
    }

    // ── Recovery ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverStaleWorkAsync_enqueues_Pending_Failed_and_lease_expired_documents_but_schedules_a_delayed_retry_for_a_live_lease()
    {
        await using var db = CreateContext();
        var queue = new LitigationOrderDocumentQueue();
        var handler = new StubHandler();
        var service = NewService(db, handler, queue);

        var (_, _, pendingOrder) = await SeedOrderAsync(db, "R1", "https://bpr.example/orders/r1.pdf");
        var pendingDocument = await SeedDocumentAsync(db, pendingOrder.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5));

        var (_, _, failedOrder) = await SeedOrderAsync(db, "R2", "https://bpr.example/orders/r2.pdf");
        var failedDocument = await SeedDocumentAsync(db, failedOrder.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5), LitigationOrderDocumentStatus.Failed);

        var (_, _, expiredLeaseOrder) = await SeedOrderAsync(db, "R3", "https://bpr.example/orders/r3.pdf");
        var expiredLeaseDocument = await SeedDocumentAsync(db, expiredLeaseOrder.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5), LitigationOrderDocumentStatus.InProgress);
        expiredLeaseDocument.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        var (_, _, liveLeaseOrder) = await SeedOrderAsync(db, "R4", "https://bpr.example/orders/r4.pdf");
        var liveLeaseDocument = await SeedDocumentAsync(db, liveLeaseOrder.LitigationCaseOrderId, DateTime.UtcNow.AddDays(5), LitigationOrderDocumentStatus.InProgress);
        // Generous relative to the immediate-drain window below — real DB round-trips seeding the other
        // three rows above can themselves eat into a too-tight margin.
        liveLeaseDocument.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(2);
        await db.SaveChangesAsync();

        var count = await service.RecoverStaleWorkAsync(CancellationToken.None);
        Assert.True(count >= 3); // query is global to the (shared, real) test DB, not scoped to this test

        var immediate = await DrainAvailableAsync(queue, TimeSpan.FromMilliseconds(200));
        Assert.Contains(pendingDocument.LitigationOrderDocumentId, immediate);
        Assert.Contains(failedDocument.LitigationOrderDocumentId, immediate);
        Assert.Contains(expiredLeaseDocument.LitigationOrderDocumentId, immediate);
        Assert.DoesNotContain(liveLeaseDocument.LitigationOrderDocumentId, immediate); // live lease — not yet

        var eventual = await DrainAvailableAsync(queue, TimeSpan.FromSeconds(3));
        Assert.Contains(liveLeaseDocument.LitigationOrderDocumentId, eventual); // shows up once its lease expires
    }

    private static async Task<List<long>> DrainAvailableAsync(LitigationOrderDocumentQueue queue, TimeSpan window)
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

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(string PathSuffix, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public void OnPath(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((pathSuffix, respond));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var route = _routes.FirstOrDefault(r => request.RequestUri!.AbsolutePath.Contains(r.PathSuffix, StringComparison.Ordinal));
            return Task.FromResult(route.Respond is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + request.RequestUri) }
                : route.Respond(request));
        }
    }
}
