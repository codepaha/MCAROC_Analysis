using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Proves the #164 schema's composite foreign keys actually reject cross-snapshot evidence at
/// the database level — not just that the happy path works. A CalculationDiscrepancy (or
/// CalculationArtifactHold) whose referenced ledger entry/discrepancy belongs to a *different*
/// CalculationAuditSnapshotId must fail to save.</summary>
public class CalculationAuditSnapshotIntegrityTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(long SnapshotAId, long SnapshotBId, long LedgerEntryUnderAId)> SeedTwoSnapshotsAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();

        // This test DB persists across test methods with no per-test cleanup/rollback (unlike
        // DossierTestSeed, which always lets EF auto-generate its RequestId via SaveChangesAsync) — a
        // fixed RequestId here would collide with a prior run's leftover row on the unique
        // (RequestId, IngestionRunId, AnalysisRunId) index. Use a random one so every call is distinct.
        var requestId = Random.Shared.NextInt64(1_000_000, long.MaxValue / 2);
        var snapshotA = new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = 1, AnalysisRunId = 1, CreatedUtc = DateTime.UtcNow };
        var snapshotB = new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = 1, AnalysisRunId = 2, CreatedUtc = DateTime.UtcNow };
        db.CalculationAuditSnapshots.AddRange(snapshotA, snapshotB);
        await db.SaveChangesAsync();

        var ledgerEntryUnderA = new CalculationLedgerEntry
        {
            CalculationAuditSnapshotId = snapshotA.CalculationAuditSnapshotId,
            CalculationKey = "Test.Metric",
            Period = "FY2025",
            ValueNumeric = 42m,
            InputsJson = "[]",
            SourceRowRefsJson = "[]",
            InputHash = new string('a', 64),
            OutputHash = new string('b', 64),
            CreatedUtc = DateTime.UtcNow
        };
        db.CalculationLedgerEntries.Add(ledgerEntryUnderA);
        await db.SaveChangesAsync();

        return (snapshotA.CalculationAuditSnapshotId, snapshotB.CalculationAuditSnapshotId, ledgerEntryUnderA.CalculationLedgerEntryId);
    }

    [Fact]
    public async Task A_discrepancy_cannot_cite_a_ledger_entry_from_a_different_snapshot()
    {
        var (snapshotAId, snapshotBId, ledgerEntryUnderAId) = await SeedTwoSnapshotsAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        db.CalculationDiscrepancies.Add(new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshotBId, // wrong snapshot on purpose
            SourceType = CalculationDiscrepancySourceType.Deterministic,
            PrimaryLedgerEntryId = ledgerEntryUnderAId, // belongs to snapshot A, not B
            ClaimSummary = "cross-snapshot evidence attempt",
            CreatedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_discrepancy_saves_successfully_when_its_ledger_entry_belongs_to_the_same_snapshot()
    {
        var (snapshotAId, _, ledgerEntryUnderAId) = await SeedTwoSnapshotsAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        db.CalculationDiscrepancies.Add(new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshotAId, // correct snapshot
            SourceType = CalculationDiscrepancySourceType.Deterministic,
            PrimaryLedgerEntryId = ledgerEntryUnderAId,
            ClaimSummary = "same-snapshot evidence",
            CreatedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow
        });

        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task A_hold_cannot_cite_a_discrepancy_from_a_different_snapshot()
    {
        var (snapshotAId, snapshotBId, ledgerEntryUnderAId) = await SeedTwoSnapshotsAsync();

        await using var seedDiscrepancy = DossierGoldenMasterTests.CreateContext();
        var discrepancyUnderA = new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshotAId,
            SourceType = CalculationDiscrepancySourceType.Deterministic,
            PrimaryLedgerEntryId = ledgerEntryUnderAId,
            ClaimSummary = "confirmed critical issue",
            Status = CalculationDiscrepancyStatus.Confirmed,
            Severity = CalculationDiscrepancySeverity.Critical,
            CreatedUtc = DateTime.UtcNow,
            LastUpdatedUtc = DateTime.UtcNow
        };
        seedDiscrepancy.CalculationDiscrepancies.Add(discrepancyUnderA);
        await seedDiscrepancy.SaveChangesAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        db.CalculationArtifactHolds.Add(new CalculationArtifactHold
        {
            CalculationAuditSnapshotId = snapshotBId, // wrong snapshot on purpose
            HoldReason = CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy,
            SourceDiscrepancyId = discrepancyUnderA.CalculationDiscrepancyId, // belongs to snapshot A, not B
            CreatedUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
