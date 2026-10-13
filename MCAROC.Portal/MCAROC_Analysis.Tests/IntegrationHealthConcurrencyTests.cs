using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Health is reported from inside other work: during a filings download several reference-tool calls run in parallel, each reporting
/// health, while the job's own flow keeps using the request-scoped context. In the TruBoard test (2026-10-13) that sharing failed the whole
/// fetch ("a second operation was started on this context").</summary>
public class IntegrationHealthConcurrencyTests : IAsyncLifetime
{
    private static readonly DbContextOptions<AppDbContext> Options =
        new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;

    public async Task InitializeAsync()
    {
        await using var db = new AppDbContext(Options);
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Parallel_health_reports_while_the_caller_uses_its_own_context_never_collide()
    {
        await using var scoped = new AppDbContext(Options);
        await using var health = new IntegrationHealthService(scoped, Options);

        var reports = Enumerable.Range(0, 12).Select(i => i % 3 == 0
            ? health.ReportFailureAsync(IntegrationName.ReferenceTool, DateTime.UtcNow.AddMinutes(-1), "transient", false, 1000, TimeSpan.FromMinutes(5), default)
            : health.ReportSuccessAsync(IntegrationName.ReferenceTool, DateTime.UtcNow.AddMinutes(-1), default)).ToList();
        // ...while the job's own flow keeps querying the scoped context, as the progress flush does.
        var callerWork = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++) await scoped.Requests.AsNoTracking().CountAsync();
        });

        await Task.WhenAll(reports.Append(callerWork));                                              // no "second operation" / MARS failure
        Assert.NotNull(await health.GetAsync(IntegrationName.ReferenceTool, default));
        Assert.False(await health.IsOpenAsync(IntegrationName.ReferenceTool, default));               // 4 failures against a threshold of 1000
    }
}
