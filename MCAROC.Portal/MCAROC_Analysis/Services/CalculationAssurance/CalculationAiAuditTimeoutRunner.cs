namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Bounds an async operation to a fixed wall-clock timeout regardless of how long-lived the
/// caller's own cancellation token is. This exists specifically because the worker's ambient token is a
/// BackgroundService's shutdown token — it does not fire until the app itself is stopping, so passing it
/// alone to a Vertex AI call placed no real ceiling on that call's duration. That gap let a still-running
/// call outlive its own CalculationAiAuditRun lease, letting startup recovery reclaim the run and issue a
/// genuine duplicate external AI call. This is the mechanism that structurally guarantees a call ends
/// within a known bound — the lease duration is derived from that same bound (see
/// CalculationAiAuditOrchestrator.ComputeLeaseSeconds), so the two can never drift out of sync the way two
/// independently-configured values could.</summary>
public static class CalculationAiAuditTimeoutRunner
{
    public static async Task<(bool Completed, T? Result, bool TimedOut)> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken outerCt)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var result = await operation(timeoutCts.Token);
            return (true, result, false);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            // Cancelled by our own timeout, not by the caller's token (e.g. app shutdown) — a genuine
            // timeout, distinct from outer cancellation so the caller can report it as such.
            return (false, default, true);
        }
    }
}
