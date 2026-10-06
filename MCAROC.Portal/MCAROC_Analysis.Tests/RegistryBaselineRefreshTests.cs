using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Services.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>The imported registry baseline is a full aggregate over millions of rows. It must be rebuilt when the rows change, not
/// every few minutes: repeating it on an unchanged table starved a small SQL Server and slowed every other page.</summary>
public sealed class RegistryBaselineRefreshTests
{
    private sealed class CountingState : IRegistrySnapshotStore, IRegistryPromotionCoordinator
    {
        public RegistryAggregateData? Stored { get; set; }
        public int Saves { get; private set; }
        public Task<RegistryAggregateData?> GetSnapshotAsync(long id, CancellationToken ct = default) => Task.FromResult(Stored);
        public Task SaveSnapshotAsync(long id, RegistryAggregateData data, CancellationToken ct = default)
        { Stored = data; Saves++; return Task.CompletedTask; }
        public bool IsPromotionActive(out long? id) { id = null; return false; }
        public Task<IAsyncDisposable> AcquirePromotionAdmissionAsync(long id, TimeSpan timeout, CancellationToken ct = default)
            => throw new InvalidOperationException("Unexpected promotion admission.");
        public Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new NoopGate());
        public Task<bool> TryProbePromotionAdmissionAsync(CancellationToken ct = default) => Task.FromResult(true);
        private sealed class NoopGate : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private static CompanyMasterRecord Row(string cin, string status = "Active") => new()
    {
        Identifier = cin, RecordType = CompanyMasterRecordType.Company, Name = "Company " + cin, Status = status,
        State = "Maharashtra", Roc = "RoC-Mumbai", Country = "India", RegistrationDate = new DateOnly(2020, 1, 1)
    };

    /// <summary>A service instance as a worker tick would see it: a new scope, so nothing is cached in memory.</summary>
    private static async Task RefreshAsync(string connectionString, CountingState state)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options);
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["RegistryAnalytics:AllowImportedBaseline"] = "true" }).Build();
        var service = new CompanyRegistryQueryService(db, cache, state, state, NullLogger<CompanyRegistryQueryService>.Instance,
            new SemaphoreSlim(1, 1), configuration);
        await service.RefreshImportedBaselineAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_baseline_is_rebuilt_when_the_rows_change_or_it_is_a_day_old_and_not_on_a_short_clock()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("RegistryBaseline");
        var state = new CountingState();
        await using (var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options))
        {
            seed.CompanyMasterRecords.AddRange(Row("U00001MH2020PTC000001"), Row("U00002MH2020PTC000002"), Row("U00003MH2020PTC000003", "Strike Off"));
            await seed.SaveChangesAsync();
        }

        // First refresh: nothing stored, so it builds, and records the row count it was built from.
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(1, state.Saves);
        Assert.Equal(3, state.Stored!.Metadata.SourceRowCount);

        // Two hours later, same rows: not rebuilt (the old fifteen-minute rule rebuilt here, and again every fifteen minutes).
        state.Stored.Metadata.CalculatedUtc = DateTime.UtcNow.AddHours(-2);
        await RefreshAsync(isolated.ConnectionString, state);
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(1, state.Saves);

        // A row is added: the next refresh rebuilds and records the new count.
        await using (var add = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options))
        {
            add.CompanyMasterRecords.Add(Row("U00004MH2020PTC000004"));
            await add.SaveChangesAsync();
        }
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(2, state.Saves);
        Assert.Equal(4, state.Stored!.Metadata.SourceRowCount);

        // A row is removed: also a change.
        await using (var remove = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options))
        {
            remove.CompanyMasterRecords.Remove(await remove.CompanyMasterRecords.FirstAsync(r => r.Identifier == "U00004MH2020PTC000004"));
            await remove.SaveChangesAsync();
        }
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(3, state.Saves);
        Assert.Equal(3, state.Stored!.Metadata.SourceRowCount);

        // Unchanged rows but a day old (an in-place edit cannot change the count): rebuilt, so figures are never older than a day.
        state.Stored.Metadata.CalculatedUtc = DateTime.UtcNow - CompanyRegistryQueryService.MaxBaselineAge - TimeSpan.FromMinutes(1);
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(4, state.Saves);

        // A baseline saved before the row count was recorded is judged by age alone: not rebuilt while it is young...
        state.Stored!.Metadata.SourceRowCount = null;
        state.Stored.Metadata.CalculatedUtc = DateTime.UtcNow.AddHours(-1);
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(4, state.Saves);
        // ...and rebuilt, with the count recorded from then on, once it is a day old.
        state.Stored.Metadata.CalculatedUtc = DateTime.UtcNow.AddHours(-25);
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(5, state.Saves);
        Assert.Equal(3, state.Stored!.Metadata.SourceRowCount);
    }

    [Fact]
    public async Task A_baseline_is_not_served_once_the_registry_has_been_emptied()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("RegistryBaselineEmptied");
        var state = new CountingState();
        await using (var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options))
        {
            seed.CompanyMasterRecords.AddRange(Row("U00001MH2020PTC000001"), Row("U00002MH2020PTC000002"));
            await seed.SaveChangesAsync();
        }
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(1, state.Saves);
        Assert.NotNull(await ReadAsDashboardAsync(isolated.ConnectionString, state)); // the dashboard serves the populated baseline

        // every row is removed: the next refresh must replace the stored baseline, not leave it standing
        await using (var wipe = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options))
            await wipe.CompanyMasterRecords.ExecuteDeleteAsync();
        await RefreshAsync(isolated.ConnectionString, state);

        Assert.Equal(2, state.Saves);
        Assert.Empty(state.Stored!.EntityAnalytics);
        Assert.Null(await ReadAsDashboardAsync(isolated.ConnectionString, state)); // no figures for records that no longer exist

        // already cleared: refreshing an empty registry again writes nothing more
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(2, state.Saves);

        // and rows coming back are aggregated again
        await using (var back = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options))
        {
            back.CompanyMasterRecords.Add(Row("U00009MH2021PTC000009"));
            await back.SaveChangesAsync();
        }
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(3, state.Saves);
        Assert.NotNull(await ReadAsDashboardAsync(isolated.ConnectionString, state));
    }

    /// <summary>The dashboard's read path (the one a page request takes): the stored baseline if it is usable, never a rebuild.</summary>
    private static async Task<RegistryAggregateData?> ReadAsDashboardAsync(string connectionString, CountingState state)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options);
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["RegistryAnalytics:AllowImportedBaseline"] = "true" }).Build();
        var service = new CompanyRegistryQueryService(db, cache, state, state, NullLogger<CompanyRegistryQueryService>.Instance,
            new SemaphoreSlim(1, 1), configuration);
        var read = (Task<RegistryAggregateData?>)typeof(CompanyRegistryQueryService)
            .GetMethod("GetImportedBaselineAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, [CancellationToken.None, false])!;
        return await read;
    }

    [Fact]
    public async Task A_refresh_with_no_registry_rows_builds_nothing()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("RegistryBaselineEmpty");
        var state = new CountingState();
        await RefreshAsync(isolated.ConnectionString, state);
        Assert.Equal(0, state.Saves);
    }
}
