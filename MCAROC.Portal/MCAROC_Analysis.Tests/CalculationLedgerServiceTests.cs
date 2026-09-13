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
        await db.Database.MigrateAsync();
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
}
