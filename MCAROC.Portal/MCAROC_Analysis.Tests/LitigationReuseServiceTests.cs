using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>#291 (plan §4.2a): when another request already holds a litigation report for the same scope,
/// retrieved within the last 7 days, a request takes that report instead of buying and analysing again.
/// Real SQL Server; a real temp directory stands in for App_Data so the retained-PDF copy is genuinely
/// exercised, not just its row.</summary>
public sealed class LitigationReuseServiceTests : IAsyncLifetime
{
    private string _contentRoot = "";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
        _contentRoot = Path.Combine(Path.GetTempPath(), "mcaroc-reuse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private LitigationReuseService Service(AppDbContext db) =>
        new(db, new FakeEnv(_contentRoot), TimeProvider.System, NullLogger<LitigationReuseService>.Instance);

    [Fact]
    public async Task Reuses_a_recent_source_and_copies_cases_orders_the_retained_file_and_chunks()
    {
        await using var db = CreateContext();
        var scope = $"search|SEED-{Guid.NewGuid():N}|hash";
        var source = await SeedSourceAsync(db, scope, retrievedUtc: DateTime.UtcNow.AddDays(-3), withDocument: true, withChunk: true);
        var request = await SeedRequestAsync(db, "reuser");

        var result = await Service(db).TryReuseAsync(request.RequestId, scope, CancellationToken.None);

        Assert.True(result.Reused);
        Assert.Equal(source.RequestId, result.SourceRequestId);
        Assert.Equal(source.RetrievedUtc, result.SourceRetrievedUtc);

        await using var verify = CreateContext();
        var newSnapshot = await verify.LitigationReportSnapshots.AsNoTracking().SingleAsync(s => s.LitigationReportSnapshotId == result.SnapshotId);
        Assert.Equal(request.RequestId, newSnapshot.RequestId);
        Assert.Equal(source.SnapshotId, newSnapshot.ReusedFromSnapshotId);
        Assert.Equal(source.RequestId, newSnapshot.ReusedFromRequestId);
        Assert.Equal(source.SnapshotId, newSnapshot.OriginSnapshotId); // source was itself a fresh purchase — its own id is the origin
        Assert.Equal(source.RetrievedUtc, newSnapshot.RetrievedUtc); // never reset

        var newJob = await verify.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.LitigationSearchJobId == result.JobId);
        Assert.Equal(request.RequestId, newJob.RequestId);
        Assert.Equal(LitigationSearchJobStatus.Completed, newJob.Status);

        var newCase = await verify.LitigationCases.AsNoTracking().SingleAsync(c => c.RequestId == request.RequestId);
        Assert.Equal("CNR-SOURCE-1", newCase.Cnr);
        Assert.NotEqual(source.CaseId, newCase.LitigationCaseId); // a genuine copy, not the same row

        var newOrder = await verify.LitigationCaseOrders.AsNoTracking().SingleAsync(o => o.LitigationCaseId == newCase.LitigationCaseId);
        var newDoc = await verify.LitigationOrderDocuments.AsNoTracking().SingleAsync(d => d.LitigationCaseOrderId == newOrder.LitigationCaseOrderId);
        Assert.Equal(LitigationOrderDocumentStatus.Downloaded, newDoc.Status);
        Assert.NotEqual(source.DocumentPath, newDoc.StoragePath);
        Assert.True(File.Exists(newDoc.StoragePath));
        Assert.Equal(File.ReadAllBytes(source.DocumentPath!), await File.ReadAllBytesAsync(newDoc.StoragePath!));

        var newChunk = await verify.LitigationOrderChunks.AsNoTracking().SingleAsync(c => c.LitigationOrderDocumentId == newDoc.LitigationOrderDocumentId);
        Assert.Equal(request.RequestId, newChunk.RequestId);
        Assert.Equal(newCase.LitigationCaseId, newChunk.LitigationCaseId);
        Assert.Equal("reused chunk text", newChunk.ChunkText);
    }

