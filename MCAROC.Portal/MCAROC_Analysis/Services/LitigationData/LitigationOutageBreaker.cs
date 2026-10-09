namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>The AI service stopped answering (a run of calls in a row failed before any reply came back), so the run pauses and tries again later
/// instead of marking hundreds of cases failed. Everything already analysed is kept.</summary>
public sealed class ProviderUnavailableException(string message) : Exception(message);

/// <summary>Counts model calls that failed to get a reply (a timeout, a quota or server error, a refused connection - not a reply that was
/// received and then rejected by validation). A run of <c>threshold</c> such calls in a row means the service is down or out of quota, not that
/// those cases are bad, so the run should stop spending time and wait. One good reply starts the count again. Safe across the run's parallel calls.</summary>
public sealed class LitigationOutageBreaker(int threshold)
{
    private int _consecutive;

    public bool Tripped => threshold > 0 && Volatile.Read(ref _consecutive) >= threshold;

    public int ConsecutiveFailures => Volatile.Read(ref _consecutive);

    public void Record(bool callFailed)
    {
        if (!callFailed) Interlocked.Exchange(ref _consecutive, 0);
        else Interlocked.Increment(ref _consecutive);
    }
}
