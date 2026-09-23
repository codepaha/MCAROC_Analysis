using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Relational tests (real .\SQLEXPRESS test DB) for the <c>IntegrationHealth</c> circuit-breaker
/// concurrency contract described in docs/pipeline-automation-plan.md §5.4: single-statement atomic writes,
/// auth-rejected opens immediately, ordinary success cannot close an open breaker (only a probe can), and
/// stale-result fencing via <c>LastTransitionUtc</c>. Uses a single fixed <see cref="IntegrationName"/> (an
/// arbitrary pick — nothing else in the test suite writes real rows against this table) and resets that
/// row before every test so methods stay independent despite the enum only having 3 possible names.</summary>
public sealed class IntegrationHealthServiceTests : IAsyncLifetime
{
    private const IntegrationName Name = IntegrationName.Vertex;
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM IntegrationHealths WHERE Name = {Name.ToString()}");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Failures_below_threshold_degrade_but_do_not_open()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);

        // Each failing call captures its own call-start time (as real callers do) — reusing one fixed
        // timestamp across two sequential reports would make the second look stale against the first's
        // own LastTransitionUtc update, which is a test-authoring mistake, not a product bug.
        await svc.ReportFailureAsync(Name, DateTime.UtcNow, "boom", authRejected: false, threshold: 3, probeLease: TimeSpan.FromMinutes(5), CancellationToken.None);
        await svc.ReportFailureAsync(Name, DateTime.UtcNow, "boom again", authRejected: false, threshold: 3, probeLease: TimeSpan.FromMinutes(5), CancellationToken.None);

        var row = await svc.GetAsync(Name, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(IntegrationHealthState.Degraded, row!.State);
        Assert.Equal(2, row.ConsecutiveFailures);
        Assert.Null(row.OpenedUtc);
        Assert.False(await svc.IsOpenAsync(Name, CancellationToken.None));
    }

    [Fact]
    public async Task Failure_reaching_threshold_opens_the_breaker_and_sets_a_probe_time()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);
        var probeLease = TimeSpan.FromMinutes(5);

        await svc.ReportFailureAsync(Name, DateTime.UtcNow, "1", authRejected: false, threshold: 2, probeLease, CancellationToken.None);
        await svc.ReportFailureAsync(Name, DateTime.UtcNow, "2", authRejected: false, threshold: 2, probeLease, CancellationToken.None);

        var row = await svc.GetAsync(Name, CancellationToken.None);
        Assert.Equal(IntegrationHealthState.Open, row!.State);
        Assert.NotNull(row.OpenedUtc);
        Assert.NotNull(row.NextProbeUtc);
        Assert.True(await svc.IsOpenAsync(Name, CancellationToken.None));
    }

    [Fact]
    public async Task AuthRejected_opens_the_breaker_immediately_on_the_first_failure()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);
        var callStart = DateTime.UtcNow.AddSeconds(-1);

        // threshold 100 — would never be reached by count alone; AuthRejected must bypass it entirely.
        await svc.ReportFailureAsync(Name, callStart, "bad credentials", authRejected: true, threshold: 100, TimeSpan.FromMinutes(5), CancellationToken.None);

        var row = await svc.GetAsync(Name, CancellationToken.None);
        Assert.Equal(IntegrationHealthState.Open, row!.State);
        Assert.Equal(1, row.ConsecutiveFailures);
    }

    [Fact]
    public async Task Success_resets_consecutive_failures_and_returns_degraded_to_healthy()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);
        var callStart = DateTime.UtcNow.AddSeconds(-1);
        await svc.ReportFailureAsync(Name, callStart, "boom", authRejected: false, threshold: 5, TimeSpan.FromMinutes(5), CancellationToken.None);

        await svc.ReportSuccessAsync(Name, DateTime.UtcNow, CancellationToken.None);

        var row = await svc.GetAsync(Name, CancellationToken.None);
        Assert.Equal(IntegrationHealthState.Healthy, row!.State);
        Assert.Equal(0, row.ConsecutiveFailures);
        Assert.NotNull(row.LastSuccessUtc);
    }

    [Fact]
    public async Task Ordinary_success_cannot_close_an_open_breaker()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);
        var callStart = DateTime.UtcNow.AddSeconds(-1);
        await svc.ReportFailureAsync(Name, callStart, "bad credentials", authRejected: true, threshold: 3, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(await svc.IsOpenAsync(Name, CancellationToken.None));

        // An in-flight caller happens to succeed (e.g. a request queued before the breaker tripped) —
        // per §5.4 this must be ignored: only TryCloseAfterProbeAsync may close an Open breaker.
        await svc.ReportSuccessAsync(Name, DateTime.UtcNow, CancellationToken.None);

        Assert.True(await svc.IsOpenAsync(Name, CancellationToken.None));
    }

    [Fact]
    public async Task Half_open_claim_has_exactly_one_winner()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);
        var callStart = DateTime.UtcNow.AddSeconds(-1);
        // Open with a probe lease already in the past so the claim window is eligible right away.
        await svc.ReportFailureAsync(Name, callStart, "bad credentials", authRejected: true, threshold: 3, probeLease: TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await Task.Delay(50);

        var first = await svc.TryClaimHalfOpenProbeAsync(Name, TimeSpan.FromMinutes(5), CancellationToken.None);
        var second = await svc.TryClaimHalfOpenProbeAsync(Name, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(first);
        Assert.False(second); // the first claim already pushed NextProbeUtc out
    }

    [Fact]
    public async Task TryCloseAfterProbe_closes_an_open_breaker_back_to_healthy()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);
        var callStart = DateTime.UtcNow.AddSeconds(-1);
        await svc.ReportFailureAsync(Name, callStart, "bad credentials", authRejected: true, threshold: 3, TimeSpan.FromMinutes(5), CancellationToken.None);

        await svc.TryCloseAfterProbeAsync(Name, DateTime.UtcNow, CancellationToken.None);

        var row = await svc.GetAsync(Name, CancellationToken.None);
        Assert.Equal(IntegrationHealthState.Healthy, row!.State);
        Assert.Equal(0, row.ConsecutiveFailures);
        Assert.Null(row.OpenedUtc);
        Assert.Null(row.NextProbeUtc);
    }

    [Fact]
    public async Task Stale_failure_started_before_the_last_transition_is_ignored()
    {
        await using var db = CreateContext();
        var svc = new IntegrationHealthService(db);
        var staleCallStart = DateTime.UtcNow;
        await Task.Delay(20);
        // A probe closes the breaker (a fresh transition) — anchors LastTransitionUtc to "now".
        await svc.ReportFailureAsync(Name, staleCallStart, "opened", authRejected: true, threshold: 3, TimeSpan.FromMinutes(5), CancellationToken.None);
        await svc.TryCloseAfterProbeAsync(Name, DateTime.UtcNow, CancellationToken.None);
        var afterClose = await svc.GetAsync(Name, CancellationToken.None);
        Assert.Equal(IntegrationHealthState.Healthy, afterClose!.State);

        // A slow request that started before the probe's close attempt is reported as a failure now — it
        // must be ignored, since it failed under conditions the probe has already superseded.
        await svc.ReportFailureAsync(Name, staleCallStart, "late failure from before the close", authRejected: false, threshold: 3, TimeSpan.FromMinutes(5), CancellationToken.None);

        var row = await svc.GetAsync(Name, CancellationToken.None);
        Assert.Equal(IntegrationHealthState.Healthy, row!.State);
        Assert.Equal(0, row.ConsecutiveFailures);
    }
}
