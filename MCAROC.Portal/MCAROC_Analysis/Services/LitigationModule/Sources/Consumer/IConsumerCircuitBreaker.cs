using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Circuit breaker interface for e-Jagriti consumer portal requests (plan section 4.9).</summary>
public interface IConsumerCircuitBreaker
{
    /// <summary>Checks whether the circuit breaker is currently open (blocking outgoing requests).</summary>
    bool IsOpen();

    /// <summary>Records a successful response, resetting consecutive failure counts.</summary>
    void RecordSuccess();

    /// <summary>Records an HTTP failure (5xx, 429), incrementing failures and potentially tripping the breaker.</summary>
    void RecordFailure(HttpStatusCode statusCode, TimeSpan? retryAfter = null);

    /// <summary>Count of consecutive transient failures.</summary>
    int ConsecutiveFailures { get; }

    /// <summary>Timestamp until which the circuit remains tripped, if currently open.</summary>
    DateTimeOffset? TrippedUntil { get; }
}

