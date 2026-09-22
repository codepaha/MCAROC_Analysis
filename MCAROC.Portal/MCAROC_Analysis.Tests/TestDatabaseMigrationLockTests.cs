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

    [Fact]
    public void MigrateAsync_from_different_DbContext_instances_returns_the_same_task()
    {
        // The real fix for a failure mode that looked like a timing race but wasn't (three consecutive
        // hosted CI runs failed identically with "Database already exists" from inside EF's own
        // MigrateAsync, and a bounded retry did not help — see TestDatabase's own doc comment): every
        // caller in the process must share exactly one underlying create+migrate attempt, never each
        // trigger their own. Deterministic, no timing dependency: two calls, from two different
        // AppDbContext instances (simulating two different test classes' fixtures), must return
        // reference-identical tasks.
        using var first = CreateContext();
        using var second = CreateContext();

        var firstTask = TestDatabase.MigrateAsync(first);
        var secondTask = TestDatabase.MigrateAsync(second);

        Assert.Same(firstTask, secondTask);
    }
}
