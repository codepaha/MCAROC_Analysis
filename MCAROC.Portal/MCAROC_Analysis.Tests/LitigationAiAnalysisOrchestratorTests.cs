using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>#290 (plan §4.2): <c>CreateOrJoinAsync</c> now persists <c>Trigger</c>/<c>TriggerSnapshotId</c>/
/// <c>OriginSnapshotId</c>, and treats a unique-violation on the "one Auto run per OriginSnapshotId" index
/// (#269) as "already exists → join" — defence in depth behind the admission ledger, which is what actually
/// stops a second request from spending on the same snapshot before reuse (#291) exists to share the result
/// properly. Real SQL Server.</summary>
public sealed class LitigationAiAnalysisOrchestratorTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static LitigationAiAnalysisOrchestrator Orchestrator(AppDbContext db) =>
        new(db, null!, new LitigationAiAnalysisQueue(), Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);

    [Fact]
    public async Task Manual_create_persists_no_trigger_snapshot_when_none_is_supplied()
    {
        var request = await SeedRequestAsync();
        await using var db = CreateContext();

        var run = await Orchestrator(db).CreateOrJoinAsync(request.RequestId, LitigationAiAnalysisTrigger.Manual, null, null, CancellationToken.None);

        Assert.Equal(LitigationAiAnalysisTrigger.Manual, run.Trigger);
        Assert.Null(run.TriggerSnapshotId);
        Assert.Null(run.OriginSnapshotId);
    }

    [Fact]
    public async Task Auto_create_persists_the_snapshot_as_both_trigger_and_origin()
    {
        var request = await SeedRequestAsync();
        var snapshotId = await SeedSnapshotAsync(request.RequestId);
        await using var db = CreateContext();

        var run = await Orchestrator(db).CreateOrJoinAsync(request.RequestId, LitigationAiAnalysisTrigger.Auto, snapshotId, snapshotId, CancellationToken.None);

        Assert.Equal(LitigationAiAnalysisTrigger.Auto, run.Trigger);
        Assert.Equal(snapshotId, run.TriggerSnapshotId);
        Assert.Equal(snapshotId, run.OriginSnapshotId);
    }

    /// <summary>What #291 will actually build on: today nothing shares case/order data across requests, but the
    /// database-level "at most one Auto run per OriginSnapshotId" invariant already holds even if two different
    /// requests' Auto starts somehow raced on one shared snapshot id — the second joins the first's run rather
    /// than failing or double-spending.</summary>
    [Fact]
    public async Task Auto_create_for_a_different_request_sharing_one_origin_snapshot_joins_the_existing_run()
    {
        var requestA = await SeedRequestAsync();
        var requestB = await SeedRequestAsync();
        var snapshotId = await SeedSnapshotAsync(requestA.RequestId);

        await using var dbA = CreateContext();
        var runA = await Orchestrator(dbA).CreateOrJoinAsync(requestA.RequestId, LitigationAiAnalysisTrigger.Auto, snapshotId, snapshotId, CancellationToken.None);

        await using var dbB = CreateContext();
        var runB = await Orchestrator(dbB).CreateOrJoinAsync(requestB.RequestId, LitigationAiAnalysisTrigger.Auto, snapshotId, snapshotId, CancellationToken.None);

        Assert.Equal(runA.LitigationAiAnalysisRunId, runB.LitigationAiAnalysisRunId);
        Assert.Equal(requestA.RequestId, runB.RequestId); // the joined run is still A's — sharing the result is #291's job
        Assert.Equal(1, await dbB.LitigationAiAnalysisRuns.CountAsync(r => r.OriginSnapshotId == snapshotId));
    }

    /// <summary>A second Auto start for the SAME request and snapshot is the ordinary per-request join
    /// (checked first, before the cross-request index even matters) — never two rows.</summary>
    [Fact]
    public async Task Auto_create_twice_for_the_same_request_joins_its_own_active_run()
    {
        var request = await SeedRequestAsync();
        var snapshotId = await SeedSnapshotAsync(request.RequestId);

        await using var db1 = CreateContext();
        var first = await Orchestrator(db1).CreateOrJoinAsync(request.RequestId, LitigationAiAnalysisTrigger.Auto, snapshotId, snapshotId, CancellationToken.None);
        await using var db2 = CreateContext();
        var second = await Orchestrator(db2).CreateOrJoinAsync(request.RequestId, LitigationAiAnalysisTrigger.Auto, snapshotId, snapshotId, CancellationToken.None);

        Assert.Equal(first.LitigationAiAnalysisRunId, second.LitigationAiAnalysisRunId);
        Assert.Equal(1, await db2.LitigationAiAnalysisRuns.CountAsync(r => r.RequestId == request.RequestId));
    }

    private static async Task<long> SeedSnapshotAsync(long requestId)
    {
        await using var db = CreateContext();
        var job = new LitigationSearchJob { RequestId = requestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId, ReportHash = Guid.NewGuid().ToString("N")[..16],
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot.LitigationReportSnapshotId;
    }

    private static async Task<McaRequest> SeedRequestAsync()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "LAO" + Guid.NewGuid().ToString("N")[..7], ClientName = "Orchestrator Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Orchestrator Test Co",
            Cin = $"U{Random.Shared.Next(10000, 99999)}OR1995PLC{Random.Shared.Next(100000, 999999)}",
            RequestNumber = $"LAO-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }
}
