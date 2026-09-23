using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystProvisioningServiceTests
{
    private static AppDbContext CreateContext(string connectionString) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(connectionString).Options);

    [Fact]
    public async Task ConcurrentProvisioning_CreatesOnlyOneInitialAnalyst()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("AnalystProvisioning");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = AttemptAsync(isolated.ConnectionString, start.Task, "FIRST-ANALYST");
        var second = AttemptAsync(isolated.ConnectionString, start.Task, "SECOND-ANALYST");
        start.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result));
        await using var verify = CreateContext(isolated.ConnectionString);
        Assert.Equal(1, await verify.Analysts.CountAsync());
    }

    private static async Task<bool> AttemptAsync(string connectionString, Task start, string loginName)
    {
        await start;
        await using var db = CreateContext(connectionString);
        var service = new AnalystProvisioningService(db, new AnalystPasswordHasher(), new NoopAuditLogService());
        try
        {
            await service.ProvisionInitialAsync(loginName, "Synthetic analyst", "Synthetic-password-1234");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class NoopAuditLogService : IAuditLogService
    {
        public Task TryLogAsync<T>(AuditEvent<T> evt, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public Task TryLogAsync(AuditEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    }
}
