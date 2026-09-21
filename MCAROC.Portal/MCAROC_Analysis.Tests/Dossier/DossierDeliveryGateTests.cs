using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>Coverage for CalculationArtifactGateService (#164 PR4) — the Off/ObserveOnly/Enforced ×
/// missing-snapshot/hold matrix, plus the DossierController-level guarantee that a held artifact is
/// indistinguishable from a genuinely-missing one, even when a PDF for that exact snapshot was already
/// rendered and cached to disk before the hold existed.</summary>
public class DossierDeliveryGateTests : IAsyncLifetime
{
    private string _contentRoot = "";

    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
        _contentRoot = Path.Combine(Path.GetTempPath(), "mcaroc-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // Random, not sequential — CalculationAuditSnapshot has a unique index on (RequestId, IngestionRunId,
    // AnalysisRunId) and the shared local test database is migrated, not recreated, between runs; a fixed
    // id would collide with a row left over from a previous run.
    private static long NextId() => Random.Shared.NextInt64(1, long.MaxValue / 2);

    private static IConfiguration ModeConfig(CalculationAssuranceMode mode) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["CalculationAssurance:Mode"] = mode.ToString() })
        .Build();

    private static CalculationArtifactGateService NewGate(AppDbContext db, CalculationAssuranceMode mode) =>
        new(db, ModeConfig(mode), NullLogger<CalculationArtifactGateService>.Instance);

    private static async Task<CalculationAuditSnapshot> SeedSnapshotAsync(AppDbContext db, long requestId, long ingestionRunId, long analysisRunId)
    {
        var snapshot = new CalculationAuditSnapshot
        {
            RequestId = requestId, IngestionRunId = ingestionRunId, AnalysisRunId = analysisRunId, CreatedUtc = DateTime.UtcNow
        };
        db.CalculationAuditSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot;
    }

    private static async Task AddHoldAsync(AppDbContext db, CalculationAuditSnapshot snapshot, CalculationDiscrepancy discrepancy,
        string? variant = null, bool isActive = true)
    {
        db.CalculationArtifactHolds.Add(new CalculationArtifactHold
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            Variant = variant,
            HoldReason = CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy,
            IsActive = isActive,
            SourceDiscrepancy = discrepancy,
            CreatedUtc = DateTime.UtcNow,
            ReleasedUtc = isActive ? null : DateTime.UtcNow,
            ReleasedByReviewerName = isActive ? null : "test-reviewer"
        });
        await db.SaveChangesAsync();
    }

    private static async Task<CalculationDiscrepancy> SeedConfirmedDiscrepancyAsync(AppDbContext db, CalculationAuditSnapshot snapshot)
    {
        var ledgerEntry = new CalculationLedgerEntry
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            CalculationKey = "Test.Key", MetricLabel = "Test", Period = "FY2025", CreatedUtc = DateTime.UtcNow
        };
        db.CalculationLedgerEntries.Add(ledgerEntry);
        await db.SaveChangesAsync();

        var discrepancy = new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId,
            SourceType = CalculationDiscrepancySourceType.Deterministic,
            OriginCheckKey = "Test.Check",
            PrimaryLedgerEntryId = ledgerEntry.CalculationLedgerEntryId,
            ClaimSummary = "Test discrepancy", Status = CalculationDiscrepancyStatus.Confirmed,
            Severity = CalculationDiscrepancySeverity.Critical, CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow
        };
        db.CalculationDiscrepancies.Add(discrepancy);
        await db.SaveChangesAsync();
        return discrepancy;
    }

    // ── CalculationArtifactGateService — direct coverage ──────────────────────────────────────────

    [Fact]
    public async Task Off_NeverHolds_EvenWithAnActiveHoldOnRecord()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();
        var snapshot = await SeedSnapshotAsync(db, id, id, id);
        var discrepancy = await SeedConfirmedDiscrepancyAsync(db, snapshot);
        await AddHoldAsync(db, snapshot, discrepancy);

        var held = await NewGate(db, CalculationAssuranceMode.Off).IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None);

        Assert.False(held);
    }

    [Fact]
    public async Task Enforced_MissingSnapshot_FailsClosed()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();

        var held = await NewGate(db, CalculationAssuranceMode.Enforced).IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None);

        Assert.True(held);
    }

    [Fact]
    public async Task ObserveOnly_MissingSnapshot_NeverBlocks()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();

        var held = await NewGate(db, CalculationAssuranceMode.ObserveOnly).IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None);

        Assert.False(held);
    }

    [Fact]
    public async Task Enforced_SnapshotWithNoHold_IsNotHeld()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();
        await SeedSnapshotAsync(db, id, id, id);

        var held = await NewGate(db, CalculationAssuranceMode.Enforced).IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None);

        Assert.False(held);
    }

    [Fact]
    public async Task Enforced_ActiveHold_NullVariant_BlocksTheOnlyVariant()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();
        var snapshot = await SeedSnapshotAsync(db, id, id, id);
        var discrepancy = await SeedConfirmedDiscrepancyAsync(db, snapshot);
        await AddHoldAsync(db, snapshot, discrepancy, variant: null);

        var gate = NewGate(db, CalculationAssuranceMode.Enforced);
        Assert.True(await gate.IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None));
    }

    /// <summary>Regression guard for the FullSource/SourceRecord variant removal: a pre-existing hold
    /// row stamped with one of those now-retired variant names must not leak onto Executive lookups —
    /// the match is an exact string comparison, not an enum-aware one (see <see cref="CalculationDiscrepancy"/>'s
    /// <c>Variant</c> doc comment), so a stale value simply never matches anything again.</summary>
    [Fact]
    public async Task Enforced_ActiveHold_OnARetiredVariantName_DoesNotBlockExecutive()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();
        var snapshot = await SeedSnapshotAsync(db, id, id, id);
        var discrepancy = await SeedConfirmedDiscrepancyAsync(db, snapshot);
        await AddHoldAsync(db, snapshot, discrepancy, variant: "SourceRecord");

        var gate = NewGate(db, CalculationAssuranceMode.Enforced);
        Assert.False(await gate.IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None));
    }

    [Fact]
    public async Task Enforced_ReleasedHold_IsNotHeld()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();
        var snapshot = await SeedSnapshotAsync(db, id, id, id);
        var discrepancy = await SeedConfirmedDiscrepancyAsync(db, snapshot);
        await AddHoldAsync(db, snapshot, discrepancy, isActive: false);

        var held = await NewGate(db, CalculationAssuranceMode.Enforced).IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None);

        Assert.False(held);
    }

    [Fact]
    public async Task ObserveOnly_NeverBlocksEvenWithAConfirmedActiveHold()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var id = NextId();
        var snapshot = await SeedSnapshotAsync(db, id, id, id);
        var discrepancy = await SeedConfirmedDiscrepancyAsync(db, snapshot);
        await AddHoldAsync(db, snapshot, discrepancy);

        var held = await NewGate(db, CalculationAssuranceMode.ObserveOnly).IsHeldAsync(id, id, id, DossierVariant.Executive, CancellationToken.None);

        Assert.False(held);
    }

    [Fact]
    public async Task Enforced_HoldOnADifferentIngestionRun_DoesNotBlockTheCurrentOne()
    {
        // Proves the tuple match is exact — an old snapshot's hold must never leak onto a newer,
        // unrelated snapshot for the same request just because the RequestId matches.
        await using var db = DossierGoldenMasterTests.CreateContext();
        var requestId = NextId();
        var oldSnapshot = await SeedSnapshotAsync(db, requestId, 1, 1);
        var oldDiscrepancy = await SeedConfirmedDiscrepancyAsync(db, oldSnapshot);
        await AddHoldAsync(db, oldSnapshot, oldDiscrepancy);
        await SeedSnapshotAsync(db, requestId, 2, 2); // current snapshot, no hold

        var held = await NewGate(db, CalculationAssuranceMode.Enforced).IsHeldAsync(requestId, 2, 2, DossierVariant.Executive, CancellationToken.None);

        Assert.False(held);
    }

    // ── DossierController-level: held is indistinguishable from missing, even with a stale cached PDF ──

    [Fact]
    public async Task Controller_Enforced_ActiveHold_ReturnsNotFound_SameAsAMissingRequest()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);
        var snapshot = await SeedSnapshotAsync(seedDb, requestId, ingestionRunId, analysisRunId);
        var discrepancy = await SeedConfirmedDiscrepancyAsync(seedDb, snapshot);
        await AddHoldAsync(seedDb, snapshot, discrepancy);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db, ModeConfig(CalculationAssuranceMode.Enforced))
            .Download(requestId, "executive", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Controller_Enforced_HeldArtifact_IsBlockedEvenWhenAlreadyCachedOnDiskBeforeTheHoldExisted()
    {
        // The hold check must run before the file-cache read, not just before the render — proven by
        // rendering and caching the PDF successfully first (Mode=Off), then creating a hold and confirming
        // a subsequent download under Enforced is blocked rather than served from the now-stale cache.
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var firstDb = DossierGoldenMasterTests.CreateContext();
        var firstResult = await NewController(firstDb, ModeConfig(CalculationAssuranceMode.Off))
            .Download(requestId, "executive", CancellationToken.None);
        var cachedFile = Assert.IsType<PhysicalFileResult>(firstResult);
        Assert.True(new FileInfo(cachedFile.FileName).Length > 0);
        Assert.True(File.Exists(cachedFile.FileName), "Precondition: the PDF must actually be cached on disk before the hold is created.");

        await using var holdDb = DossierGoldenMasterTests.CreateContext();
        var snapshot = await SeedSnapshotAsync(holdDb, requestId, ingestionRunId, analysisRunId);
        var discrepancy = await SeedConfirmedDiscrepancyAsync(holdDb, snapshot);
        await AddHoldAsync(holdDb, snapshot, discrepancy);

        await using var secondDb = DossierGoldenMasterTests.CreateContext();
        var secondResult = await NewController(secondDb, ModeConfig(CalculationAssuranceMode.Enforced))
            .Download(requestId, "executive", CancellationToken.None);

        Assert.IsType<NotFoundResult>(secondResult);
        // The stale file is untouched on disk (the gate returns before any file-cache logic runs) — this
        // assertion documents that the block came from the gate, not from the file having been deleted.
        Assert.True(File.Exists(cachedFile.FileName));
    }

    [Fact]
    public async Task Controller_Enforced_NoHold_DownloadsNormally()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);
        await SeedSnapshotAsync(seedDb, requestId, ingestionRunId, analysisRunId); // audited, no discrepancy/hold

        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db, ModeConfig(CalculationAssuranceMode.Enforced))
            .Download(requestId, "executive", CancellationToken.None);

        Assert.IsType<PhysicalFileResult>(result);
    }

    private DossierController NewController(AppDbContext db, IConfiguration calculationAssuranceConfig)
    {
        var cacheDb = DossierGoldenMasterTests.CreateContext();
        var cache = new DossierCache(cacheDb, new DossierAssembler(cacheDb),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 256 }));
        var webRoot = Path.Combine(FindRepoRoot(), "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
        var gate = new CalculationArtifactGateService(db, calculationAssuranceConfig, NullLogger<CalculationArtifactGateService>.Instance);
        return new DossierController(db, cache, new DossierPdfRenderer(webRoot), gate, new FakeEnv(_contentRoot, webRoot));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private sealed class FakeEnv(string contentRoot, string webRoot) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
    }
}
