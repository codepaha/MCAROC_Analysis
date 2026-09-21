using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.Registry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class RegistryDashboardTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private readonly List<string> _seededIdentifiers = [];
    private readonly List<long> _seededJobIds = [];

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();

        if (_seededIdentifiers.Count > 0)
        {
            await db.CompanyMasterRecords
                .Where(r => _seededIdentifiers.Contains(r.Identifier))
                .ExecuteDeleteAsync();
        }

        if (_seededJobIds.Count > 0)
        {
            await db.CompanyMasterSyncJobs
                .Where(j => _seededJobIds.Contains(j.JobId))
                .ExecuteDeleteAsync();
        }
    }

    private async Task SeedRecordAsync(
        AppDbContext db,
        string identifier,
        string name,
        CompanyMasterRecordType recordType,
        string? status = "Active",
        string? state = "Maharashtra",
        DateOnly? regDate = null,
        string? cls = "Private",
        string? industry = "Technology",
        decimal? authCap = 1000000m,
        string? country = null)
    {
        var record = new CompanyMasterRecord
        {
            Identifier = identifier,
            Name = name,
            RecordType = recordType,
            Status = status,
            State = state,
            RegistrationDate = regDate ?? new DateOnly(2022, 5, 15),
            Class = cls,
            IndustrialClassification = industry,
            AuthorizedCapital = authCap,
            Country = country
        };
        db.CompanyMasterRecords.Add(record);
        await db.SaveChangesAsync();
        _seededIdentifiers.Add(identifier);
    }

    private async Task<CompanyMasterSyncJob> SeedSyncJobAsync(
        AppDbContext db,
        CompanyMasterSyncJobStatus status,
        DateOnly? publishedDate = null)
    {
        var job = new CompanyMasterSyncJob
        {
            Status = status,
            PublishedDate = publishedDate ?? new DateOnly(2026, 9, 19),
            PublishedDateRaw = "19-09-2026",
            TriggerType = CompanyMasterSyncTriggerType.Scheduled,
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = status == CompanyMasterSyncJobStatus.Completed ? DateTime.UtcNow : null
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        _seededJobIds.Add(job.JobId);
        return job;
    }

    private static CompanyRegistryQueryService CreateQueryService(
        AppDbContext db,
        IMemoryCache cache,
        IRegistryPromotionCoordinator? coordinator = null,
        IRegistrySnapshotStore? snapshotStore = null,
        SemaphoreSlim? localRebuildLock = null)
    {
        var coord = coordinator ?? new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        var store = snapshotStore ?? new FileRegistrySnapshotStore(Options.Create(new RegistrySnapshotStoreOptions()));
        return new CompanyRegistryQueryService(db, cache, coord, store, NullLogger<CompanyRegistryQueryService>.Instance, localRebuildLock);
    }

    [Fact]
    public async Task RegistryExplorer_ExactIdentifierSeek_ReturnsExpectedRow()
    {
        await using var db = CreateContext();
        var id = $"U24246DL2003PTC{Random.Shared.Next(100000, 999999)}";
        await SeedRecordAsync(db, id, "Test Exact Lookup Pvt Ltd", CompanyMasterRecordType.Company);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateQueryService(db, cache);

        var result = await service.SearchExplorerAsync(new RegistryExplorerCriteria { Q = id });

        Assert.True(result.SearchExecuted);
        Assert.Null(result.ValidationErrorMessage);
        Assert.Single(result.Items);
        Assert.Equal(id, result.Items[0].Identifier);
        Assert.Equal("Test Exact Lookup Pvt Ltd", result.Items[0].Name);
        Assert.False(result.HasNextPage);
    }

    [Fact]
    public async Task RegistryExplorer_NamePrefix_RequiresRecordTypeAndMinLength()
    {
        await using var db = CreateContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateQueryService(db, cache);

        // Missing record type
        var noType = await service.SearchExplorerAsync(new RegistryExplorerCriteria { Q = "ABC" });
        Assert.NotNull(noType.ValidationErrorMessage);
        Assert.Contains("requires selecting an Entity Type", noType.ValidationErrorMessage);

        // Prefix too short (< 3 chars)
        var tooShort = await service.SearchExplorerAsync(new RegistryExplorerCriteria
        {
            Q = "AB",
            RecordType = CompanyMasterRecordType.Company
        });
        Assert.NotNull(tooShort.ValidationErrorMessage);
        Assert.Contains("requires at least 3 characters", tooShort.ValidationErrorMessage);
    }

    [Fact]
    public async Task RegistryExplorer_KeysetContinuation_DeterministicWithDuplicateNames()
    {
        await using var db = CreateContext();
        var prefix = $"TST_{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var sharedName = $"{prefix} Common Name Solutions Private Limited";

        var id1 = $"U11111DL{Guid.NewGuid():N}"[..21].ToUpperInvariant();
        var id2 = $"U22222DL{Guid.NewGuid():N}"[..21].ToUpperInvariant();
        var id3 = $"U33333DL{Guid.NewGuid():N}"[..21].ToUpperInvariant();

        await SeedRecordAsync(db, id1, sharedName, CompanyMasterRecordType.Company);
        await SeedRecordAsync(db, id2, sharedName, CompanyMasterRecordType.Company);
        await SeedRecordAsync(db, id3, sharedName, CompanyMasterRecordType.Company);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateQueryService(db, cache);

        // First page with PageSize = 2
        var page1 = await service.SearchExplorerAsync(new RegistryExplorerCriteria
        {
            Q = prefix,
            RecordType = CompanyMasterRecordType.Company,
            PageSize = 2
        });

        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasNextPage);
        Assert.NotNull(page1.NextCursorName);
        Assert.NotNull(page1.NextCursorIdentifier);

        // Second page using returned cursor
        var page2 = await service.SearchExplorerAsync(new RegistryExplorerCriteria
        {
            Q = prefix,
            RecordType = CompanyMasterRecordType.Company,
            PageSize = 2,
            CursorName = page1.NextCursorName,
            CursorIdentifier = page1.NextCursorIdentifier
        });

        Assert.Single(page2.Items);
        Assert.False(page2.HasNextPage);

        // Verify all 3 distinct items retrieved without duplication or loss
        var allIds = page1.Items.Select(x => x.Identifier).Concat(page2.Items.Select(x => x.Identifier)).ToList();
        Assert.Equal(3, allIds.Distinct().Count());
        Assert.Contains(id1, allIds);
        Assert.Contains(id2, allIds);
        Assert.Contains(id3, allIds);
    }

    [Fact]
    public async Task RegistryExplorer_RejectsSecondaryFilters_InV1()
    {
        await using var db = CreateContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateQueryService(db, cache);

        var result = await service.SearchExplorerAsync(new RegistryExplorerCriteria
        {
            Q = "TATA",
            RecordType = CompanyMasterRecordType.Company,
            State = "Maharashtra" // Secondary unindexed filter
        });

        Assert.NotNull(result.ValidationErrorMessage);
        Assert.Contains("Secondary filtering on State, Status, Industry, Class, or Year is disabled in v1", result.ValidationErrorMessage);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task RegistryAggregates_SnapshotConsistency_DuringSync_WarmCache()
    {
        await using var db = CreateContext();
        var completedJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));
        var activeJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Promoting);

        var cache = new MemoryCache(new MemoryCacheOptions());
        // Populate cache with last-known-good snapshot
        var cachedData = new RegistryAggregateData
        {
            Metadata = new RegistrySnapshotMetadata
            {
                PublishedDate = completedJob.PublishedDate,
                CompletedUtc = completedJob.CompletedUtc,
                TotalRecords = 1000
            }
        };
        cache.Set(CompanyRegistryQueryService.CacheKeyForJob(completedJob.JobId), cachedData);

        var service = CreateQueryService(db, cache);
        var vm = await service.GetDashboardAsync("overview", null);

        Assert.Equal(RegistrySnapshotState.VerifiedSnapshot, vm.State);
        Assert.NotNull(vm.Aggregates);
        Assert.True(vm.Aggregates.Metadata.IsSyncInProgress);
        Assert.Equal(CompanyMasterSyncJobStatus.Promoting, vm.Aggregates.Metadata.ActiveSyncStatus);
        Assert.Equal(completedJob.PublishedDate, vm.Aggregates.Metadata.PublishedDate);
    }

    [Fact]
    public async Task RegistryAggregates_ColdCache_ActivePromotion_TemporarilyUnavailable()
    {
        await using var db = CreateContext();
        await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));
        await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Promoting);

        // Empty/cold cache
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateQueryService(db, cache);

        var vm = await service.GetDashboardAsync("overview", null);

        Assert.Equal(RegistrySnapshotState.SyncColdUnavailable, vm.State);
        Assert.Null(vm.Aggregates);
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("Verified Aggregates Temporarily Unavailable", vm.StatusMessage);
    }

    [Fact]
    public async Task RegistryAggregates_LegacyUnverifiedState_WhenRecordsExistWithoutSyncJob()
    {
        await using var db = CreateContext();
        var id = $"U{Guid.NewGuid():N}"[..21].ToUpperInvariant();
        await SeedRecordAsync(db, id, "Legacy Company Ltd", CompanyMasterRecordType.Company);

        // Ensure no completed sync jobs exist
        await db.CompanyMasterSyncJobs.ExecuteDeleteAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateQueryService(db, cache);

        var vm = await service.GetDashboardAsync("overview", null);

        Assert.Equal(RegistrySnapshotState.UnverifiedLegacyImport, vm.State);
        Assert.Null(vm.Aggregates);
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("Legacy Master Import Detected", vm.StatusMessage);
    }

    [Fact]
    public void RegistryAggregates_StatusVocabularyCheck_ConditionalCardsAndOtherBucket()
    {
        // Dynamic status mapping test
        var rows = new List<(string? Status, int Count)>
        {
            ("Active", 500),
            ("Strike Off", 200),
            ("Under CIRP", 10),
            ("Under Liquidation", 20),
            ("Amalgamated", 15),
            ("Unknown Mystery Status", 5),
            (null, 10)
        };

        var observedWithCirp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Active", "Strike Off", "Under CIRP", "Under Liquidation", "Amalgamated" };
        var metricsWithCirp = CompanyRegistryQueryService.MapStatusMetrics(rows, observedWithCirp, isForeign: false);

        Assert.Equal(500, metricsWithCirp.Active);
        Assert.Equal(200, metricsWithCirp.StrikeOff);
        Assert.Equal(10, metricsWithCirp.UnderCirp);
        Assert.Equal(20, metricsWithCirp.UnderLiquidation);
        Assert.Equal(30, metricsWithCirp.OtherUnclassified); // 15 Amalgamated + 5 Unknown + 10 null
        Assert.True(metricsWithCirp.HasObservedCirp);
        Assert.True(metricsWithCirp.HasObservedLiquidation);
        Assert.Equal(760, metricsWithCirp.Total);

        // If CIRP is not observed in the dataset vocabulary:
        var observedWithoutCirp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Active", "Strike Off" };
        var metricsWithoutCirp = CompanyRegistryQueryService.MapStatusMetrics(rows, observedWithoutCirp, isForeign: false);
        Assert.False(metricsWithoutCirp.HasObservedCirp);
        Assert.False(metricsWithoutCirp.HasObservedLiquidation);

        // Foreign dataset never enables CIRP
        var foreignMetrics = CompanyRegistryQueryService.MapStatusMetrics(rows, observedWithCirp, isForeign: true);
        Assert.False(foreignMetrics.HasObservedCirp);
        Assert.False(foreignMetrics.HasObservedLiquidation);
    }

    [Fact]
    public async Task AutoFetchController_Prefill_CinAndLlpin()
    {
        var optionsMock = Options.Create(new MCAROC_Analysis.Services.AutoFetch.ReferenceToolOptions { BaseUrl = "https://example.test", SessionCookie = "abc" });
        await using var db = CreateContext();
        var controller = new AutoFetchController(db, null!, null!, null!, optionsMock);

        // Company CIN prefill
        var resultCo = await controller.New("U24246DL2003PTC118255") as ViewResult;
        Assert.NotNull(resultCo);
        var modelCo = resultCo.Model as MCAROC_Analysis.Models.AutoFetchRequestViewModel;
        Assert.NotNull(modelCo);
        Assert.Equal("U24246DL2003PTC118255", modelCo.Cin);
        Assert.Equal(EntityType.Company, modelCo.EntityType);

        // LLPIN prefill
        var resultLlp = await controller.New("AAA-1234") as ViewResult;
        Assert.NotNull(resultLlp);
        var modelLlp = resultLlp.Model as MCAROC_Analysis.Models.AutoFetchRequestViewModel;
        Assert.NotNull(modelLlp);
        Assert.Equal("AAA-1234", modelLlp.Cin);
        Assert.Equal(EntityType.LLP, modelLlp.EntityType);

        // Null / blank leaves blank
        var resultEmpty = await controller.New(null) as ViewResult;
        Assert.NotNull(resultEmpty);
        var modelEmpty = resultEmpty.Model as MCAROC_Analysis.Models.AutoFetchRequestViewModel;
        Assert.NotNull(modelEmpty);
        Assert.Null(modelEmpty.Cin);
    }

    [Fact]
    public async Task RegistryPromotionCoordinator_RebuildHoldsSharedLock_PromotionBlocksAndJobRemainsStaged()
    {
        await using var db = CreateContext();
        var completedJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));

        long token = 42;
        var stagedJob = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staged,
            CreatedUtc = DateTime.UtcNow
        };
        db.CompanyMasterSyncJobs.Add(stagedJob);
        await db.SaveChangesAsync();
        _seededJobIds.Add(stagedJob.JobId);

        var realCoordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        var barrierCoordinator = new TestBarrierPromotionCoordinator(realCoordinator);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var queryService = CreateQueryService(db, cache, barrierCoordinator);

        // Start GetDashboardAsync in background. It acquires shared rebuild lock, then pauses at barrier.
        var dashboardTask = Task.Run(() => queryService.GetDashboardAsync("overview", null));

        // Wait until shared rebuild gate is actively held on dedicated SQL connection
        await barrierCoordinator.RebuildGateAcquiredSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Create independent delta service with its own connection & coordinator
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = ConnectionString })
            .Build();
        var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);
        var promoCoordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        var deltaService = new CompanyMasterDeltaService(db, config, proxyPool, lease, promoCoordinator, NullLogger<CompanyMasterDeltaService>.Instance);

        // Attempting exclusive promotion admission must time out because shared lock is held
        var timeoutEx = await Assert.ThrowsAsync<TimeoutException>(() =>
            deltaService.PromoteStagedDeltaAsync(stagedJob.JobId, token, batchSize: 100, admissionTimeout: TimeSpan.FromMilliseconds(200)));
        Assert.Contains("Failed to acquire exclusive SQL applock", timeoutEx.Message);

        // Verify the job was NOT advanced to Promoting and remains Staged
        await using var checkDb = CreateContext();
        var refreshedJob = await checkDb.CompanyMasterSyncJobs.FindAsync(stagedJob.JobId);
        Assert.NotNull(refreshedJob);
        Assert.Equal(CompanyMasterSyncJobStatus.Staged, refreshedJob.Status);

        // Release barrier to let rebuild finish
        barrierCoordinator.ProceedWithRebuildSignal.TrySetResult(true);
        var vm = await dashboardTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(RegistrySnapshotState.VerifiedSnapshot, vm.State);
    }

    [Fact]
    public async Task RegistryPromotionCoordinator_ExclusivePromotionHoldsLock_ColdCacheRebuildProbesAndExecutesZeroMasterQueries()
    {
        await using var db = CreateContext();
        var completedJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));

        // Simulated external node acquires exclusive promotion lock
        var promoCoordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        await using var admissionScope = await promoCoordinator.AcquirePromotionAdmissionAsync(999999, TimeSpan.FromSeconds(5));

        // QueryService runs on cold cache with interceptor counting CompanyMasterRecords aggregate queries
        var interceptor = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var interceptedDb = new AppDbContext(options);

        var queryCoordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var queryService = CreateQueryService(interceptedDb, cache, queryCoordinator);

        var vm = await queryService.GetDashboardAsync("overview", null);

        Assert.Equal(RegistrySnapshotState.SyncColdUnavailable, vm.State);
        Assert.Null(vm.Aggregates);
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("Verified Aggregates Temporarily Unavailable", vm.StatusMessage);

        // Proves zero aggregate queries were executed against CompanyMasterRecords
        Assert.Equal(0, interceptor.MasterRecordsAggregateQueriesCount);
    }

    [Fact]
    public async Task RegistryPromotionCoordinator_ExclusivePromotionHoldsLock_WarmCacheProbesDistributedGateAndSetsSyncInProgress()
    {
        await using var db = CreateContext();
        var completedJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));

        // Warm cache with previous snapshot
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedData = new RegistryAggregateData
        {
            Metadata = new RegistrySnapshotMetadata
            {
                PublishedDate = completedJob.PublishedDate,
                CompletedUtc = completedJob.CompletedUtc,
                TotalRecords = 1000,
                IsSyncInProgress = false
            }
        };
        cache.Set(CompanyRegistryQueryService.CacheKeyForJob(completedJob.JobId), cachedData);

        // Remote node acquires exclusive lock on SQL Server (job in DB is NOT marked Promoting yet)
        var promoCoordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        await using var admissionScope = await promoCoordinator.AcquirePromotionAdmissionAsync(888888, TimeSpan.FromSeconds(5));

        // QueryService on another coordinator instance
        var queryCoordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        var queryService = CreateQueryService(db, cache, queryCoordinator);

        var vm = await queryService.GetDashboardAsync("overview", null);

        Assert.Equal(RegistrySnapshotState.VerifiedSnapshot, vm.State);
        Assert.NotNull(vm.Aggregates);
        Assert.True(vm.Aggregates.Metadata.IsSyncInProgress);
        Assert.Equal(CompanyMasterSyncJobStatus.Promoting, vm.Aggregates.Metadata.ActiveSyncStatus);
        Assert.Equal(completedJob.PublishedDate, vm.Aggregates.Metadata.PublishedDate);
    }

    [Fact]
    public async Task PromotionAdmission_TerminalFailure_PersistsTerminalStateBeforeReleasingLock()
    {
        await using var db = CreateContext();
        long token = 55;
        var job = new CompanyMasterSyncJob
        {
            FencingToken = token,
            Status = CompanyMasterSyncJobStatus.Staged,
            CreatedUtc = DateTime.UtcNow
        };
        db.CompanyMasterSyncJobs.Add(job);
        await db.SaveChangesAsync();
        _seededJobIds.Add(job.JobId);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = ConnectionString })
            .Build();
        var proxyPool = new ProxyPoolService(config, NullLogger<ProxyPoolService>.Instance);
        var lease = new DistributedAppLockLease(ConnectionString, NullLogger<DistributedAppLockLease>.Instance);

        bool auditedBeforeRelease = false;
        using var cancelCts = new CancellationTokenSource();
        var realCoordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
        var auditCoordinator = new TestAuditReleaseCoordinator(
            realCoordinator,
            onBeforeRelease: (jobId) =>
            {
                using var auditDb = CreateContext();
                var auditJob = auditDb.CompanyMasterSyncJobs.Find(jobId);
                Assert.NotNull(auditJob);
                // Assert that the job has ALREADY transitioned to a terminal status BEFORE releasing the lock
                Assert.True(auditJob.Status == CompanyMasterSyncJobStatus.Failed ||
                            auditJob.Status == CompanyMasterSyncJobStatus.PreemptedByTakeover);
                auditedBeforeRelease = true;
            },
            onAdmitted: () =>
            {
                // Cancel token immediately after exclusive admission lock is acquired
                cancelCts.Cancel();
            });

        var deltaService = new CompanyMasterDeltaService(db, config, proxyPool, lease, auditCoordinator, NullLogger<CompanyMasterDeltaService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deltaService.PromoteStagedDeltaAsync(job.JobId, token, batchSize: 100, cancellationToken: cancelCts.Token));

        Assert.True(auditedBeforeRelease);

        await using var freshDb = CreateContext();
        var terminalJob = await freshDb.CompanyMasterSyncJobs.FindAsync(job.JobId);
        Assert.NotNull(terminalJob);
        Assert.Equal(CompanyMasterSyncJobStatus.Failed, terminalJob.Status);
    }

    [Fact]
    public async Task RegistryExplorer_FcrnAndNameRouting_Exhaustive()
    {
        await using var db = CreateContext();
        await SeedRecordAsync(db, "F01326", "Foreign Corp Canonical Pvt Ltd", CompanyMasterRecordType.Foreign);
        await SeedRecordAsync(db, "F123456", "Foreign Corp Nonconforming Numeric", CompanyMasterRecordType.Foreign);
        var cin = $"U24246DL2003PTC{Random.Shared.Next(100000, 999999)}";
        await SeedRecordAsync(db, cin, "Federal Security Services Private Limited", CompanyMasterRecordType.Company);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateQueryService(db, cache);

        // 1. Canonical FCRN exact seek (no entity type specified)
        var res1 = await service.SearchExplorerAsync(new RegistryExplorerCriteria { Q = "F01326" });
        Assert.Null(res1.ValidationErrorMessage);
        Assert.Single(res1.Items);
        Assert.Equal("F01326", res1.Items[0].Identifier);

        // 2. Non-conforming numeric FCRN with explicit Foreign entity type
        var res2 = await service.SearchExplorerAsync(new RegistryExplorerCriteria
        {
            Q = "F123456",
            RecordType = CompanyMasterRecordType.Foreign
        });
        Assert.Null(res2.ValidationErrorMessage);
        Assert.Single(res2.Items);
        Assert.Equal("F123456", res2.Items[0].Identifier);

        // 3. Non-conforming numeric FCRN without Foreign entity type routes to name search and requires entity type
        var res3 = await service.SearchExplorerAsync(new RegistryExplorerCriteria { Q = "F123456" });
        Assert.NotNull(res3.ValidationErrorMessage);
        Assert.Contains("requires selecting an Entity Type", res3.ValidationErrorMessage);

        // 4. Name prefix "FEDERAL" routes to name search, NOT FCRN lookup
        var res4 = await service.SearchExplorerAsync(new RegistryExplorerCriteria
        {
            Q = "FEDERAL",
            RecordType = CompanyMasterRecordType.Company
        });
        Assert.Null(res4.ValidationErrorMessage);
        Assert.Single(res4.Items);
        Assert.Equal(cin, res4.Items[0].Identifier);
        Assert.Equal("Federal Security Services Private Limited", res4.Items[0].Name);
    }

    [Fact]
    public async Task RegistryDashboardController_InvalidCriteria_ReturnsHttp400WithFullIndexView()
    {
        await using var db = CreateContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var queryService = CreateQueryService(db, cache);

        var controller = new RegistryDashboardController(queryService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var invalidCriteria = new RegistryExplorerCriteria
        {
            Q = "TATA",
            RecordType = CompanyMasterRecordType.Company,
            State = "Delhi" // Secondary unindexed filter rejected in v1
        };

        // 1. Explorer action with invalid criteria
        var explorerResult = await controller.Explorer(invalidCriteria);
        Assert.Equal(400, controller.Response.StatusCode);
        var viewResult = Assert.IsType<ViewResult>(explorerResult);
        Assert.Equal("~/Views/Registry/Index.cshtml", viewResult.ViewName);
        var model = Assert.IsType<RegistryDashboardViewModel>(viewResult.Model);
        Assert.NotNull(model.Explorer?.ValidationErrorMessage);
        Assert.Contains("Secondary filtering", model.Explorer.ValidationErrorMessage);

        // 2. Index action with invalid criteria
        controller.Response.StatusCode = 200; // reset
        var indexResult = await controller.Index(tab: "explorer", explorer: invalidCriteria);
        Assert.Equal(400, controller.Response.StatusCode);
        var indexViewResult = Assert.IsType<ViewResult>(indexResult);
        Assert.Equal("~/Views/Registry/Index.cshtml", indexViewResult.ViewName);
        var indexModel = Assert.IsType<RegistryDashboardViewModel>(indexViewResult.Model);
        Assert.NotNull(indexModel.Explorer?.ValidationErrorMessage);
        Assert.Contains("Secondary filtering", indexModel.Explorer.ValidationErrorMessage);
    }

    private sealed class TestBarrierPromotionCoordinator : IRegistryPromotionCoordinator
    {
        private readonly IRegistryPromotionCoordinator _inner;
        public TaskCompletionSource<bool> RebuildGateAcquiredSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ProceedWithRebuildSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestBarrierPromotionCoordinator(IRegistryPromotionCoordinator inner)
        {
            _inner = inner;
        }

        public bool IsPromotionActive(out long? activeJobId) => _inner.IsPromotionActive(out activeJobId);

        public Task<IAsyncDisposable> AcquirePromotionAdmissionAsync(long syncRunId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => _inner.AcquirePromotionAdmissionAsync(syncRunId, timeout, cancellationToken);

        public async Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken cancellationToken = default)
        {
            var gate = await _inner.TryAcquireRebuildGateAsync(cancellationToken);
            if (gate != null)
            {
                RebuildGateAcquiredSignal.TrySetResult(true);
                await ProceedWithRebuildSignal.Task;
            }
            return gate;
        }

        public Task<bool> TryProbePromotionAdmissionAsync(CancellationToken cancellationToken = default)
            => _inner.TryProbePromotionAdmissionAsync(cancellationToken);
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int MasterRecordsAggregateQueriesCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CheckCommand(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CheckCommand(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CheckCommand(string commandText)
        {
            if (commandText.Contains("CompanyMasterRecords", StringComparison.OrdinalIgnoreCase) &&
                (commandText.Contains("COUNT", StringComparison.OrdinalIgnoreCase) ||
                 commandText.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)))
            {
                MasterRecordsAggregateQueriesCount++;
            }
        }
    }

    private sealed class TestAuditReleaseCoordinator : IRegistryPromotionCoordinator
    {
        private readonly IRegistryPromotionCoordinator _inner;
        private readonly Action<long> _onBeforeRelease;
        private readonly Action? _onAdmitted;

        public TestAuditReleaseCoordinator(IRegistryPromotionCoordinator inner, Action<long> onBeforeRelease, Action? onAdmitted = null)
        {
            _inner = inner;
            _onBeforeRelease = onBeforeRelease;
            _onAdmitted = onAdmitted;
        }

        public bool IsPromotionActive(out long? activeJobId) => _inner.IsPromotionActive(out activeJobId);

        public async Task<IAsyncDisposable> AcquirePromotionAdmissionAsync(long syncRunId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var innerScope = await _inner.AcquirePromotionAdmissionAsync(syncRunId, timeout, cancellationToken);
            _onAdmitted?.Invoke();
            return new AuditingScope(innerScope, () => _onBeforeRelease(syncRunId));
        }

        public Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken cancellationToken = default)
            => _inner.TryAcquireRebuildGateAsync(cancellationToken);

        public Task<bool> TryProbePromotionAdmissionAsync(CancellationToken cancellationToken = default)
            => _inner.TryProbePromotionAdmissionAsync(cancellationToken);

        private sealed class AuditingScope : IAsyncDisposable
        {
            private readonly IAsyncDisposable _inner;
            private readonly Action _audit;

            public AuditingScope(IAsyncDisposable inner, Action audit)
            {
                _inner = inner;
                _audit = audit;
            }

            public async ValueTask DisposeAsync()
            {
                _audit();
                await _inner.DisposeAsync();
            }
        }
    }

    private sealed class FailingRegistrySnapshotStore : IRegistrySnapshotStore
    {
        public Task<RegistryAggregateData?> GetSnapshotAsync(long jobId, CancellationToken ct = default)
            => Task.FromResult<RegistryAggregateData?>(null);

        public Task SaveSnapshotAsync(long jobId, RegistryAggregateData data, CancellationToken ct = default)
            => throw new IOException("Simulated shared store write failure (disk full or network storage unavailable)");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void ProductionStartup_FailsClosed_WhenRegistrySnapshotStoreRootIsAbsentOrInaccessible()
    {
        var prodEnv = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var devEnv = new TestHostEnvironment { EnvironmentName = Environments.Development };

        // 1. Production with null/empty Root fails closed
        var emptyOpts = Options.Create(new RegistrySnapshotStoreOptions { Root = "" });
        var servicesEmpty = new ServiceCollection()
            .AddSingleton(emptyOpts)
            .BuildServiceProvider();

        var exMissing = Assert.Throws<InvalidOperationException>(() =>
            FileRegistrySnapshotStore.ValidatePreflight(servicesEmpty, prodEnv));
        Assert.Contains("RegistrySnapshotStore:Root must be explicitly configured", exMissing.Message);

        // 2. Production with inaccessible Root fails closed (nested under a file, fails on all OSes)
        string blockingFile = Path.Combine(Path.GetTempPath(), $"mcaroc_blocking_file_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(blockingFile, "block");
        try
        {
            string invalidPath = Path.Combine(blockingFile, "forbidden_subfolder");
            var invalidOpts = Options.Create(new RegistrySnapshotStoreOptions { Root = invalidPath });
            var servicesInvalid = new ServiceCollection()
                .AddSingleton(invalidOpts)
                .BuildServiceProvider();

            var exInaccessible = Assert.Throws<InvalidOperationException>(() =>
                FileRegistrySnapshotStore.ValidatePreflight(servicesInvalid, prodEnv));
            Assert.Contains("preflight failed", exInaccessible.Message);
        }
        finally
        {
            try { File.Delete(blockingFile); } catch { }
        }

        // 3. Development environment with empty Root resolves default without error
        string devRoot = FileRegistrySnapshotStore.ResolveRootPath(new RegistrySnapshotStoreOptions(), devEnv);
        Assert.Contains("RegistrySnapshots", devRoot);

        var servicesDev = new ServiceCollection()
            .AddSingleton(Options.Create(new RegistrySnapshotStoreOptions()))
            .BuildServiceProvider();
        FileRegistrySnapshotStore.ValidatePreflight(servicesDev, devEnv);
    }

    [Fact]
    public async Task SimultaneousColdStart_MultipleInstances_CannotExecuteDuplicateRebuilds_AndReadsSharedCacheWithoutScanning()
    {
        await using var db = CreateContext();
        var completedJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));
        var id = $"U24246DL2003PTC{Random.Shared.Next(100000, 999999)}";
        await SeedRecordAsync(db, id, "Multi Node Test Pvt Ltd", CompanyMasterRecordType.Company);

        string sharedRoot = Path.Combine(Path.GetTempPath(), "mcaroc_shared_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sharedRoot);

        try
        {
            var storeOptions = Options.Create(new RegistrySnapshotStoreOptions { Root = sharedRoot });
            var sharedStoreA = new FileRegistrySnapshotStore(storeOptions, logger: NullLogger<FileRegistrySnapshotStore>.Instance);
            var sharedStoreB = new FileRegistrySnapshotStore(storeOptions, logger: NullLogger<FileRegistrySnapshotStore>.Instance);

            // Node A setup with counting interceptor and barrier coordinator
            var interceptorA = new CommandCountingInterceptor();
            var optionsA = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(ConnectionString)
                .AddInterceptors(interceptorA)
                .Options;
            await using var dbA = new AppDbContext(optionsA);
            var realCoordinatorA = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
            var barrierCoordinatorA = new TestBarrierPromotionCoordinator(realCoordinatorA);
            var cacheA = new MemoryCache(new MemoryCacheOptions());
            var queryServiceA = new CompanyRegistryQueryService(dbA, cacheA, barrierCoordinatorA, sharedStoreA, NullLogger<CompanyRegistryQueryService>.Instance, new SemaphoreSlim(1, 1));

            // Node B setup with its own connection, separate interceptor, separate cache, separate coordinator
            var interceptorB = new CommandCountingInterceptor();
            var optionsB = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(ConnectionString)
                .AddInterceptors(interceptorB)
                .Options;
            await using var dbB = new AppDbContext(optionsB);
            var coordinatorB = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
            var cacheB = new MemoryCache(new MemoryCacheOptions());
            var queryServiceB = new CompanyRegistryQueryService(dbB, cacheB, coordinatorB, sharedStoreB, NullLogger<CompanyRegistryQueryService>.Instance, new SemaphoreSlim(1, 1));

            // PHASE 1: Node A begins cold rebuild, acquires rebuild gate and hits barrier
            var taskA = Task.Run(() => queryServiceA.GetDashboardAsync("overview", null));
            await barrierCoordinatorA.RebuildGateAcquiredSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // While Node A holds rebuild gate, Node B requests dashboard with cold cache
            var vmA_concurrentB = await queryServiceB.GetDashboardAsync("overview", null);

            // Node B cannot acquire gate -> returns SyncColdUnavailable with 0 queries
            Assert.Equal(RegistrySnapshotState.SyncColdUnavailable, vmA_concurrentB.State);
            Assert.Equal(0, interceptorB.MasterRecordsAggregateQueriesCount);

            // Let Node A proceed with rebuild and persistence to shared store
            barrierCoordinatorA.ProceedWithRebuildSignal.TrySetResult(true);
            var vmA = await taskA.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(RegistrySnapshotState.VerifiedSnapshot, vmA.State);
            Assert.NotNull(vmA.Aggregates);
            Assert.True(interceptorA.MasterRecordsAggregateQueriesCount > 0);

            // Verify file was written to sharedRoot
            string snapshotFile = Path.Combine(sharedRoot, $"snapshot_{completedJob.JobId}.json");
            Assert.True(File.Exists(snapshotFile));

            // PHASE 2: Node A has completed and released both locks.
            // Node B has its independent cache (cacheB is still cold).
            // Node B requests dashboard: must read from shared store (L2) and execute 0 aggregate queries.
            var vmB = await queryServiceB.GetDashboardAsync("overview", null);

            Assert.Equal(RegistrySnapshotState.VerifiedSnapshot, vmB.State);
            Assert.NotNull(vmB.Aggregates);
            Assert.Equal(completedJob.PublishedDate, vmB.Aggregates.Metadata.PublishedDate);
            Assert.Equal(vmA.Aggregates.Metadata.TotalRecords, vmB.Aggregates.Metadata.TotalRecords);

            // Interceptor B must still have recorded 0 aggregate queries across both phases!
            Assert.Equal(0, interceptorB.MasterRecordsAggregateQueriesCount);

            // Also check that cacheB is now warm (L1 populated)
            string cacheKey = CompanyRegistryQueryService.CacheKeyForJob(completedJob.JobId);
            Assert.True(cacheB.TryGetValue(cacheKey, out RegistryAggregateData? warmB));
            Assert.NotNull(warmB);
        }
        finally
        {
            if (Directory.Exists(sharedRoot))
            {
                try { Directory.Delete(sharedRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task L2Snapshot_CorruptedChecksum_UnsupportedVersion_OrInvalidJson_Rejected()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "mcaroc_corrupt_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            var options = Options.Create(new RegistrySnapshotStoreOptions { Root = testRoot });
            var store = new FileRegistrySnapshotStore(options);

            long jobId = 777;
            var data = new RegistryAggregateData
            {
                Metadata = new RegistrySnapshotMetadata
                {
                    PublishedDate = new DateOnly(2026, 9, 1),
                    CompletedUtc = DateTime.UtcNow,
                    TotalRecords = 500,
                    Source = "Test"
                }
            };

            // 1. Save valid snapshot
            await store.SaveSnapshotAsync(jobId, data);
            string snapshotFile = Path.Combine(testRoot, $"snapshot_{jobId}.json");
            Assert.True(File.Exists(snapshotFile));

            var loadedValid = await store.GetSnapshotAsync(jobId);
            Assert.NotNull(loadedValid);

            // 2. Corrupt checksum (tamper with RawDtoJson while keeping Sha256Hash)
            string originalJson = await File.ReadAllTextAsync(snapshotFile);
            var envelope = System.Text.Json.JsonSerializer.Deserialize<RegistrySnapshotEnvelope>(originalJson);
            Assert.NotNull(envelope);

            var tamperedEnvelope = new RegistrySnapshotEnvelope
            {
                PayloadVersion = envelope.PayloadVersion,
                JobId = envelope.JobId,
                PublishedDate = envelope.PublishedDate,
                CompletedUtc = envelope.CompletedUtc,
                Source = envelope.Source,
                CreatedUtc = envelope.CreatedUtc,
                Sha256Hash = envelope.Sha256Hash, // original hash
                RawDtoJson = "{\"Metadata\":{\"TotalRecords\":999999}}" // tampered payload
            };
            await File.WriteAllTextAsync(snapshotFile, System.Text.Json.JsonSerializer.Serialize(tamperedEnvelope));

            var loadedTampered = await store.GetSnapshotAsync(jobId);
            Assert.Null(loadedTampered); // Must reject tampered checksum

            // 3. Unsupported payload version
            tamperedEnvelope.PayloadVersion = 999;
            tamperedEnvelope.RawDtoJson = envelope.RawDtoJson;
            tamperedEnvelope.Sha256Hash = envelope.Sha256Hash;
            await File.WriteAllTextAsync(snapshotFile, System.Text.Json.JsonSerializer.Serialize(tamperedEnvelope));

            var loadedWrongVersion = await store.GetSnapshotAsync(jobId);
            Assert.Null(loadedWrongVersion); // Must reject unsupported version

            // 4. Invalid JSON
            await File.WriteAllTextAsync(snapshotFile, "{ definitely not valid json ::::");

            var loadedInvalidJson = await store.GetSnapshotAsync(jobId);
            Assert.Null(loadedInvalidJson); // Must reject invalid JSON
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                try { Directory.Delete(testRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SharedStoreWriteFailure_ReturnsStorageErrorState_LeavesL1Empty()
    {
        await using var db = CreateContext();
        var completedJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));
        var id = $"U24246DL2003PTC{Random.Shared.Next(100000, 999999)}";
        await SeedRecordAsync(db, id, "Write Failure Test Pvt Ltd", CompanyMasterRecordType.Company);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var coordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);

        // Store that fails on SaveSnapshotAsync
        var failingStore = new FailingRegistrySnapshotStore();
        var queryService = new CompanyRegistryQueryService(db, cache, coordinator, failingStore, NullLogger<CompanyRegistryQueryService>.Instance);

        var vm = await queryService.GetDashboardAsync("overview", null);

        Assert.Equal(RegistrySnapshotState.SyncColdUnavailable, vm.State);
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("Verified Aggregates Storage Error", vm.StatusMessage);

        // Assert that L1 cache was NOT populated
        string cacheKey = CompanyRegistryQueryService.CacheKeyForJob(completedJob.JobId);
        Assert.False(cache.TryGetValue(cacheKey, out _));
    }

    [Fact]
    public async Task Retention_RetainsConfiguredNewestJobIds_SafelyIgnoresTemporaryFiles()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "mcaroc_retention_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            var options = Options.Create(new RegistrySnapshotStoreOptions
            {
                Root = testRoot,
                RetentionCount = 3
            });
            var store = new FileRegistrySnapshotStore(options);

            var dummyData = new RegistryAggregateData
            {
                Metadata = new RegistrySnapshotMetadata
                {
                    PublishedDate = new DateOnly(2026, 9, 1),
                    CompletedUtc = DateTime.UtcNow,
                    TotalRecords = 100,
                    Source = "Test"
                }
            };

            // Create temporary in-progress file and unrelated file
            string tmpFile = Path.Combine(testRoot, "snapshot_999.tmp.someguid");
            await File.WriteAllTextAsync(tmpFile, "temporary in-flight file");

            string unrelatedFile = Path.Combine(testRoot, "notes.txt");
            await File.WriteAllTextAsync(unrelatedFile, "keep me");

            // Save snapshots for 5 jobs: 10, 20, 30, 40, 50
            for (long j = 10; j <= 50; j += 10)
            {
                await store.SaveSnapshotAsync(j, dummyData);
            }

            // Snapshots retained must be the newest 3: 30, 40, 50
            Assert.False(File.Exists(Path.Combine(testRoot, "snapshot_10.json")));
            Assert.False(File.Exists(Path.Combine(testRoot, "snapshot_20.json")));
            Assert.True(File.Exists(Path.Combine(testRoot, "snapshot_30.json")));
            Assert.True(File.Exists(Path.Combine(testRoot, "snapshot_40.json")));
            Assert.True(File.Exists(Path.Combine(testRoot, "snapshot_50.json")));

            // Temporary and unrelated files must safely be preserved
            Assert.True(File.Exists(tmpFile));
            Assert.True(File.Exists(unrelatedFile));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                try { Directory.Delete(testRoot, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task L2Snapshot_PreservesActivePromotionCheck_AndStampsIsSyncInProgress()
    {
        await using var db = CreateContext();
        var completedJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Completed, new DateOnly(2026, 9, 1));
        var promotingJob = await SeedSyncJobAsync(db, CompanyMasterSyncJobStatus.Promoting, new DateOnly(2026, 9, 20));

        string testRoot = Path.Combine(Path.GetTempPath(), "mcaroc_l2_promo_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            var options = Options.Create(new RegistrySnapshotStoreOptions { Root = testRoot });
            var store = new FileRegistrySnapshotStore(options);

            var dummyData = new RegistryAggregateData
            {
                Metadata = new RegistrySnapshotMetadata
                {
                    PublishedDate = completedJob.PublishedDate,
                    CompletedUtc = completedJob.CompletedUtc,
                    TotalRecords = 12345,
                    Source = "Test",
                    IsSyncInProgress = false
                }
            };
            // Populate L2 only (L1 cache starts cold)
            await store.SaveSnapshotAsync(completedJob.JobId, dummyData);

            var cache = new MemoryCache(new MemoryCacheOptions());
            var coordinator = new RegistryPromotionCoordinator(ConnectionString, NullLogger<RegistryPromotionCoordinator>.Instance);
            var queryService = new CompanyRegistryQueryService(db, cache, coordinator, store, NullLogger<CompanyRegistryQueryService>.Instance);

            var vm = await queryService.GetDashboardAsync("overview", null);

            Assert.Equal(RegistrySnapshotState.VerifiedSnapshot, vm.State);
            Assert.NotNull(vm.Aggregates);
            Assert.True(vm.Aggregates.Metadata.IsSyncInProgress);
            Assert.Equal(CompanyMasterSyncJobStatus.Promoting, vm.Aggregates.Metadata.ActiveSyncStatus);
            Assert.Equal(12345, vm.Aggregates.Metadata.TotalRecords);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                try { Directory.Delete(testRoot, recursive: true); } catch { }
            }
        }
    }
}
