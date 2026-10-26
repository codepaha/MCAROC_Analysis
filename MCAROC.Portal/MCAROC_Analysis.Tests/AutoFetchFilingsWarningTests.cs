using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.E2e;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
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

        var handler = new TogglingDocHandler { FailFilings = true };
        var client = new ReferenceToolClient(
            new HttpClient(handler),
            options,
            new ReferenceToolSession(),
            new NoOpIntegrationHealthService(),
            NullLogger<ReferenceToolClient>.Instance);

        var storageReservations = new StorageReservationManager(db, Options.Create(new LargeArchiveUploadOptions()), NullLogger<StorageReservationManager>.Instance);
        var autoFetchQueue = new AutoFetchQueue();
        var jobs = new AutoFetchJobService(
            db, client, options, new FileValidationService(new ExcelSheetReader()),
            null!, new AnalysisQueue(), new FilingProcessingQueue(), storageReservations,
            new FakeEnv(_tempDir), NullLogger<AutoFetchJobService>.Instance);

        var (requestId, jobId) = await SeedRequestPastIngestionAsync(db);

        // Seed a pipeline run so we can assert the stage outcome on the run record
        var pipelineRun = new PipelineRun
        {
            RequestId = requestId,
            Outcome = PipelineOutcome.InProgress,
            Trigger = PipelineRunTrigger.AutoFetch,
            CorrelationId = "test-corr",
            CreatedUtc = DateTime.UtcNow
        };
        db.PipelineRuns.Add(pipelineRun);
        await db.SaveChangesAsync();

        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);

        Assert.Equal(AutoFetchJobStatus.CompletedWithWarnings, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Contains("filings stage failed", job.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(job.FilingsDocumentId);
        Assert.Null(job.FilingBatchId);
        Assert.Contains("Filings stage failed", job.WarningsJson);
        Assert.False(string.IsNullOrWhiteSpace(job.FailureReason));
        Assert.Contains("500", job.FailureReason);

        // Workbook ingestion and request status are preserved — request is not marked ExtractionFailed
        Assert.NotEqual(RequestStatus.ExtractionFailed, request.RequestStatus);
        Assert.NotNull(request.LatestCompletedIngestionRunId);

        // Reconcile the pipeline run and check the run record
        var snapshotReader = new PipelineSnapshotReader(db, new ConfigurationBuilder().Build(), Options.Create(new BprLitigationOptions()), options);
        var reconciler = new PipelineReconciler(db, snapshotReader, TimeProvider.System, NullLogger<PipelineReconciler>.Instance);
        await reconciler.ReconcileAsync(pipelineRun.PipelineRunId, CancellationToken.None);

        var runRecordService = new RunRecordService(db);
        var runRecord = await runRecordService.GetAsync(requestId, CancellationToken.None);
        Assert.NotNull(runRecord);
        Assert.Equal("CompletedWithWarnings", runRecord.Fetch?.Status);
        Assert.Equal(job.FailureReason, runRecord.Fetch?.FailureReason);
        var filingsStage = runRecord.Stages.FirstOrDefault(s => s.Stage == "Filings");
        Assert.NotNull(filingsStage);
        Assert.Equal("SucceededWithWarnings", filingsStage.State);
        Assert.Equal("FILINGS_FETCH_FAILED", filingsStage.ReasonCode);
        Assert.Equal(job.FailureReason, filingsStage.ReasonDetail);
        Assert.Contains(runRecord.StagesWithWarnings, s => s.Stage == "Filings");

        // Request page shows the warning
        var vm = new RequestDetailsViewModel { Request = request, AutoFetchJob = job, Documents = [] };
        var html = await RenderAutoFetchPanelAsync(vm);
        Assert.Contains("alert alert-warning", html);
        Assert.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(job.FailureReason!), html);
        Assert.Contains("Retry filings", html);

        // Retry via /Requests/{id}/autofetch/retry
        var httpContext = new DefaultHttpContext();
        var controller = new AutoFetchController(db, jobs, autoFetchQueue, client, options)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Retry(requestId, CancellationToken.None));
        Assert.Equal("Details", redirect.ActionName);

        var requeuedJob = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        Assert.Equal(AutoFetchJobStatus.Queued, requeuedJob.Status);
        Assert.Null(requeuedJob.FailureReason);
        Assert.NotNull(requeuedJob.IngestionRunId);
        Assert.NotNull(requeuedJob.RocDocumentId);

        // Second pass: filings succeed; retry runs only filings (ingestion is kept)
        var priorIngestionRunId = requeuedJob.IngestionRunId;
        handler.FailFilings = false;
        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var retriedJob = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        Assert.Equal(AutoFetchJobStatus.Completed, retriedJob.Status);
        Assert.Equal(priorIngestionRunId, retriedJob.IngestionRunId);
        Assert.NotNull(retriedJob.FilingsDocumentId);
        Assert.Null(retriedJob.FailureReason);
    }

    /// <summary>True packaging-failure fixture: downloads succeed cleanly with valid PDFs, but the packaged
    /// outer archive exceeds the configured archive safety size limit (exactly as seen on Infosys request 23).
    /// Asserts that packaging/safety check is reached and fails cleanly without failing earlier stages.</summary>
    [Fact]
    public async Task When_archive_packaging_fails_safety_check_job_ends_CompletedWithWarnings_and_preserves_extraction()
    {
        await using var db = CreateContext();

        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc",
            UserId = "265271",
            MaxResponseBytes = 400,
            MaxAggregateDownloadBytes = 50_000,
            DownloadConcurrency = 1,
            DownloadAttempts = 1
        });

        // Downloads succeed with valid PDF bytes
        var client = new ReferenceToolClient(
            new HttpClient(new SuccessfulDocHandler()),
            options,
            new ReferenceToolSession(),
            new NoOpIntegrationHealthService(),
            NullLogger<ReferenceToolClient>.Instance);

        // Set MaxArchiveSizeBytes so the plan and download budget admit the file, but the packaged archive (~1,200 bytes) fails ArchiveSafetyValidator
        var uploadOptions = Options.Create(new LargeArchiveUploadOptions
        {
            MaxArchiveSizeBytes = 500 // aggregateCap = 475 bytes (admits 102-byte plan & 400-byte response), but packaged outer zip is ~650 bytes
        });

        var storageReservations = new StorageReservationManager(db, uploadOptions, NullLogger<StorageReservationManager>.Instance);
        var jobs = new AutoFetchJobService(
            db, client, options, new FileValidationService(new ExcelSheetReader()),
            null!, new AnalysisQueue(), new FilingProcessingQueue(), storageReservations,
            new FakeEnv(_tempDir), NullLogger<AutoFetchJobService>.Instance,
            uploadOptions: uploadOptions);

        var (requestId, jobId) = await SeedRequestPastIngestionAsync(db);

        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);

        Assert.Equal(AutoFetchJobStatus.CompletedWithWarnings, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Contains("filings stage failed", job.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The packaged filings archive failed the safety check", job.WarningsJson);
        Assert.Null(job.FilingsDocumentId);
        Assert.Null(job.FilingBatchId);
        Assert.NotEqual(RequestStatus.ExtractionFailed, request.RequestStatus);
        Assert.NotNull(request.LatestCompletedIngestionRunId);
    }

    /// <summary>Regression for PR #544 review finding [P2]: when a job fails in filings and is retried,
    /// a successful second pass clears the prior "Filings stage failed" warning, creates the archive/batch,
    /// preserves checkpoints (no repeated extraction), and completes cleanly.</summary>
    [Fact]
    public async Task When_filings_fail_then_retried_and_succeeds_stale_warning_is_cleared_and_job_completes()
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

        var handler = new TogglingDocHandler { FailFilings = true };
        var client = new ReferenceToolClient(
            new HttpClient(handler),
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

        // Pass 1: filings fail
        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var jobPass1 = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        Assert.Equal(AutoFetchJobStatus.CompletedWithWarnings, jobPass1.Status);
        Assert.Contains("Filings stage failed", jobPass1.WarningsJson);
        Assert.Null(jobPass1.FilingsDocumentId);

        // Click "Retry filings" (RequeueAsync)
        var requeued = await jobs.RequeueAsync(requestId, CancellationToken.None);
        Assert.NotNull(requeued);
        Assert.Equal(AutoFetchJobStatus.Queued, requeued.Status);
        Assert.NotNull(requeued.IngestionRunId); // Checkpoint preserved

        // Pass 2: filings now succeed
        handler.FailFilings = false;
        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var jobPass2 = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);

        // Final status is Completed (no warnings remain), stale failure warning is gone
        Assert.Equal(AutoFetchJobStatus.Completed, jobPass2.Status);
        Assert.Equal(100, jobPass2.ProgressPercent);
        Assert.NotNull(jobPass2.FilingsDocumentId);
        Assert.NotNull(jobPass2.FilingBatchId);
        Assert.DoesNotContain("Filings stage failed", jobPass2.WarningsJson);
        Assert.Contains("Done — workbook data extracted, analysis queued, filings handed to the document pipeline.", jobPass2.StatusMessage);

        // Request still active, no extraction re-run
        Assert.NotEqual(RequestStatus.ExtractionFailed, request.RequestStatus);
    }

    /// <summary>Regression for PR #544 review finding [P2]: configured lower limits (e.g. MaxPdfCount)
    /// truncate planning with a warning, preserve the fitting files, package and validate them cleanly, and downstream
    /// FilingBatchProcessor unpacks them using compatible configured limits.</summary>
    [Fact]
    public async Task When_lower_configured_limits_truncate_plan_fitting_files_are_preserved_with_warning()
    {
        await using var db = CreateContext();

        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc",
            UserId = "265271",
            MaxResponseBytes = 50_000,
            MaxAggregateDownloadBytes = 500_000,
            DownloadConcurrency = 1,
            DownloadAttempts = 1
        });

        // 3 documents in registry, but MaxPdfCount is capped at 2
        var client = new ReferenceToolClient(
            new HttpClient(new MultiDocHandler(count: 3)),
            options,
            new ReferenceToolSession(),
            new NoOpIntegrationHealthService(),
            NullLogger<ReferenceToolClient>.Instance);

        var uploadOptions = Options.Create(new LargeArchiveUploadOptions
        {
            MaxPdfCount = 2,
            MaxArchiveSizeBytes = 500_000_000
        });

        var storageReservations = new StorageReservationManager(db, uploadOptions, NullLogger<StorageReservationManager>.Instance);
        var jobs = new AutoFetchJobService(
            db, client, options, new FileValidationService(new ExcelSheetReader()),
            null!, new AnalysisQueue(), new FilingProcessingQueue(), storageReservations,
            new FakeEnv(_tempDir), NullLogger<AutoFetchJobService>.Instance,
            uploadOptions: uploadOptions);

        var (requestId, jobId) = await SeedRequestPastIngestionAsync(db);

        await jobs.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);

        // Fitting 2 files are preserved and packaged, truncation warning is recorded
        Assert.Equal(AutoFetchJobStatus.CompletedWithWarnings, job.Status);
        Assert.Equal(100, job.ProgressPercent);
        Assert.Contains("2-PDF batch limit", job.WarningsJson);
        Assert.DoesNotContain("Filings stage failed", job.WarningsJson);
        Assert.Contains("Done — workbook data extracted, analysis queued, filings handed to the document pipeline.", job.StatusMessage);
        Assert.Equal(2, job.FilesTotal);
        Assert.Equal(2, job.FilesDownloaded);
        Assert.NotNull(job.FilingsDocumentId);
        Assert.NotNull(job.FilingBatchId);

        // Downstream unpack uses the same compatible limits and processes cleanly
        var processor = new FilingBatchProcessor(
            db, _tempDir, null, null, new FilingProcessingQueue(), new DocumentChunkingQueue(),
            NullLogger<FilingBatchProcessor>.Instance,
            archiveOptions: uploadOptions);

        await processor.UnpackBatchAsync(job.FilingBatchId.Value, CancellationToken.None);

        var batch = await db.McaFilingBatches.AsNoTracking().SingleAsync(b => b.BatchId == job.FilingBatchId.Value);
        Assert.Equal(FilingBatchStatus.Processing, batch.Status);
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

    private sealed class SuccessfulDocHandler : HttpMessageHandler
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
                    var pdf = "%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF"u8.ToArray();
                    return Task.FromResult(Bytes(pdf, "application/pdf"));
                }
                var section = "{\"data\":[{\"docId\":\"doc1\",\"mcaName\":\"Form 1\",\"documentDate\":\"2026-07-01T00:00:00+05:30\",\"size\":0.1,\"awsPath\":\"x/1.pdf\",\"section\":\"S\",\"attachments\":[]}],\"totalCount\":1}";
                return Task.FromResult(Json("{\"charge\":" + JsonSerializer.Serialize(section) + "}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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

    private sealed class TogglingDocHandler : HttpMessageHandler
    {
        public bool FailFilings { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("jwt/service.php", StringComparison.Ordinal))
                return Task.FromResult(Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
            if (path.EndsWith("userDetailsService.php", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"id\":265271}"));
            if (path.EndsWith("docService/service.php", StringComparison.Ordinal))
            {
                if (FailFilings)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("Filings server temporarily unreachable")
                    });
                }

                if (request.RequestUri!.Query.Contains("action=downloadPdf", StringComparison.Ordinal))
                {
                    var pdf = "%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF"u8.ToArray();
                    return Task.FromResult(Bytes(pdf, "application/pdf"));
                }
                var section = "{\"data\":[{\"docId\":\"doc1\",\"mcaName\":\"Form 1\",\"documentDate\":\"2026-07-01T00:00:00+05:30\",\"size\":1,\"awsPath\":\"x/1.pdf\",\"section\":\"S\",\"attachments\":[]}],\"totalCount\":1}";
                return Task.FromResult(Json("{\"charge\":" + JsonSerializer.Serialize(section) + "}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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

    private sealed class MultiDocHandler(int count = 3) : HttpMessageHandler
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
                    var pdf = "%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF"u8.ToArray();
                    return Task.FromResult(Bytes(pdf, "application/pdf"));
                }
                var docs = Enumerable.Range(1, count)
                    .Select(i => $"{{\"docId\":\"doc{i}\",\"mcaName\":\"Form {i}\",\"documentDate\":\"2026-07-0{i}T00:00:00+05:30\",\"size\":0.1,\"awsPath\":\"x/{i}.pdf\",\"section\":\"S\",\"attachments\":[]}}");
                var section = $"{{\"data\":[{string.Join(",", docs)}],\"totalCount\":{count}}}";
                return Task.FromResult(Json("{\"charge\":" + JsonSerializer.Serialize(section) + "}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("MCAROC.Portal not found in parent hierarchy");
    }

    private static IServiceProvider CreateRazorServices()
    {
        var services = new ServiceCollection();
        var repoRoot = FindRepoRoot();
        var webAppDir = Path.Combine(repoRoot, "MCAROC.Portal", "MCAROC_Analysis");
        var webRoot = Path.Combine(webAppDir, "wwwroot");

        var env = new TestRazorWebHostEnvironment
        {
            ApplicationName = "MCAROC_Analysis",
            ContentRootPath = webAppDir,
            WebRootPath = webRoot,
            ContentRootFileProvider = new PhysicalFileProvider(webAppDir),
            WebRootFileProvider = new PhysicalFileProvider(webRoot)
        };
        services.AddSingleton<IWebHostEnvironment>(env);
        services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        var diag = new DiagnosticListener("Microsoft.AspNetCore");
        services.AddSingleton<DiagnosticSource>(diag);
        services.AddSingleton<DiagnosticListener>(diag);
        services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
        services.AddLogging();
        services.AddControllersWithViews();

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAutoFetchPanelAsync(RequestDetailsViewModel model)
    {
        var sp = CreateRazorServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_AutoFetchPanel.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
            viewResult = viewEngine.FindView(actionContext, "Details/_AutoFetchPanel", isMainPage: false);

        if (!viewResult.Success)
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations: {string.Join(", ", viewResult.SearchedLocations ?? [])}");

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<RequestDetailsViewModel>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };

        var tempData = new TempDataDictionary(actionContext.HttpContext, tempDataProvider);
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            tempData,
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed class TestRazorWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
