using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Integration coverage for #164 PR2's severity-to-hold wiring, running the real ledger +
/// check-runner pipeline together against the seeded DB (not synthetic outcomes) — proves a confirmed
/// Critical deterministic finding actually creates an active hold citing the right discrepancy, and that
/// a clean run creates none.</summary>
public class CalculationHoldCreationTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static IConfiguration EnforcedConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CalculationAssurance:Mode"] = "Enforced" }).Build();

    private static CalculationLedgerService LedgerService(AppDbContext db) =>
        new(db, new DossierAssembler(db), EnforcedConfig(), NullLogger<CalculationLedgerService>.Instance);

    private static CalculationCheckRunnerService CheckRunner(AppDbContext db) =>
        new(db, new DossierAssembler(db), EnforcedConfig(), NullLogger<CalculationCheckRunnerService>.Instance);

    [Fact]
    public async Task A_clean_seeded_snapshot_produces_no_holds()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        await LedgerService(db).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);
        await CheckRunner(db).RunChecksAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        var snapshot = await verify.CalculationAuditSnapshots.SingleAsync(s => s.RequestId == requestId);
        Assert.False(await verify.CalculationArtifactHolds.AnyAsync(h => h.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId));
        Assert.NotEmpty(await verify.CalculationCheckResults.Where(c => c.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId).ToListAsync());
    }

    [Fact]
    public async Task A_confirmed_critical_finding_creates_an_active_hold_citing_the_discrepancy()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var ledgerDb = DossierGoldenMasterTests.CreateContext();
        await LedgerService(ledgerDb).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);

        // Simulate a stale/tampered ledger row: the capital-reconciliation entry's stored value no longer
        // matches what a fresh recompute over the same source rows gives — RecomputeIntegrity must fire.
        await using var corrupt = DossierGoldenMasterTests.CreateContext();
        var snapshot = await corrupt.CalculationAuditSnapshots.SingleAsync(s => s.RequestId == requestId);
        await corrupt.CompanyProfiles.Where(p => p.IngestionRunId == ingestionRunId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PaidUpCapital, 80m));
        await corrupt.FinancialYearData.Where(f => f.IngestionRunId == ingestionRunId && f.FinancialYear == 2025)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ShareCapital, 78m)); // real diff would be 2, not 0
        await corrupt.CalculationLedgerEntries
            .Where(e => e.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId && e.CalculationKey.StartsWith("CapitalReconciliation."))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ValueNumeric, 0m).SetProperty(e => e.InsufficiencyReason, (string?)null));

        await using var db = DossierGoldenMasterTests.CreateContext();
        await CheckRunner(db).RunChecksAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        var discrepancy = await verify.CalculationDiscrepancies.SingleAsync(d => d.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId);
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed, discrepancy.Status);
        Assert.Equal(CalculationDiscrepancySeverity.Critical, discrepancy.Severity);
        Assert.Equal(CalculationDiscrepancySourceType.Deterministic, discrepancy.SourceType);

        var hold = await verify.CalculationArtifactHolds.SingleAsync(h => h.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId);
        Assert.True(hold.IsActive);
        Assert.Equal(CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy, hold.HoldReason);
        Assert.Equal(discrepancy.CalculationDiscrepancyId, hold.SourceDiscrepancyId);
    }

    [Fact]
    public async Task Concurrent_check_runs_for_the_same_snapshot_produce_exactly_one_set_of_results()
    {
        // Proves the unique (CalculationAuditSnapshotId, CheckKey) index (fixed alongside PR #170's own
        // ledger-concurrency fix) actually backs the runner's "already ran" guard: two simultaneous
        // RunChecksAsync calls for the same snapshot must not both pass it and persist duplicate
        // check results/discrepancies/holds.
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var ledgerDb = DossierGoldenMasterTests.CreateContext();
        await LedgerService(ledgerDb).PersistSnapshotAsync(requestId, ingestionRunId, analysisRunId, default);

        await using var dbA = DossierGoldenMasterTests.CreateContext();
        await using var dbB = DossierGoldenMasterTests.CreateContext();
        var taskA = CheckRunner(dbA).RunChecksAsync(requestId, ingestionRunId, analysisRunId, default);
        var taskB = CheckRunner(dbB).RunChecksAsync(requestId, ingestionRunId, analysisRunId, default);
        await Task.WhenAll(taskA, taskB);

        await using var verify = DossierGoldenMasterTests.CreateContext();
        var snapshot = await verify.CalculationAuditSnapshots.SingleAsync(s => s.RequestId == requestId);

        var checkKeys = await verify.CalculationCheckResults
            .Where(c => c.CalculationAuditSnapshotId == snapshot.CalculationAuditSnapshotId)
            .Select(c => c.CheckKey)
            .ToListAsync();
        Assert.NotEmpty(checkKeys);
        Assert.Equal(checkKeys.Count, checkKeys.Distinct().Count()); // no duplicate CheckKey rows
    }
}
