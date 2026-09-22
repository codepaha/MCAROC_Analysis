using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Pipeline;

namespace MCAROC_Analysis.Tests;

public static class TestHelpers
{
    public static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    public static SheetData Sheet(string name, params IReadOnlyList<object?>[] rows) => new(name, rows);
}

/// <summary>No-op <see cref="IIntegrationHealthService"/> for tests that construct a <see
/// cref="MCAROC_Analysis.Services.AutoFetch.ReferenceToolClient"/> but don't care about breaker/health
/// tracking — the real implementation needs a live SQL Server <c>AppDbContext</c>, which most client-level
/// tests don't set up. <see cref="IntegrationHealthServiceTests"/> covers the real implementation's
/// concurrency contract against a real database.</summary>
public sealed class NoOpIntegrationHealthService : IIntegrationHealthService
{
    public Task ReportFailureAsync(IntegrationName name, DateTime callStartUtc, string? error, bool authRejected, int threshold, TimeSpan probeLease, CancellationToken ct) => Task.CompletedTask;
    public Task ReportSuccessAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> TryClaimHalfOpenProbeAsync(IntegrationName name, TimeSpan probeLease, CancellationToken ct) => Task.FromResult(false);
    public Task TryCloseAfterProbeAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct) => Task.CompletedTask;
    public Task<IntegrationHealth?> GetAsync(IntegrationName name, CancellationToken ct) => Task.FromResult<IntegrationHealth?>(null);
    public Task<bool> IsOpenAsync(IntegrationName name, CancellationToken ct) => Task.FromResult(false);
}
