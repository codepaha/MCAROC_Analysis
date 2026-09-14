using System.Diagnostics;
using MCAROC_Analysis.Services.CalculationAssurance;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for the fix to the lease-vs-call-duration gap: CalculationAiAuditService's
/// constructor eagerly loads Google Cloud credentials, so it can't be constructed in-process — these tests
/// instead exercise CalculationAiAuditTimeoutRunner directly with a fake slow operation, proving the actual
/// mechanism that bounds a Vertex AI call really does end the call within a known window regardless of how
/// long-lived the outer (worker shutdown) token is. This is what makes
/// CalculationAiAuditOrchestrator.ComputeLeaseSeconds' guarantee real rather than aspirational.</summary>
public class CalculationAiAuditTimeoutRunnerTests
{
    [Fact]
    public async Task OperationLongerThanTimeout_IsStoppedAtApproximatelyTheTimeout_NotTheOuterTokenLifetime()
    {
        // Simulates the exact bug scenario: an outer token that behaves like a BackgroundService's
        // shutdown token — it never fires on its own during this test — combined with an operation that
        // would otherwise run far longer than the configured timeout (here, a stand-in for an over-lease
        // Vertex AI call). Proves the operation is stopped by the timeout alone.
        var neverFiringOuterToken = CancellationToken.None;
        var stopwatch = Stopwatch.StartNew();

        var (completed, result, timedOut) = await CalculationAiAuditTimeoutRunner.RunAsync(
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // far longer than the 1s timeout below
                return "should never be reached";
            },
            TimeSpan.FromSeconds(1),
            neverFiringOuterToken);

        stopwatch.Stop();

        Assert.False(completed);
        Assert.True(timedOut);
        Assert.Null(result);
        // Generous upper bound (well under the 30s the fake operation would otherwise take) — proves the
        // call is actually bounded, not just eventually cancelled by something else.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected the timeout to stop the operation quickly; took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ElapsedTimeForATimedOutCall_NeverExceedsTheConfiguredTimeoutByMuch()
    {
        // Proves the enforced ceiling is tight, not just eventually-true: an operation that would run for
        // 30s is actually stopped close to the 1s timeout, not somewhere between 1s and 30s. This is what
        // makes CalculationAiAuditOrchestrator.ComputeLeaseSeconds' margin (timeout + 60s) a real,
        // dependable guarantee rather than a hopeful estimate.
        var configuredTimeout = TimeSpan.FromSeconds(1);
        var stopwatch = Stopwatch.StartNew();

        var (_, _, timedOut) = await CalculationAiAuditTimeoutRunner.RunAsync(
            async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return "unreachable"; },
            configuredTimeout,
            CancellationToken.None);

        stopwatch.Stop();

        Assert.True(timedOut);
        Assert.True(stopwatch.Elapsed < configuredTimeout + TimeSpan.FromSeconds(5),
            $"Expected close to the {configuredTimeout} timeout; took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task FastOperation_CompletesNormally_NeverReportedAsTimedOut()
    {
        var (completed, result, timedOut) = await CalculationAiAuditTimeoutRunner.RunAsync(
            async ct => { await Task.Delay(TimeSpan.FromMilliseconds(10), ct); return "ok"; },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.True(completed);
        Assert.False(timedOut);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task OuterTokenCancellation_IsNotMisreportedAsATimeout()
    {
        // A genuine app-shutdown cancellation (the outer token itself firing) must propagate as a real
        // cancellation, not be swallowed and misreported as "timed out" — those are different failure
        // modes the caller needs to be able to tell apart.
        using var outerCts = new CancellationTokenSource();
        outerCts.Cancel();

        // ThrowsAnyAsync, not ThrowsAsync: the runtime throws TaskCanceledException, a subtype of
        // OperationCanceledException — xUnit's exact-type ThrowsAsync would (wrongly) fail on that.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CalculationAiAuditTimeoutRunner.RunAsync(
                async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return "unreachable"; },
                TimeSpan.FromSeconds(30),
                outerCts.Token));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(600)]
    public void ComputeLeaseSeconds_AlwaysExceedsTheConfiguredTimeout(int timeoutSeconds)
    {
        // Regression test for the root cause: two independently-configured values (a timeout and a lease)
        // could previously drift out of sync if an operator raised one without the other. Deriving the
        // lease from the timeout makes that structurally impossible.
        var leaseSeconds = CalculationAiAuditOrchestrator.ComputeLeaseSeconds(timeoutSeconds);

        Assert.True(leaseSeconds > timeoutSeconds);
    }
}
