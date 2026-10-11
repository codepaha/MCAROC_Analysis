using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Standard in-memory circuit breaker implementation for the DRT portal (plan section 4.9).
/// Trips after <paramref name="threshold"/> consecutive 5xx/429 failures.</summary>
public sealed class DrtCircuitBreaker : IDrtCircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _coolDown;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _trippedUntil;

    public DrtCircuitBreaker(int threshold = 8, TimeSpan? coolDown = null, TimeProvider? timeProvider = null)
    {
        _threshold = threshold;
        _coolDown = coolDown ?? TimeSpan.FromMinutes(5);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int ConsecutiveFailures
    {
        get { lock (_sync) return _consecutiveFailures; }
    }

    public bool IsOpen()
    {
        lock (_sync)
        {
            if (_trippedUntil is null) return false;
            var now = _timeProvider.GetUtcNow();
            if (now >= _trippedUntil.Value)
            {
                // Cool down expired: half-open, allow probe
                return false;
            }
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _trippedUntil = null;
        }
    }

    public void RecordFailure(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (code is not (429 or 500 or 502 or 503 or 504))
            return;

        lock (_sync)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _threshold)
            {
                _trippedUntil = _timeProvider.GetUtcNow().Add(_coolDown);
            }
        }
    }
}

