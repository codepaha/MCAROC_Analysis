using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MCAROC_Analysis.Services.Analysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for AnalysisOrchestrator's atomic claim and crash-recovery fix. These
/// mirror the exact SQL-level predicates AnalysisOrchestrator.RunAnalysisAsync/RecoverStaleWorkAsync use
/// directly against a real SQL Server test database, the same boundary already established for
/// FilingClaimSemanticsTests in Phase 2 — AnalysisOrchestrator itself can't be constructed in-process
/// because AiCrossSectionAnalysisService's constructor eagerly loads Google Cloud credentials from disk.
/// Requires .\SQLEXPRESS.</summary>
public class AnalysisClaimAndRecoveryTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<McaRequest> SeedRequestAsync(AppDbContext db, RequestStatus status, long ingestionRunId = 1)
    {
        var client = new Client { ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Test Client", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"TEST-{Guid.NewGuid():N}", RequestStatus = status, CreatedDate = DateTime.UtcNow,
            LatestCompletedIngestionRunId = ingestionRunId
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    [Fact]
    public async Task AnalysisClaim_OnlySucceedsOnce()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, RequestStatus.DataExtracted);

        // Mirrors AnalysisOrchestrator.RunAnalysisAsync's atomic claim exactly.
        Task<int> Claim() => db.Requests
            .Where(r => r.RequestId == request.RequestId && r.RequestStatus == RequestStatus.DataExtracted)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AiAnalysisInProgress));

        var first = await Claim();
        var second = await Claim();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task RecoverStaleWork_ResetsOrphanedRunAndRequest_SoTheNormalClaimCanSucceedAgain()
    {
        // Regression test for the review's key finding: the atomic claim only matches
        // RequestStatus=DataExtracted, so a request already at AiAnalysisInProgress from a crashed run
        // would never satisfy it again if simply re-enqueued. Recovery must reset first.
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, RequestStatus.AiAnalysisInProgress);
        var orphanedRun = new AnalysisRun
        {
            RequestId = request.RequestId, IngestionRunId = 1, RunNumber = 1,
            Status = AnalysisRunStatus.Running, StartedDate = DateTime.UtcNow
        };
        db.AnalysisRuns.Add(orphanedRun);
        await db.SaveChangesAsync();

        // Mirrors AnalysisOrchestrator.RecoverStaleWorkAsync's exact sequence.
        var stuckRequestIds = await db.Requests
            .Where(r => r.RequestStatus == RequestStatus.AiAnalysisInProgress)
            .Select(r => r.RequestId)
            .ToListAsync();
        Assert.Contains(request.RequestId, stuckRequestIds);

        await db.AnalysisRuns
            .Where(a => a.RequestId == request.RequestId && a.Status == AnalysisRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AnalysisRunStatus.Failed)
                .SetProperty(a => a.FailureReason, "Application restarted mid-analysis."));

        await db.Requests.Where(r => r.RequestId == request.RequestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.DataExtracted));

        await using var verifyDb = CreateContext();
        var reloadedRun = await verifyDb.AnalysisRuns.FirstAsync(a => a.AnalysisRunId == orphanedRun.AnalysisRunId);
        Assert.Equal(AnalysisRunStatus.Failed, reloadedRun.Status);
        Assert.Equal("Application restarted mid-analysis.", reloadedRun.FailureReason);

        var reloadedRequest = await verifyDb.Requests.FirstAsync(r => r.RequestId == request.RequestId);
        Assert.Equal(RequestStatus.DataExtracted, reloadedRequest.RequestStatus);

        // The normal claim now succeeds again — proving the "never resumes" bug is fixed.
        var claimed = await verifyDb.Requests
            .Where(r => r.RequestId == request.RequestId && r.RequestStatus == RequestStatus.DataExtracted)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestStatus, RequestStatus.AiAnalysisInProgress));
        Assert.Equal(1, claimed);
    }

    [Fact]
    public async Task AutomaticRetry_RestartHonoursTheRemainingWait_AndFailedAttemptsAreCountedPerIngestionRun()
    {
        await using var db = CreateContext();
        var due = await SeedRequestAsync(db, RequestStatus.DataExtracted);
        var notYet = await SeedRequestAsync(db, RequestStatus.DataExtracted);
        var running = await SeedRequestAsync(db, RequestStatus.DataExtracted);
        db.AnalysisRuns.AddRange(
            new AnalysisRun { RequestId = notYet.RequestId, IngestionRunId = 1, RunNumber = 1, Status = AnalysisRunStatus.Failed, StartedDate = DateTime.UtcNow },
            new AnalysisRun { RequestId = notYet.RequestId, IngestionRunId = 1, RunNumber = 2, Status = AnalysisRunStatus.Failed, StartedDate = DateTime.UtcNow },
            new AnalysisRun { RequestId = notYet.RequestId, IngestionRunId = 2, RunNumber = 3, Status = AnalysisRunStatus.Failed, StartedDate = DateTime.UtcNow },
            new AnalysisRun { RequestId = running.RequestId, IngestionRunId = 1, RunNumber = 1, Status = AnalysisRunStatus.Running, StartedDate = DateTime.UtcNow });
        await db.SaveChangesAsync();

        // Times are set last, so the seeding above does not eat into the wait.
        await db.Requests.Where(r => r.RequestId == due.RequestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.AnalysisRetryAfter, DateTime.UtcNow.AddSeconds(-5)));
        await db.Requests.Where(r => r.RequestId == notYet.RequestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.AnalysisRetryAfter, DateTime.UtcNow.AddSeconds(5)));
        // The app restarts: the real recovery runs against a fresh queue.
        var queue = new AnalysisQueue();
        var orchestrator = new MCAROC_Analysis.Services.Analysis.AnalysisOrchestrator(
            db, null!, null!, queue, null!, null!, null!, NullLogger<MCAROC_Analysis.Services.Analysis.AnalysisOrchestrator>.Instance);
        await orchestrator.RecoverStaleWorkAsync(CancellationToken.None);

        var seen = new List<long>();
        async Task DrainAsync(TimeSpan span)
        {
            using var cts = new CancellationTokenSource(span);
            try { await foreach (var id in queue.ReadAllAsync(cts.Token)) seen.Add(id); } catch (OperationCanceledException) { }
        }
        await DrainAsync(TimeSpan.FromMilliseconds(1500));
        Assert.Contains(due.RequestId, seen);                 // its wait was already over: queued at once
        Assert.DoesNotContain(notYet.RequestId, seen);        // restart during the wait: not retried early
        Assert.DoesNotContain(running.RequestId, seen);       // a run already in flight is left alone

        await DrainAsync(TimeSpan.FromSeconds(8));
        Assert.Contains(notYet.RequestId, seen);              // queued once the remaining wait has passed

        // Only the attempts on the current ingestion run count towards the retry budget.
        var failedAttempts = await db.AnalysisRuns.CountAsync(
            a => a.RequestId == notYet.RequestId && a.IngestionRunId == 1 && a.Status == AnalysisRunStatus.Failed);
        Assert.Equal(2, failedAttempts);
        Assert.True(failedAttempts <= MCAROC_Analysis.Services.Analysis.AnalysisOrchestrator.MaxAutomaticRetries);
    }

    [Fact]
    public async Task AutomaticRetry_AnEarlyDuplicateQueueItemCannotClaimTheRequestBeforeItIsDue()
    {
        await using var db = CreateContext();
        var waiting = await SeedRequestAsync(db, RequestStatus.DataExtracted);
        await db.Requests.Where(r => r.RequestId == waiting.RequestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AnalysisRetryAfter, DateTime.UtcNow.AddMinutes(5)));

        var orchestrator = new AnalysisOrchestrator(
            db, null!, null!, new AnalysisQueue(), null!, null!, null!, NullLogger<AnalysisOrchestrator>.Instance);
        await orchestrator.RunAnalysisAsync(waiting.RequestId, CancellationToken.None);   // the early duplicate

        await using var verify = CreateContext();
        Assert.Equal(RequestStatus.DataExtracted, (await verify.Requests.FirstAsync(r => r.RequestId == waiting.RequestId)).RequestStatus);
        Assert.False(await verify.AnalysisRuns.AnyAsync(a => a.RequestId == waiting.RequestId));
    }

    [Fact]
    public async Task AnalysisRun_UniqueRequestIdAndRunNumber_RejectsADuplicateInsert()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, RequestStatus.AiAnalysisInProgress);

        db.AnalysisRuns.Add(new AnalysisRun { RequestId = request.RequestId, IngestionRunId = 1, RunNumber = 1, Status = AnalysisRunStatus.Running, StartedDate = DateTime.UtcNow });
        await db.SaveChangesAsync();

        db.AnalysisRuns.Add(new AnalysisRun { RequestId = request.RequestId, IngestionRunId = 1, RunNumber = 1, Status = AnalysisRunStatus.Running, StartedDate = DateTime.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
