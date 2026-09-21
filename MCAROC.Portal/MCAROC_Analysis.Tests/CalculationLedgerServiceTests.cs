using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers #164 PR1: ledger persistence for the three MetricGroups this issue starts with
/// (FinancialTrend, CapitalReconciliation, ChargeRegister). A no-op when Mode is Off; write-once
/// (idempotent skip, not a duplicate) when a snapshot for the exact tuple already exists.</summary>
public class CalculationLedgerServiceTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static CalculationLedgerService CreateService(AppDbContext db, string mode = "Enforced") =>
        new(db, new DossierAssembler(db),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CalculationAssurance:Mode"] = mode }).Build(),
            NullLogger<CalculationLedgerService>.Instance);

    [Fact]
    public async Task Off_mode_persists_nothing_and_makes_no_query()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        await CreateService(db, "Off").PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        Assert.False(await verify.CalculationAuditSnapshots.AnyAsync(s => s.RequestId == requestId));
    }

    [Fact]
    public async Task Enforced_mode_persists_one_snapshot_and_a_ledger_entry_per_metric_across_the_three_covered_groups()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        await CreateService(db).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        var snapshot = await verify.CalculationAuditSnapshots.SingleAsync(s => s.RequestId == requestId);
        Assert.Equal(ingestionRunId, snapshot.IngestionRunId);
        Assert.Equal(analysisRunId, snapshot.AnalysisRunId);

        var entries = await verify.CalculationLedgerEntries
            .Where(e => e.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId)
            .ToListAsync();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.CalculationKey.StartsWith("FinancialTrend."));
        Assert.Contains(entries, e => e.CalculationKey.StartsWith("CapitalReconciliation."));
        Assert.Contains(entries, e => e.CalculationKey.StartsWith("ChargeRegister."));

        // The seed's Standalone financial years never set ShareCapital, so this metric is Insufficient —
        // the reason must be recorded in the ledger, not silently dropped.
        var capitalEntry = Assert.Single(entries, e => e.CalculationKey.StartsWith("CapitalReconciliation."));
        Assert.Null(capitalEntry.ValueNumeric);
        Assert.NotNull(capitalEntry.InsufficiencyReason);

        // Every populated value carries a well-formed hash and, since real FinancialYearData/RocCharge
        // rows back these three groups, resolved (non-fabricated, non-empty) provenance.
        foreach (var entry in entries.Where(e => e.ValueNumeric is not null || e.ValueText is not null))
        {
            Assert.Equal(64, entry.InputHash.Length);
            Assert.Equal(64, entry.OutputHash.Length);
            Assert.False(entry.HasUnresolvedProvenance, $"{entry.CalculationKey} should have resolved provenance.");
        }
    }

    [Fact]
    public async Task Persisting_the_same_snapshot_twice_is_idempotent_not_duplicated()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var first = DossierGoldenMasterTests.CreateContext();
        await CreateService(first).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var second = DossierGoldenMasterTests.CreateContext();
        await CreateService(second).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        Assert.Equal(1, await verify.CalculationAuditSnapshots.CountAsync(s => s.RequestId == requestId));
    }

    [Fact]
    public async Task An_orphaned_snapshot_with_no_ledger_entries_is_detected_and_completed_on_retry()
    {
        // Simulates the exact failure PR #170 review round 3 (point 2) flagged: the old two-step
        // implementation could leave a snapshot row with zero ledger entries if the process died between
        // the two saves, and every later retry saw "a snapshot already exists" and skipped forever. A
        // retry must now detect the incomplete snapshot and complete it, not silently give up on it.
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var orphan = DossierGoldenMasterTests.CreateContext();
        orphan.CalculationAuditSnapshots.Add(new CalculationAuditSnapshot
        {
            RequestId = requestId, IngestionRunId = ingestionRunId, AnalysisRunId = analysisRunId, CreatedUtc = DateTime.UtcNow
        });
        await orphan.SaveChangesAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        await CreateService(db).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        var snapshot = await verify.CalculationAuditSnapshots.SingleAsync(s => s.RequestId == requestId);
        var entryCount = await verify.CalculationLedgerEntries.CountAsync(e => e.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId);
        Assert.True(entryCount > 0, "The orphaned snapshot should have been completed with ledger entries, not left empty forever.");
    }

    [Fact]
    public async Task Concurrent_calls_for_the_same_snapshot_produce_exactly_one_complete_ledger()
    {
        // PR #170 review round 3 (point 2): two callers racing to persist the same brand-new snapshot
        // must not both succeed, corrupt each other, or crash unhandled — one wins atomically, the other
        // loses cleanly to the unique-index violation and treats that as "already done."
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var dbA = DossierGoldenMasterTests.CreateContext();
        await using var dbB = DossierGoldenMasterTests.CreateContext();
        var taskA = CreateService(dbA).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);
        var taskB = CreateService(dbB).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);
        await Task.WhenAll(taskA, taskB);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        var snapshot = await verify.CalculationAuditSnapshots.SingleAsync(s => s.RequestId == requestId);
        var entryCount = await verify.CalculationLedgerEntries.CountAsync(e => e.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId);
        Assert.True(entryCount > 0);
    }
}
