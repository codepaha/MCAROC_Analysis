using System.Net;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Closes two rounds of the same review finding on PR #224.
/// Round 1 (head <c>0ca705d</c>): "several workers only check <c>bytes &gt;= aggregateCap</c> before
/// starting, then add after finishing" — multiple simultaneous downloads whose declared/individual sizes
/// fit under the plan-time estimate could, once the real bytes came in higher than declared, overshoot the
/// aggregate cap by multiple files' worth because admission wasn't atomic.
/// Round 2 (head <c>be5ca4a</c>): admission became atomic but still let the ONE reservation that crossed
/// the threshold through — bounding the overshoot to one file's worth, but still exceeding a cap that is a
/// configured limit, not an approximate one.
/// This runs the real <see cref="AutoFetchJobService.ProcessAsync"/> download stage (real concurrency, real
/// HTTP-shaped stub responses) rather than unit-testing the budget in isolation, since this is exactly the
/// scenario Codex asked to see covered end to end. The registry deliberately declares sizes far smaller
/// than the real per-file responses, so the plan-time byte-truncation (AutoFetchJobService.BuildDownloadPlan)
/// does not itself remove any documents — every document reaches the live download loop, and the atomic,
/// hard-capped budget in that loop is what must keep real usage at or under the configured limit.</summary>
public class AutoFetchAggregateCapConcurrencyTests : IAsyncLifetime
{
    private const string KeyHex = "6b65792d666f722d7465737473"; // "key-for-tests"
    private const long MaxResponseBytes = 50_000; // 50 KB per-file cap
    private const long AggregateCap = 180_000; // 180 KB — deliberately NOT a multiple of MaxResponseBytes,
        // so the last admitted reservation lands strictly under the cap (150,000) rather than exactly at
        // it, proving the boundary math (150,000 + 50,000 = 200,000 > 180,000 → refused) rather than a
        // coincidental exact fit.
    private const int RealFileBytes = 50_000; // exactly at the per-file cap — "individual sizes fit" per
        // the review, and equal to the worst-case reservation so admission counts are fully deterministic
        // (Commit's actual-minus-worstCase adjustment is a no-op, so no interleaving can admit "extra"
        // downloads beyond what the arithmetic below predicts).
    private const int DocumentCount = 10; // 10 × 50 KB = 500 KB real total, well over the 180 KB aggregate cap
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "autofetch-aggregate-cap-tests-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task Real_concurrent_downloads_whose_declared_sizes_undersell_the_truth_never_exceed_the_configured_cap()
    {
        await using var db = CreateContext();

        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc",
            UserId = "265271",
            MaxResponseBytes = MaxResponseBytes,
            MaxAggregateDownloadBytes = AggregateCap,
            DownloadConcurrency = 8, // deliberately high — the more concurrent workers, the bigger the old race's overshoot would be
            DownloadAttempts = 1
        });
        var client = new ReferenceToolClient(new HttpClient(new StubHandler()), options, NullLogger<ReferenceToolClient>.Instance);
        var storageReservations = new StorageReservationManager(db, Options.Create(new LargeArchiveUploadOptions()), NullLogger<StorageReservationManager>.Instance);

        var jobs = new AutoFetchJobService(
            db, client, options, new FileValidationService(new ExcelSheetReader()),
            null!, null!, new FilingProcessingQueue(), storageReservations,
            new FakeEnv(_tempDir), NullLogger<AutoFetchJobService>.Instance);

        var (_, jobId) = await SeedRequestPastIngestionAsync(db);

        Exception? processException = null;
        try { await jobs.ProcessAsync(jobId, CancellationToken.None); }
        catch (Exception ex) { processException = ex; }

        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);

        Assert.True(job.Status is AutoFetchJobStatus.Completed or AutoFetchJobStatus.CompletedWithWarnings,
            $"Job ended as {job.Status}: {job.FailureReason} / msg={job.StatusMessage} / processException={processException}");

        // The hard bound this fix guarantees: downloaded bytes never exceed AggregateCap, full stop,
        // regardless of DownloadConcurrency. Before round 1's fix, up to DownloadConcurrency (8) files —
        // 400 KB — could land; before round 2's fix, one reservation was still allowed to cross the
        // threshold (200 KB, over the 180 KB cap). Admission is deterministic here (RealFileBytes ==
        // MaxResponseBytes, so Commit's true-up is a no-op and no interleaving can admit more): exactly 3
        // of the 10 documents fit (3 × 50,000 = 150,000 ≤ 180,000); a 4th would need 200,000, which is
        // refused outright rather than admitted because 150,000 alone was still under the cap.
        Assert.Equal(3 * MaxResponseBytes, job.BytesDownloaded);
        Assert.True(job.BytesDownloaded <= AggregateCap,
            $"Downloaded {job.BytesDownloaded} bytes; the configured cap {AggregateCap} must never be exceeded — old designs could reach up to {AggregateCap + 8 * MaxResponseBytes} (round 1) or {AggregateCap + MaxResponseBytes} (round 2).");

        // The cap must have actually been exercised — not all 10 documents' real bytes (500 KB) fit.
        // FilesFailed stays 0: the other 7 were never attempted at all (refused by the budget before any
        // HTTP call), not attempted-and-failed.
        Assert.Equal(3, job.FilesDownloaded);
        Assert.Equal(0, job.FilesFailed);

        var warnings = JsonSerializer.Deserialize<List<string>>(job.WarningsJson) ?? [];
        Assert.Contains(warnings, w => w.Contains("aggregate download limit", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(long RequestId, long JobId)> SeedRequestPastIngestionAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "AGG" + Guid.NewGuid().ToString("N")[..7], ClientName = "Aggregate Cap Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Aggregate Cap Test Company", // distinct from CIN — skips the company-name search lookup
            Cin = "U45203OR1995PLC003982", RequestNumber = $"AGG-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        // Checkpoints already past workbook-fetch and ingestion — ProcessAsync skips straight to the
        // filings download stage, which is the only thing this test needs to exercise.
        var job = new AutoFetchJob
        {
            RequestId = request.RequestId, Cin = request.Cin!, Bid = ReferenceToolClient.ComputeBid(request.Cin!),
            Status = AutoFetchJobStatus.Queued, IncludeFilings = true, MaxDocumentsPerSection = 0,
            RocDocumentId = 999_999_999, IngestionRunId = 999_999_999, WarningsJson = "[]", CreatedUtc = DateTime.UtcNow
        };
        db.AutoFetchJobs.Add(job);
        await db.SaveChangesAsync();

        return (request.RequestId, job.AutoFetchJobId);
    }

    private static string RegistryJson()
    {
        var docs = string.Join(",", Enumerable.Range(1, DocumentCount).Select(i =>
            // "size" (KB) is a deliberate lowball — 1 KB declared vs. 40 KB real — so the plan-time
            // byte-truncation in BuildDownloadPlan does not itself remove any document from the plan.
            $"{{\"docId\":\"doc{i}\",\"mcaName\":\"Form CHG-{i}\",\"documentDate\":\"2026-07-01T00:00:00+05:30\",\"size\":1,\"awsPath\":\"214/x/doc{i}.pdf\",\"section\":\"S\",\"attachments\":[]}}"));
        var section = $"{{\"data\":[{docs}],\"totalCount\":{DocumentCount}}}";
        return "{\"charge\":" + JsonSerializer.Serialize(section) + "}";
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("jwt/service.php", StringComparison.Ordinal))
                return Task.FromResult(Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
            if (path.EndsWith("userDetailsService.php", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"id\":265271}"));
            if (path.EndsWith("docService/service.php", StringComparison.Ordinal))
            {
                // downloadPdf's params are plain (unsigned) query string; referenceDocs' action lives
                // inside the signed, opaque "qp" JWT instead — so a literal "action=downloadPdf" match
                // unambiguously distinguishes the two without needing to decode anything.
                if (request.RequestUri!.Query.Contains("action=downloadPdf", StringComparison.Ordinal))
                {
                    // A real PDF response, well over the declared registry size but exactly at (not over)
                    // MaxResponseBytes individually — "fits" per the review's own phrasing.
                    var body = new byte[RealFileBytes];
                    "%PDF-1.4\n"u8.ToArray().CopyTo(body, 0);
                    return Task.FromResult(Bytes(body, "application/pdf"));
                }
                return Task.FromResult(Json(RegistryJson())); // referenceDocs
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + request.RequestUri) });
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
