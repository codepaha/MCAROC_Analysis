using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Relational tests (real .\SQLEXPRESS test DB, not just source configuration) for the two FKs
/// added to LitigationAiAnalysisRun in response to review of PR #269: TriggerSnapshotId/OriginSnapshotId
/// must reject a nonexistent LitigationReportSnapshot and accept a real one. Source-level HasOne/
/// HasForeignKey configuration alone does not prove the constraint is actually deployed — this consolidated
/// migration was regenerated in place, so the only way to be sure the FK survived the regeneration is to
/// exercise it against the real, migrated schema.</summary>
public sealed class LitigationAiAnalysisSnapshotForeignKeyTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(McaRequest Request, LitigationReportSnapshot Snapshot)> SeedRequestAndSnapshotAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "FK Test Co",
            RequestNumber = $"FK-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            KeywordsJson = "[]",
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = $"hash-{Guid.NewGuid():N}",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        return (request, snapshot);
    }

    private static LitigationAiAnalysisRun NewRun(long requestId, long? triggerSnapshotId, long? originSnapshotId) => new()
    {
        RequestId = requestId,
        RunNumber = 1,
        Status = LitigationAiAnalysisRunStatus.Pending,
        Trigger = LitigationAiAnalysisTrigger.Manual,
        TriggerSnapshotId = triggerSnapshotId,
        OriginSnapshotId = originSnapshotId,
        ModelId = "test-model",
        PromptVersion = "v1",
        CreatedUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task ValidSnapshotReferences_AreAccepted()
    {
        await using var db = CreateContext();
        var (request, snapshot) = await SeedRequestAndSnapshotAsync(db);

        db.LitigationAiAnalysisRuns.Add(NewRun(request.RequestId, snapshot.LitigationReportSnapshotId, snapshot.LitigationReportSnapshotId));

        // Must not throw — a real FK to a real row is exactly the accepted case.
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NonexistentOriginSnapshotId_IsRejectedByTheForeignKey()
    {
        await using var db = CreateContext();
        var (request, snapshot) = await SeedRequestAndSnapshotAsync(db);
        var nonexistentSnapshotId = snapshot.LitigationReportSnapshotId + 999_000; // guaranteed absent

        db.LitigationAiAnalysisRuns.Add(NewRun(request.RequestId, triggerSnapshotId: snapshot.LitigationReportSnapshotId, originSnapshotId: nonexistentSnapshotId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NonexistentTriggerSnapshotId_IsRejectedByTheForeignKey()
    {
        await using var db = CreateContext();
        var (request, snapshot) = await SeedRequestAndSnapshotAsync(db);
        var nonexistentSnapshotId = snapshot.LitigationReportSnapshotId + 999_001; // guaranteed absent, distinct from the other test

        db.LitigationAiAnalysisRuns.Add(NewRun(request.RequestId, triggerSnapshotId: nonexistentSnapshotId, originSnapshotId: snapshot.LitigationReportSnapshotId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task NullSnapshotReferences_AreAccepted()
    {
        // A run with no snapshot linkage at all (legacy/manual rows predating this column, per the entity's
        // own doc comment) must remain valid — the FK is on a nullable column, not a required one.
        await using var db = CreateContext();
        var request = (await SeedRequestAndSnapshotAsync(db)).Request;

        db.LitigationAiAnalysisRuns.Add(NewRun(request.RequestId, triggerSnapshotId: null, originSnapshotId: null));

        await db.SaveChangesAsync();
    }
}
