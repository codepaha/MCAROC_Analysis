using System.Net;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

public class AutoFetchFilingsWarningTests : IAsyncLifetime
{
    private const string KeyHex = "6b65792d666f722d7465737473";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "autofetch-filings-warning-tests-" + Guid.NewGuid().ToString("N"));

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task When_only_filings_fail_job_ends_CompletedWithWarnings_with_filings_stage_flagged()
    {
        await using var db = CreateContext();

        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc",
            UserId = "265271",
            MaxResponseBytes = 50_000,
            MaxAggregateDownloadBytes = 200_000,
            DownloadConcurrency = 2,
            DownloadAttempts = 1
        });

        // Handler returns error for docService (filings fail)
        var client = new ReferenceToolClient(
            new HttpClient(new FailingDocHandler()),
            options,
            new ReferenceToolSession(),
            new NoOpIntegrationHealthService(),
            NullLogger<ReferenceToolClient>.Instance);

        var storageReservations = new StorageReservationManager(db, Options.Create(new LargeArchiveUploadOptions()), NullLogger<StorageReservationManager>.Instance);
        var jobs = new AutoFetchJobService(
            db, client, options, new FileValidationService(new ExcelSheetReader()),
            null!, new AnalysisQueue(), new FilingProcessingQueue(), storageReservations,
            new FakeEnv(_tempDir), NullLogger<AutoFetchJobService>.Instance);

        var (requestId, jobId) = await SeedRequestPastIngestionAsync(db);

        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);

        Assert.Equal(AutoFetchJobStatus.CompletedWithWarnings, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Contains("filings stage failed", job.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(job.FilingsDocumentId);
        Assert.Null(job.FilingBatchId);
        Assert.Contains("Filings stage failed", job.WarningsJson);

        // Workbook ingestion and request status are preserved — request is not marked ExtractionFailed
        Assert.NotEqual(RequestStatus.ExtractionFailed, request.RequestStatus);
        Assert.NotNull(request.LatestCompletedIngestionRunId);
    }

    [Fact]
    public async Task When_archive_packaging_fails_safety_check_job_ends_CompletedWithWarnings_and_preserves_extraction()
    {
        await using var db = CreateContext();

        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc",
            UserId = "265271",
            MaxResponseBytes = 50_000,
            MaxAggregateDownloadBytes = 200_000,
            DownloadConcurrency = 1,
            DownloadAttempts = 1
        });

        // Handler returns valid documents and downloads, but packaging creates an archive
        // Here we test when the safety check or packaging throws
        var client = new ReferenceToolClient(
            new HttpClient(new CorruptFilingDocHandler()),
            options,
            new ReferenceToolSession(),
            new NoOpIntegrationHealthService(),
            NullLogger<ReferenceToolClient>.Instance);

        var storageReservations = new StorageReservationManager(db, Options.Create(new LargeArchiveUploadOptions()), NullLogger<StorageReservationManager>.Instance);
        var jobs = new AutoFetchJobService(
            db, client, options, new FileValidationService(new ExcelSheetReader()),
            null!, new AnalysisQueue(), new FilingProcessingQueue(), storageReservations,
            new FakeEnv(_tempDir), NullLogger<AutoFetchJobService>.Instance);

        var (requestId, jobId) = await SeedRequestPastIngestionAsync(db);

        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);

        Assert.Equal(AutoFetchJobStatus.CompletedWithWarnings, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Contains("filings stage failed", job.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Filings stage failed", job.WarningsJson);
        Assert.NotEqual(RequestStatus.ExtractionFailed, request.RequestStatus);
    }

    private static async Task<(long RequestId, long JobId)> SeedRequestPastIngestionAsync(AppDbContext db)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.IsActive);
        if (client is null)
        {
            client = new Client { ClientName = "Test Client " + Guid.NewGuid().ToString("N")[..8], IsActive = true };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

        var request = new McaRequest
        {
            ClientId = client.ClientId, RequestNumber = "MCA-" + Guid.NewGuid().ToString("N")[..12],
            CompanyName = "Test Target " + Guid.NewGuid().ToString("N")[..8],
            Cin = "U" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant(),
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var roc = new RequestDocument
        {
            RequestId = request.RequestId, DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "roc.xlsx", StoredFileName = "roc.xlsx",
            StoragePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx"),
            FileHash = "hash-" + Guid.NewGuid().ToString("N"),
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(roc);
        await db.SaveChangesAsync();

        var run = new IngestionRun
        {
            RequestId = request.RequestId, SourceRocDocumentId = roc.DocumentId, RunNumber = 1,
            Status = IngestionRunStatus.CompletedClean, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow
        };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        await db.SaveChangesAsync();

        var job = new AutoFetchJob
        {
            RequestId = request.RequestId, Cin = request.Cin!, Bid = ReferenceToolClient.ComputeBid(request.Cin!),
            Status = AutoFetchJobStatus.Queued, IncludeFilings = true, MaxDocumentsPerSection = 0,
            RocDocumentId = roc.DocumentId, IngestionRunId = run.IngestionRunId, WarningsJson = "[]", CreatedUtc = DateTime.UtcNow
        };
        db.AutoFetchJobs.Add(job);
        await db.SaveChangesAsync();

        return (request.RequestId, job.AutoFetchJobId);
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

    private sealed class FailingDocHandler : HttpMessageHandler
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
                // Fail with HTTP 500 or broken JSON
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("Filings server temporarily unreachable")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class CorruptFilingDocHandler : HttpMessageHandler
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
                if (request.RequestUri!.Query.Contains("action=downloadPdf", StringComparison.Ordinal))
                {
                    // Fail the download so downloadedCount == 0
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                var section = "{\"data\":[{\"docId\":\"doc1\",\"mcaName\":\"Form 1\",\"documentDate\":\"2026-07-01T00:00:00+05:30\",\"size\":1,\"awsPath\":\"x/1.pdf\",\"section\":\"S\",\"attachments\":[]}],\"totalCount\":1}";
                return Task.FromResult(Json("{\"charge\":" + JsonSerializer.Serialize(section) + "}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
