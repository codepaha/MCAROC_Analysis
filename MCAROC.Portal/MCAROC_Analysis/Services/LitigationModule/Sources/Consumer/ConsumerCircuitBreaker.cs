using System.Collections.Concurrent;
using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Standard in-memory circuit breaker implementation for the e-Jagriti consumer portal (plan section 4.9).
/// Trips after <paramref name="threshold"/> consecutive 5xx/429 failures.
/// Can be shared across clients per host via <see cref="GetShared"/>.</summary>
public sealed class ConsumerCircuitBreaker : IConsumerCircuitBreaker
{
    private static readonly ConcurrentDictionary<string, ConsumerCircuitBreaker> SharedInstances = new(StringComparer.OrdinalIgnoreCase);

    private readonly int _threshold;
    private readonly TimeSpan _coolDown;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _trippedUntil;

    public ConsumerCircuitBreaker(int threshold = 8, TimeSpan? coolDown = null, TimeProvider? timeProvider = null)
    {
        _threshold = threshold;
        _coolDown = coolDown ?? TimeSpan.FromMinutes(5);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets or creates a shared circuit breaker instance for the given key/host (plan section 4.9).</summary>
    public static ConsumerCircuitBreaker GetShared(string key, int threshold = 8, TimeSpan? coolDown = null, TimeProvider? timeProvider = null)
    {
        return SharedInstances.GetOrAdd(key, _ => new ConsumerCircuitBreaker(threshold, coolDown, timeProvider));
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
        // Only 5xx server errors and 429 Too Many Requests trip the circuit
        var code = (int)statusCode;
        if (code != 429 && code is < 500 or > 599) return;

        lock (_sync)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _threshold || code == 429)
            {
                var duration = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
                    ? retryAfter.Value
                    : _coolDown;
                _trippedUntil = _timeProvider.GetUtcNow().Add(duration);
            }
        }
    }
}

