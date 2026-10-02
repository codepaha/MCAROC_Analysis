using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#349: <see cref="FullTextSearchStatus"/> reports what the database server actually has. Checked against the
/// server's own answer rather than a fixed expectation, so it holds on the Windows runner (no Full-Text) and the Linux
/// one (Full-Text installed) alike.</summary>
public sealed class FullTextSearchStatusTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetAsync_reports_the_servers_full_text_install_and_chunk_indexes_and_caches_the_answer()
    {
        await using var db = CreateContext();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var status = new FullTextSearchStatus(db, cache);

        var state = await status.GetAsync(CancellationToken.None);

        var installed = await db.Database.SqlQueryRaw<int>("SELECT CAST(ISNULL(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) AS int) AS Value").SingleAsync() == 1;
        var indexed = await db.Database.SqlQueryRaw<string>("SELECT OBJECT_NAME(object_id) AS Value FROM sys.fulltext_indexes").ToListAsync();
        Assert.Equal(installed, state.Installed);
        Assert.Equal(indexed.Contains("DocumentChunks"), state.DocumentChunksIndexed);
        Assert.Equal(indexed.Contains("LitigationOrderChunks"), state.LitigationChunksIndexed);
        Assert.Equal(state.Installed && state.DocumentChunksIndexed && state.LitigationChunksIndexed, state.Available);

        Assert.Same(state, await status.GetAsync(CancellationToken.None));
    }

    /// <summary>PR #352 review: each chunk table falls back on its own, so one missing index must not be reported as
    /// keyword search being off everywhere.</summary>
    [Theory]
    [InlineData(true, true, true, true, true, null)]
    [InlineData(true, true, false, true, false, "covers MCA filings only")]
    [InlineData(true, false, true, false, true, "covers court orders only")]
    [InlineData(true, false, false, false, false, "unavailable on this server")]
    [InlineData(false, true, true, false, false, "unavailable on this server")]
    public void ChatNote_and_per_table_coverage_reflect_partial_availability(bool installed, bool filingsIndexed, bool ordersIndexed,
        bool filings, bool orders, string? note)
    {
        var state = new FullTextSearchState(installed, filingsIndexed, ordersIndexed);

        Assert.Equal((filings, orders), (state.FilingsKeywordSearch, state.OrdersKeywordSearch));
        if (note is null) Assert.Null(state.ChatNote);
        else Assert.Contains(note, state.ChatNote);
    }
}