    [Fact]
    public async Task A_source_older_than_seven_days_is_not_reused()
    {
        await using var db = CreateContext();
        var scope = $"search|SEED-{Guid.NewGuid():N}|hash";
        await SeedSourceAsync(db, scope, retrievedUtc: DateTime.UtcNow.AddDays(-8));
        var request = await SeedRequestAsync(db, "reuser2");

        var result = await Service(db).TryReuseAsync(request.RequestId, scope, CancellationToken.None);

        Assert.False(result.Reused);
        Assert.Null(result.SnapshotId);
    }

    [Fact]
    public async Task No_committed_admission_for_the_scope_is_not_reused()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "reuser3");

        var result = await Service(db).TryReuseAsync(request.RequestId, $"search|NEVER-SEARCHED-{Guid.NewGuid():N}|hash", CancellationToken.None);

        Assert.False(result.Reused);
    }

    [Fact]
    public async Task A_second_reuse_for_the_same_request_and_origin_joins_the_first_copy_rather_than_duplicating()
    {
        await using var db = CreateContext();
        var scope = $"search|SEED-{Guid.NewGuid():N}|hash";
        await SeedSourceAsync(db, scope, retrievedUtc: DateTime.UtcNow.AddHours(-1));
        var request = await SeedRequestAsync(db, "reuser4");

        var first = await Service(db).TryReuseAsync(request.RequestId, scope, CancellationToken.None);
        var second = await Service(db).TryReuseAsync(request.RequestId, scope, CancellationToken.None);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        await using var verify = CreateContext();
        Assert.Equal(1, await verify.LitigationReportSnapshots.CountAsync(s => s.RequestId == request.RequestId));
        Assert.Equal(1, await verify.LitigationSearchJobs.CountAsync(j => j.RequestId == request.RequestId));
    }

    [Fact]
    public async Task A_completed_analysis_on_the_source_is_copied_as_Reused_with_evidence_carried_byte_for_byte()
    {
        await using var db = CreateContext();
        var scope = $"search|SEED-{Guid.NewGuid():N}|hash";
        var source = await SeedSourceAsync(db, scope, retrievedUtc: DateTime.UtcNow.AddHours(-2), withAnalysis: true);
        var request = await SeedRequestAsync(db, "reuser5");

        var result = await Service(db).TryReuseAsync(request.RequestId, scope, CancellationToken.None);

        await using var verify = CreateContext();
        var run = await verify.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.RequestId == request.RequestId);
        Assert.Equal(LitigationAiAnalysisTrigger.Reused, run.Trigger);
        Assert.Equal(LitigationAiAnalysisRunStatus.Completed, run.Status);
        Assert.Equal(result.SnapshotId, run.TriggerSnapshotId);
        Assert.Equal(source.SnapshotId, run.OriginSnapshotId);

        var newCase = await verify.LitigationCases.AsNoTracking().SingleAsync(c => c.RequestId == request.RequestId);
        var caseAnalysis = await verify.LitigationCaseAiAnalyses.AsNoTracking().SingleAsync(a => a.LitigationAiAnalysisRunId == run.LitigationAiAnalysisRunId);
        Assert.Equal(newCase.LitigationCaseId, caseAnalysis.LitigationCaseId); // FK moved to the copy's own case
        Assert.Equal("source-evidence-hash", caseAnalysis.EvidenceHash); // content carried byte-for-byte, unremapped

        var portfolio = await verify.LitigationPortfolioAiAnalyses.AsNoTracking().SingleAsync(p => p.LitigationAiAnalysisRunId == run.LitigationAiAnalysisRunId);
        Assert.Equal("source-portfolio-hash", portfolio.EvidenceHash);
    }

    private sealed record SourceHandles(long RequestId, long SnapshotId, long CaseId, DateTime RetrievedUtc, string? DocumentPath);

    private async Task<SourceHandles> SeedSourceAsync(
        AppDbContext db, string scopeKey, DateTime retrievedUtc, bool withDocument = false, bool withChunk = false, bool withAnalysis = false)
    {
        var request = await SeedRequestAsync(db, "source");
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed,
            EntityType = "company", ApplicationCustomerId = "cust-1", CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId,
            ReportHash = Guid.NewGuid().ToString("N")[..16], Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = retrievedUtc, CasesPersistedCount = 1, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        db.PaidCallAdmissions.Add(new PaidCallAdmission
        {
            Kind = PaidCallKind.LitigationSearch, ScopeKey = scopeKey, DayKey = DateOnly.FromDateTime(DateTime.UtcNow),
            Trigger = PaidCallTrigger.Manual, RequestId = request.RequestId, State = PaidCallAdmissionState.Committed,
            ReferenceId = job.LitigationSearchJobId, ReservedUtc = DateTime.UtcNow, ResolvedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var c = new LitigationCase
        {
            RequestId = request.RequestId, Cnr = "CNR-SOURCE-1", ProceedingType = "OS", CaseNumber = "1/2024",
            Court = "High Court", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(c);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        string? documentPath = null;
        if (withDocument || withChunk)
        {
            var order = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "2024-01-01", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
            db.LitigationCaseOrders.Add(order);
            await db.SaveChangesAsync();

            var doc = new LitigationOrderDocument
            {
                LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded,
                RetainedUntilUtc = DateTime.UtcNow.AddDays(3), FileHash = "h", ContentType = "application/pdf",
                DownloadedUtc = DateTime.UtcNow, ExtractedText = "some text", CreatedUtc = DateTime.UtcNow
            };
            if (withDocument)
            {
                var dir = Path.Combine(_contentRoot, "App_Data", "Requests", request.RequestId.ToString(), "litigation-orders");
                Directory.CreateDirectory(dir);
                documentPath = Path.Combine(dir, $"{Guid.NewGuid():N}.pdf");
                await File.WriteAllBytesAsync(documentPath, [1, 2, 3, 4, 5]);
                doc.StoragePath = documentPath;
                doc.FileSizeBytes = 5;
            }
            db.LitigationOrderDocuments.Add(doc);
            await db.SaveChangesAsync();

            if (withChunk)
            {
                db.LitigationOrderChunks.Add(new LitigationOrderChunk
                {
                    RequestId = request.RequestId, LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
                    LitigationCaseOrderId = order.LitigationCaseOrderId, LitigationCaseId = c.LitigationCaseId,
                    ChunkIndex = 0, PageNumber = 1, ChunkText = "reused chunk text",
                    Embedding = new SqlVector<float>(new float[768]), EmbeddingModel = "test-model",
                    EmbeddingDimensions = 768, ChunkingVersion = "v1", CreatedDate = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }

        if (withAnalysis)
        {
            var run = new LitigationAiAnalysisRun
            {
                RequestId = request.RequestId, RunNumber = 1, Trigger = LitigationAiAnalysisTrigger.Manual,
                Status = LitigationAiAnalysisRunStatus.Completed, ModelId = "m", PromptVersion = "v1",
                CreatedUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
            };
            db.LitigationAiAnalysisRuns.Add(run);
            await db.SaveChangesAsync();

            db.LitigationCaseAiAnalyses.Add(new LitigationCaseAiAnalysis
            {
                LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, LitigationCaseId = c.LitigationCaseId,
                Status = LitigationAiAnalysisItemStatus.Completed, EvidenceJson = "{}", EvidenceHash = "source-evidence-hash",
                PromptHash = "prompt-hash", AnalysisJson = "{\"status\":\"Completed\"}", CompletedUtc = DateTime.UtcNow
            });
            db.LitigationPortfolioAiAnalyses.Add(new LitigationPortfolioAiAnalysis
            {
                LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, Status = LitigationAiAnalysisItemStatus.Completed,
                EvidenceJson = "{}", EvidenceHash = "source-portfolio-hash", PromptHash = "prompt-hash",
                AnalysisJson = "{\"status\":\"Completed\"}", CompletedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        return new SourceHandles(request.RequestId, snapshot.LitigationReportSnapshotId, c.LitigationCaseId, retrievedUtc, documentPath);
    }

    private static async Task<McaRequest> SeedRequestAsync(AppDbContext db, string suffix)
    {
        var client = new Client { ClientCode = "LRU" + suffix + Guid.NewGuid().ToString("N")[..6], ClientName = "Reuse Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Reuse Test Company",
            RequestNumber = $"LRU-{suffix}-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
    }
}
