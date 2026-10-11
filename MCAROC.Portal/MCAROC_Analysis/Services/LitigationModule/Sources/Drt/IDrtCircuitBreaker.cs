using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Circuit breaker contract for DRT portal operations per plan section 4.9.
/// After N consecutive 5xx/429 failures across agents, it opens to defer further requests.</summary>
public interface IDrtCircuitBreaker
{
    /// <summary>True if the circuit breaker is currently open (tripped).</summary>
    bool IsOpen();

    /// <summary>Records a successful response, resetting consecutive failure counts.</summary>
    void RecordSuccess();

    /// <summary>Records an HTTP failure (increments failure counts on 5xx/429 and trips if threshold reached).</summary>
    void RecordFailure(HttpStatusCode statusCode);

    /// <summary>Number of consecutive 5xx/429 failures currently recorded.</summary>
    int ConsecutiveFailures { get; }
}

