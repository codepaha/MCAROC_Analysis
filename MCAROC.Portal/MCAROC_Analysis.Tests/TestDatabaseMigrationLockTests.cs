using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public sealed class TestDatabaseMigrationLockTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .Options);

    [Fact]
    public async Task MigrateAsync_serializes_concurrent_context_initialization()
    {
        await using var first = CreateContext();
        await using var second = CreateContext();

        await Task.WhenAll(
            TestDatabase.MigrateAsync(first),
            TestDatabase.MigrateAsync(second));
    }
}
