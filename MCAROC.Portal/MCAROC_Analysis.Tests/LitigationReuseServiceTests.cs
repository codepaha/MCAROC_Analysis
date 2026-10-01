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
    private readonly LitigationOrderDocumentQueue DocumentQueue = new();

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
        new(db, new FakeEnv(_contentRoot), TimeProvider.System, DocumentQueue, NullLogger<LitigationReuseService>.Instance);

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

    /// <summary>Review finding on PR #303: a copied nonterminal order document (never downloaded, or failed
    /// and still retryable) must be enqueued to the real in-process worker queue the moment the copy commits
    /// — not left to sit until the process happens to restart and its recovery sweep finds it. Real SQL
    /// Server, a real (non-mock) <see cref="LitigationOrderDocumentQueue"/>: this proves the id is actually
    /// on the queue, not just that the code compiles a call to Enqueue.</summary>
    [Fact]
    public async Task A_reused_pending_order_document_is_enqueued_for_download_without_a_restart()
    {
        await using var db = CreateContext();
        var sourceRequest = await SeedRequestAsync(db, "pendingdoc");
        var job = new LitigationSearchJob { RequestId = sourceRequest.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = sourceRequest.RequestId, ReportHash = "pending-doc-hash",
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow.AddHours(-1), CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        var c = new LitigationCase { RequestId = sourceRequest.RequestId, Cnr = $"CNR{Guid.NewGuid():N}"[..16], CaseNumber = "1/2024", Court = "High Court", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
        db.LitigationCases.Add(c);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        var order = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "2024-01-01", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();
        // Never downloaded in the source — exactly the "genuine gap" the copy is meant to preserve, but the
        // gap must still be actively worked on under the reusing request's own id.
        db.LitigationOrderDocuments.Add(new LitigationOrderDocument
        {
            LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Pending,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5), CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var scopeKey = $"search|SEED-{Guid.NewGuid():N}|hash";
        db.PaidCallAdmissions.Add(new PaidCallAdmission
        {
            Kind = PaidCallKind.LitigationSearch, ScopeKey = scopeKey, DayKey = DateOnly.FromDateTime(DateTime.UtcNow),
            Trigger = PaidCallTrigger.Manual, RequestId = sourceRequest.RequestId, State = PaidCallAdmissionState.Committed,
            ReferenceId = job.LitigationSearchJobId, ReservedUtc = DateTime.UtcNow, ResolvedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var requester = await SeedRequestAsync(db, "reuser7");
        var result = await Service(db).TryReuseAsync(requester.RequestId, scopeKey, CancellationToken.None);
        Assert.True(result.Reused);

        await using var verify = CreateContext();
        var newDoc = await verify.LitigationOrderDocuments.AsNoTracking()
            .SingleAsync(d => d.Order!.Case!.RequestId == requester.RequestId);
        Assert.Equal(LitigationOrderDocumentStatus.Pending, newDoc.Status);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long enqueuedId = 0;
        await foreach (var id in DocumentQueue.ReadAllAsync(cts.Token))
        {
            enqueuedId = id;
            break;
        }
        Assert.Equal(newDoc.LitigationOrderDocumentId, enqueuedId); // the COPY's id, never the source's
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

    /// <summary>#195: order-outcome classifications travel with the analysis they belong to — content verbatim, owning
    /// ids remapped to the copied case/order/document, so the reusing request's outcome lookup finds them.</summary>
    [Fact]
    public async Task A_completed_analysis_copies_its_order_classifications_onto_the_copied_orders()
    {
        await using var db = CreateContext();
        var scope = $"search|SEED-{Guid.NewGuid():N}|hash";
        await SeedSourceAsync(db, scope, retrievedUtc: DateTime.UtcNow.AddHours(-2), withChunk: true, withAnalysis: true, withClassification: true);
        var request = await SeedRequestAsync(db, "reuser6");

        await Service(db).TryReuseAsync(request.RequestId, scope, CancellationToken.None);

        await using var verify = CreateContext();
        var run = await verify.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.RequestId == request.RequestId);
        var newDoc = await verify.LitigationOrderDocuments.AsNoTracking().SingleAsync(d => d.Order!.Case!.RequestId == request.RequestId);
        var classification = await verify.LitigationOrderClassifications.AsNoTracking().SingleAsync(c => c.LitigationAiAnalysisRunId == run.LitigationAiAnalysisRunId);
        Assert.Equal(request.RequestId, classification.RequestId);
        Assert.Equal(newDoc.LitigationOrderDocumentId, classification.LitigationOrderDocumentId);
        Assert.Equal(newDoc.LitigationCaseOrderId, classification.LitigationCaseOrderId);
        var copiedChunk = await verify.LitigationOrderChunks.AsNoTracking().SingleAsync(c => c.LitigationOrderDocumentId == newDoc.LitigationOrderDocumentId);
        Assert.Equal(LitigationOrderClassifier.Hashes(LitigationOrderClassifier.BuildEvidence([copiedChunk])).EvidenceHash, classification.EvidenceHash);

        var lookup = await new LitigationOrderOutcomeQuery(verify).FindAsync(request.RequestId, [LitigationOrderOutcome.FinePenalty], CancellationToken.None);
        var match = Assert.Single(lookup.Matches);
        Assert.Equal(25000m, match.FineAmount);
    }

    /// <summary>Review finding on PR #303: CopyAnalysisAsync must not just take the source request's *latest*
    /// completed analysis — a request whose report was re-fetched (a new snapshot under the same job; a
    /// request has at most one job, reused in place) since an earlier analysis has an analysis that belongs
    /// to a DIFFERENT, older snapshot than the one actually being reused. Naively taking "the request's
    /// latest analysis, regardless of which snapshot triggered it" would copy that older, unrelated run's
    /// evidence — and, sharpest of all, its portfolio synthesis, which carries no case-id link to check —
    /// onto a request whose own cases don't match it at all.</summary>
    [Fact]
    public async Task Reusing_a_snapshot_with_no_analysis_of_its_own_never_borrows_an_older_unrelated_one()
    {
        await using var db = CreateContext();
        var sourceRequest = await SeedRequestAsync(db, "multisnap");
        var job = new LitigationSearchJob { RequestId = sourceRequest.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        // An old, unrelated snapshot from a much earlier fetch — analysed at the time, but outside today's
        // 7-day reuse window and not what this reuse call is about at all.
        var staleSnapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = sourceRequest.RequestId, ReportHash = "stale-hash",
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow.AddDays(-30), CreatedUtc = DateTime.UtcNow.AddDays(-30)
        };
        db.LitigationReportSnapshots.Add(staleSnapshot);
        await db.SaveChangesAsync();
        var staleCase = new LitigationCase { RequestId = sourceRequest.RequestId, Cnr = $"CNR{Guid.NewGuid():N}"[..16], CaseNumber = "OLD/2020", Court = "High Court", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
        db.LitigationCases.Add(staleCase);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = staleCase.LitigationCaseId, LitigationReportSnapshotId = staleSnapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        var staleRun = new LitigationAiAnalysisRun
        {
            RequestId = sourceRequest.RequestId, RunNumber = 1, Trigger = LitigationAiAnalysisTrigger.Manual,
            TriggerSnapshotId = staleSnapshot.LitigationReportSnapshotId, OriginSnapshotId = staleSnapshot.LitigationReportSnapshotId,
            Status = LitigationAiAnalysisRunStatus.Completed, ModelId = "m", PromptVersion = "v1",
            CreatedUtc = DateTime.UtcNow.AddDays(-30), StartedUtc = DateTime.UtcNow.AddDays(-30), CompletedUtc = DateTime.UtcNow.AddDays(-30)
        };
        db.LitigationAiAnalysisRuns.Add(staleRun);
        await db.SaveChangesAsync();
        db.LitigationCaseAiAnalyses.Add(new LitigationCaseAiAnalysis
        {
            LitigationAiAnalysisRunId = staleRun.LitigationAiAnalysisRunId, LitigationCaseId = staleCase.LitigationCaseId,
            Status = LitigationAiAnalysisItemStatus.Completed, EvidenceJson = "{}", EvidenceHash = "stale-unrelated-hash",
            PromptHash = "p", AnalysisJson = "{\"status\":\"Completed\"}", CompletedUtc = DateTime.UtcNow.AddDays(-30)
        });
        // A portfolio synthesis carries no per-case id — it is exactly what the buggy code would copy
        // wholesale onto the reusing request with nothing to catch the mismatch.
        db.LitigationPortfolioAiAnalyses.Add(new LitigationPortfolioAiAnalysis
        {
            LitigationAiAnalysisRunId = staleRun.LitigationAiAnalysisRunId, Status = LitigationAiAnalysisItemStatus.Completed,
            EvidenceJson = "{}", EvidenceHash = "stale-unrelated-portfolio-hash", PromptHash = "p",
            AnalysisJson = "{\"status\":\"Completed\"}", CompletedUtc = DateTime.UtcNow.AddDays(-30)
        });
        await db.SaveChangesAsync();

        // The report actually being reused: recent, within the window, its own case, no analysis at all yet.
        var currentSnapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = sourceRequest.RequestId, ReportHash = "current-hash",
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow.AddHours(-2), CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(currentSnapshot);
        await db.SaveChangesAsync();
        var currentCase = new LitigationCase { RequestId = sourceRequest.RequestId, Cnr = $"CNR{Guid.NewGuid():N}"[..16], CaseNumber = "NEW/2026", Court = "High Court", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
        db.LitigationCases.Add(currentCase);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = currentCase.LitigationCaseId, LitigationReportSnapshotId = currentSnapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });

        var scopeKey = $"search|SEED-{Guid.NewGuid():N}|hash";
        db.PaidCallAdmissions.Add(new PaidCallAdmission
        {
            Kind = PaidCallKind.LitigationSearch, ScopeKey = scopeKey, DayKey = DateOnly.FromDateTime(DateTime.UtcNow),
            Trigger = PaidCallTrigger.Manual, RequestId = sourceRequest.RequestId, State = PaidCallAdmissionState.Committed,
            ReferenceId = job.LitigationSearchJobId, ReservedUtc = DateTime.UtcNow, ResolvedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var requester = await SeedRequestAsync(db, "reuser6");
        var result = await Service(db).TryReuseAsync(requester.RequestId, scopeKey, CancellationToken.None);

        Assert.True(result.Reused);
        var copiedSnapshot = await db.LitigationReportSnapshots.AsNoTracking().SingleAsync(s => s.LitigationReportSnapshotId == result.SnapshotId);
        Assert.Equal(currentSnapshot.LitigationReportSnapshotId, copiedSnapshot.ReusedFromSnapshotId); // the recent one, not the stale one

        await using var verify = CreateContext();
        Assert.False(await verify.LitigationAiAnalysisRuns.AnyAsync(r => r.RequestId == requester.RequestId),
            "no analysis run should be copied — the reused snapshot has none of its own");
        Assert.Equal("NEW/2026", (await verify.LitigationCases.AsNoTracking().SingleAsync(c => c.RequestId == requester.RequestId)).CaseNumber);
    }

    private sealed record SourceHandles(long RequestId, long SnapshotId, long CaseId, DateTime RetrievedUtc, string? DocumentPath);

    private async Task<SourceHandles> SeedSourceAsync(
        AppDbContext db, string scopeKey, DateTime retrievedUtc, bool withDocument = false, bool withChunk = false, bool withAnalysis = false,
        bool withClassification = false)
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
        LitigationOrderDocument? seededDoc = null;
        LitigationOrderChunk? seededChunk = null;
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
            seededDoc = doc;

            if (withChunk)
            {
                seededChunk = new LitigationOrderChunk
                {
                    RequestId = request.RequestId, LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
                    LitigationCaseOrderId = order.LitigationCaseOrderId, LitigationCaseId = c.LitigationCaseId,
                    ChunkIndex = 0, PageNumber = 1, ChunkText = "reused chunk text",
                    Embedding = new SqlVector<float>(new float[768]), EmbeddingModel = "test-model",
                    EmbeddingDimensions = 768, ChunkingVersion = "v1", CreatedDate = DateTime.UtcNow
                };
                db.LitigationOrderChunks.Add(seededChunk);
                await db.SaveChangesAsync();
            }
        }

        if (withAnalysis)
        {
            var run = new LitigationAiAnalysisRun
            {
                RequestId = request.RequestId, RunNumber = 1, Trigger = LitigationAiAnalysisTrigger.Manual,
                TriggerSnapshotId = snapshot.LitigationReportSnapshotId, OriginSnapshotId = snapshot.LitigationReportSnapshotId,
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
            // Real hashes of the seeded chunk's content: the outcome lookup only treats a classification of the order's
            // current text as current, and a byte-identical copy must still match on the reusing request.
            var (evidenceHash, promptHash) = seededChunk is null ? ("none", "none")
                : LitigationOrderClassifier.Hashes(LitigationOrderClassifier.BuildEvidence([seededChunk]));
            if (withClassification && seededDoc is not null)
                db.LitigationOrderClassifications.Add(new LitigationOrderClassification
                {
                    LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, RequestId = request.RequestId, LitigationCaseId = c.LitigationCaseId,
                    LitigationCaseOrderId = seededDoc.LitigationCaseOrderId, LitigationOrderDocumentId = seededDoc.LitigationOrderDocumentId,
                    Status = LitigationAiAnalysisItemStatus.Completed, OutcomeTypesJson = "[\"FinePenalty\"]", FineAmount = 25000m,
                    Confidence = ClassificationConfidence.High, EvidenceJson = "{}", EvidenceHash = evidenceHash,
                    PromptHash = promptHash, ClassificationJson = "{\"status\":\"Completed\"}", CompletedUtc = DateTime.UtcNow
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
