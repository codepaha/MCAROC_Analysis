using System.Collections.Concurrent;
using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Standard in-memory circuit breaker implementation for the DRT portal (plan section 4.9).
/// Trips after <paramref name="threshold"/> consecutive 5xx/429 failures.
/// Can be shared across clients per host via <see cref="GetShared"/>.</summary>
public sealed class DrtCircuitBreaker : IDrtCircuitBreaker
{
    private static readonly ConcurrentDictionary<string, DrtCircuitBreaker> SharedInstances = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Gets or creates a shared circuit breaker instance for the given key/host (plan section 4.9).</summary>
    public static DrtCircuitBreaker GetShared(string key, int threshold = 8, TimeSpan? coolDown = null, TimeProvider? timeProvider = null)
    {
        return SharedInstances.GetOrAdd(key, _ => new DrtCircuitBreaker(threshold, coolDown, timeProvider));
    }

    /// <summary>Resets all shared breaker instances (used in tests).</summary>
    public static void ResetShared()
    {
        SharedInstances.Clear();
    }

    public int ConsecutiveFailures
    {
        get { lock (_sync) return _consecutiveFailures; }
    }

    public DateTimeOffset? TrippedUntil
    {
        get { lock (_sync) return _trippedUntil; }
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

    public void RecordFailure(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    {
        var code = (int)statusCode;
        if (code is not (429 or 500 or 502 or 503 or 504))
            return;

        lock (_sync)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _threshold || code == 429)
            {
                var effectiveCooldown = retryAfter.HasValue && retryAfter.Value > _coolDown
                    ? retryAfter.Value
                    : _coolDown;
                _trippedUntil = _timeProvider.GetUtcNow().Add(effectiveCooldown);
            }
        }
    }
}
