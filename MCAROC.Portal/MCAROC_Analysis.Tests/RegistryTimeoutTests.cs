using System.Data.Common;
using System.Reflection;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Services.Registry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

public sealed class RegistryTimeoutTests
{
    private static SqlException SqlFailure(int number)
    {
        // SqlClient exposes no public constructors for server errors; manufacture a provider error
        // to test the precise timeout classification without connecting to a real database.
        var constructor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).First();
        var arguments = constructor.GetParameters().Select((parameter, index) => index == 0
            ? (object)number
            : parameter.ParameterType == typeof(string) ? "Synthetic registry SQL failure"
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        var error = (SqlError)constructor.Invoke(arguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: [typeof(SqlErrorCollection), typeof(string)], modifiers: null)!.Invoke(null, [errors, "test-version"])!;
    }

    private sealed class FailedConnection(Exception failure) : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw failure;
    }

    private static AppDbContext Context(Exception failure) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=not-used;Database=SyntheticRegistryTimeout;Integrated Security=True;Encrypt=False")
        .AddInterceptors(new FailedConnection(failure)).Options);

    private static CompanyRegistryQueryService Service(AppDbContext db, IMemoryCache cache) => new(db, cache,
        new UnusedRegistryState(), new UnusedRegistryState(), NullLogger<CompanyRegistryQueryService>.Instance);

    [Fact]
    public async Task TimeoutWithholdsMetricsAndSearchResults()
    {
        await using var db = Context(SqlFailure(-2));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var model = await Service(db, cache).GetDashboardAsync("explorer", new RegistryExplorerCriteria { Q = "ABC" });
        Assert.Equal(RegistrySnapshotState.DatabaseUnavailable, model.State);
        Assert.Equal("explorer", model.ActiveTab);
        Assert.Null(model.Aggregates);
        Assert.Empty(model.Explorer.Items);
        Assert.False(model.Explorer.SearchExecuted);
        Assert.DoesNotContain("Synthetic", model.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DashboardAndExplorerReturnRetryable503ForTimeout(bool explorer)
    {
        await using var db = Context(SqlFailure(-2));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new RegistryDashboardController(Service(db, cache))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = explorer ? await controller.Explorer(new RegistryExplorerCriteria { Q = "ABC" }) : await controller.Index();
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("~/Views/Registry/Index.cshtml", view.ViewName);
        Assert.Equal(503, controller.Response.StatusCode);
        Assert.Equal("30", controller.Response.Headers["Retry-After"]);
        Assert.Equal(RegistrySnapshotState.DatabaseUnavailable, Assert.IsType<RegistryDashboardViewModel>(view.Model).State);
    }

    [Fact]
    public async Task OtherSqlErrorsAreNotMisreportedAsTemporaryTimeouts()
    {
        var failure = SqlFailure(208); // Invalid object: a schema/configuration error needs separate diagnosis.
        await using var db = Context(failure);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var thrown = await Assert.ThrowsAsync<SqlException>(() => Service(db, cache).GetDashboardAsync());
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task RequestCancellationIsNotConvertedIntoUnavailablePage()
    {
        var failure = new OperationCanceledException();
        await using var db = Context(failure);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db, cache).GetDashboardAsync());
        Assert.Same(failure, thrown);
    }

    private sealed class UnusedRegistryState : IRegistryPromotionCoordinator, IRegistrySnapshotStore
    {
        private static Exception Unexpected() => new InvalidOperationException("No promotion or snapshot work should follow a failed initial query.");
        public bool IsPromotionActive(out long? activeJobId) => throw Unexpected();
        public Task<IAsyncDisposable> AcquirePromotionAdmissionAsync(long syncRunId, TimeSpan timeout, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<bool> TryProbePromotionAdmissionAsync(CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<RegistryAggregateData?> GetSnapshotAsync(long jobId, CancellationToken ct = default) => throw Unexpected();
        public Task SaveSnapshotAsync(long jobId, RegistryAggregateData data, CancellationToken ct = default) => throw Unexpected();
    }
}
