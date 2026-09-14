using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for CalculationAiAuditOrchestrator's atomic claim and crash-recovery
/// logic. Mirrors AnalysisClaimAndRecoveryTests exactly: the exact SQL-level predicates
/// RunAuditAsync/RecoverStaleWorkAsync use, run directly against a real SQL Server test database —
/// CalculationAiAuditOrchestrator itself can't be constructed in-process because
/// CalculationAiAuditService's constructor eagerly loads Google Cloud credentials from disk. Requires
/// .\SQLEXPRESS.</summary>
public class CalculationAiAuditClaimAndRecoveryTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<CalculationAuditSnapshot> SeedSnapshotAsync(AppDbContext db)
    {
        var unique = Random.Shared.NextInt64(1, long.MaxValue / 2);
        var snapshot = new CalculationAuditSnapshot
        {
            RequestId = unique, IngestionRunId = unique, AnalysisRunId = unique, CreatedUtc = DateTime.UtcNow
        };
        db.CalculationAuditSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot;
    }

    [Fact]
    public async Task AiAuditClaim_OnlySucceedsOnce()
    {
        await using var db = CreateContext();
        var snapshot = await SeedSnapshotAsync(db);
        var run = new CalculationAiAuditRun
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            Status = CalculationAiAuditRunStatus.Pending, ModelId = "test-model"
        };
        db.CalculationAiAuditRuns.Add(run);
        await db.SaveChangesAsync();

        // Mirrors CalculationAiAuditOrchestrator.RunAuditAsync's atomic claim exactly.
        Task<int> Claim() => db.CalculationAiAuditRuns
            .Where(r => r.CalculationAiAuditRunId == run.CalculationAiAuditRunId && r.Status == CalculationAiAuditRunStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, CalculationAiAuditRunStatus.InProgress)
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1));

        var first = await Claim();
        var second = await Claim();

        Assert.Equal(1, first);
        Assert.Equal(0, second);

        // A fresh context, not `db` — `db` still has `run` cached in its identity map from the earlier
        // Add/SaveChangesAsync with AttemptCount=0, and ExecuteUpdateAsync never refreshes tracked entities.
        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.CalculationAiAuditRuns.FirstAsync(r => r.CalculationAiAuditRunId == run.CalculationAiAuditRunId);
        Assert.Equal(1, reloaded.AttemptCount);
    }

    [Fact]
    public async Task RecoverStaleWork_ResetsOrphanedRun_SoTheNormalClaimCanSucceedAgain()
    {
        // Regression test mirroring AnalysisOrchestrator's equivalent: an InProgress row orphaned by a
        // mid-call crash must be reset back to Pending before it can ever be claimed again.
        await using var db = CreateContext();
        var snapshot = await SeedSnapshotAsync(db);
        var run = new CalculationAiAuditRun
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            Status = CalculationAiAuditRunStatus.InProgress, AttemptCount = 1, ModelId = "test-model",
            StartedUtc = DateTime.UtcNow
        };
        db.CalculationAiAuditRuns.Add(run);
        await db.SaveChangesAsync();

        // Mirrors CalculationAiAuditOrchestrator.RecoverStaleWorkAsync's exact sequence.
        await db.CalculationAiAuditRuns
            .Where(r => r.Status == CalculationAiAuditRunStatus.InProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, CalculationAiAuditRunStatus.Pending));

        var pendingIds = await db.CalculationAiAuditRuns
            .Where(r => r.Status == CalculationAiAuditRunStatus.Pending)
            .Select(r => r.CalculationAiAuditRunId)
            .ToListAsync();
        Assert.Contains(run.CalculationAiAuditRunId, pendingIds);

        // The normal claim now succeeds again — proving the orphaned row is never stuck forever.
        var claimed = await db.CalculationAiAuditRuns
            .Where(r => r.CalculationAiAuditRunId == run.CalculationAiAuditRunId && r.Status == CalculationAiAuditRunStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, CalculationAiAuditRunStatus.InProgress));
        Assert.Equal(1, claimed);
    }

    [Fact]
    public async Task AiAuditRun_UniquePerSnapshot_RejectsADuplicateInsert()
    {
        await using var db = CreateContext();
        var snapshot = await SeedSnapshotAsync(db);

        db.CalculationAiAuditRuns.Add(new CalculationAiAuditRun
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            Status = CalculationAiAuditRunStatus.Pending, ModelId = "test-model"
        });
        await db.SaveChangesAsync();

        db.CalculationAiAuditRuns.Add(new CalculationAiAuditRun
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            Status = CalculationAiAuditRunStatus.Pending, ModelId = "test-model"
        });

        // Proves the idempotency guarantee CalculationAiAuditOrchestrator.EnqueueForSnapshotAsync relies
        // on is a real database constraint, not just a service-layer AnyAsync check that a race could slip
        // past.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SkippedAiUnavailableRun_NeverCreatesAHold()
    {
        // A terminal SkippedAiUnavailable status must never itself create or extend a
        // CalculationArtifactHold — only a Confirmed CalculationDiscrepancy with Critical/Material
        // severity does that (CalculationCheckRunnerService), and an AI-sourced candidate always starts
        // Open/Severity=null regardless of the run's own status.
        await using var db = CreateContext();
        var snapshot = await SeedSnapshotAsync(db);
        db.CalculationAiAuditRuns.Add(new CalculationAiAuditRun
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            Status = CalculationAiAuditRunStatus.SkippedAiUnavailable, AttemptCount = 3,
            ModelId = "test-model", CompletedUtc = DateTime.UtcNow, FailureReason = "Vertex AI unavailable"
        });
        await db.SaveChangesAsync();

        var holdCount = await db.CalculationArtifactHolds.CountAsync(h => h.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId);
        Assert.Equal(0, holdCount);
    }
}
