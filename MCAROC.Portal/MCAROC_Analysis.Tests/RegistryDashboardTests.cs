using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Services.Registry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

    [Fact]
    public async Task RegistryExplorer_ExactIdentifierSeek_ReturnsExpectedRow()
    {
        await using var db = CreateContext();
        var id = $"U24246DL{Guid.NewGuid():N}"[..21].ToUpperInvariant();
        await SeedRecordAsync(db, id, "Test Exact Lookup Pvt Ltd", CompanyMasterRecordType.Company);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new CompanyRegistryQueryService(db, cache, NullLogger<CompanyRegistryQueryService>.Instance);

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
        var service = new CompanyRegistryQueryService(db, cache, NullLogger<CompanyRegistryQueryService>.Instance);

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
        var service = new CompanyRegistryQueryService(db, cache, NullLogger<CompanyRegistryQueryService>.Instance);

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
        var service = new CompanyRegistryQueryService(db, cache, NullLogger<CompanyRegistryQueryService>.Instance);

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

        var service = new CompanyRegistryQueryService(db, cache, NullLogger<CompanyRegistryQueryService>.Instance);
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
        var service = new CompanyRegistryQueryService(db, cache, NullLogger<CompanyRegistryQueryService>.Instance);

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
        var service = new CompanyRegistryQueryService(db, cache, NullLogger<CompanyRegistryQueryService>.Instance);

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
}
