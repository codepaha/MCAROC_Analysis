namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Plan §6.2's fixed retry schedule (2 min / 10 min / 30 min / 2 h), ±20% jitter so many requests
/// failing at once don't all retry in the same instant. Pure given an injected <see cref="Random"/> — the
/// coordinator's own attempt cap (<c>Pipeline:MaxCoordinatorAttempts</c>) is enforced by the caller, not
/// here; this only answers "how long until the next try," never "should there be one."</summary>
public static class PipelineBackoff
{
    private static readonly TimeSpan[] Schedule =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    /// <param name="attemptNumber">1-based count of attempts already made — the delay returned is before the
    /// *next* one. Clamped to the schedule's last step for any attempt beyond it, so a caller with a higher
    /// cap than the schedule's length still gets a sane (if repeated) final wait rather than an index error.</param>
    public static TimeSpan NextDelay(int attemptNumber, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var baseDelay = Schedule[Math.Clamp(attemptNumber - 1, 0, Schedule.Length - 1)];
        var jitterFactor = 0.8 + rng.NextDouble() * 0.4; // [0.8, 1.2]
        return baseDelay * jitterFactor;
    }
}
