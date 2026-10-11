using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Circuit breaker interface for DRT and portal requests (plan section 4.9).</summary>
public interface IDrtCircuitBreaker
{
    /// <summary>Consecutive transient failures currently counted.</summary>
    int ConsecutiveFailures { get; }

    /// <summary>When the circuit breaker is scheduled to transition to half-open, or null if closed.</summary>
    DateTimeOffset? TrippedUntil { get; }

    /// <summary>True if the circuit is currently open and requests should be deferred.</summary>
    bool IsOpen();

    /// <summary>Records a successful request, resetting the failure counter and closing the breaker.</summary>
    void RecordSuccess();

    /// <summary>Records a transient failure (5xx or 429), optionally honouring the server's Retry-After interval.</summary>
    void RecordFailure(HttpStatusCode statusCode, TimeSpan? retryAfter = null);
}
