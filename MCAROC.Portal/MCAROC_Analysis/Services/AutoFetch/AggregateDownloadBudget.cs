namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Atomic byte budget for one auto-fetch job's aggregate download cap.
///
/// The earlier version of this cap checked <c>bytes &gt;= aggregateCap</c> before starting a download and
/// only added the real file size to <c>bytes</c> after it finished — two separate, unsynchronized steps.
/// Under real concurrency (<c>DownloadConcurrency</c> workers), several of them could read "under cap"
/// before any of them had added anything, each then write up to <c>MaxResponseBytes</c>, and only find out
/// afterward — letting the job overshoot the cap by up to <c>DownloadConcurrency × MaxResponseBytes</c>,
/// not the one file's worth a cap is supposed to allow at most.
///
/// <see cref="TryReserve"/> fixes this by claiming a transfer's worst-case size (the per-response cap)
/// BEFORE it starts, as a single atomic compare-and-swap — no two callers can ever both observe "room for
/// one more" for the same last slice of budget. That bounds real disk usage to at most
/// <c>capacity + worstCaseBytes</c> (one in-flight transfer's worth of overshoot), regardless of how many
/// workers race the check, not <c>capacity + concurrency × worstCaseBytes</c>. <see cref="Commit"/> trues
/// a reservation down to what a completed transfer actually wrote (freeing room for more when the real
/// size came in under the worst case); <see cref="Release"/> gives back a reservation for a transfer that
/// never wrote anything.</summary>
public sealed class AggregateDownloadBudget(long capacityBytes)
{
    private long _reserved;

    /// <summary>Bytes currently reserved — worst-case for in-flight transfers, actual for committed ones.
    /// Never authoritative for "bytes really on disk" while transfers are in flight; use a separate
    /// counter for progress display if that distinction matters to the caller.</summary>
    public long Reserved => Interlocked.Read(ref _reserved);

    /// <summary>Accounts for usage this budget didn't itself admit — e.g. files already staged on disk
    /// from a previous, interrupted run of the same job. Not capacity-checked: it is a statement of fact
    /// about what already exists, not a new admission decision, and can legitimately push the budget over
    /// capacity (correctly refusing every new reservation afterward).</summary>
    public void SeedKnownUsage(long bytes) => Interlocked.Add(ref _reserved, bytes);

    /// <summary>Atomically claims <paramref name="worstCaseBytes"/> of budget if doing so does not exceed
    /// capacity that was already free at the moment of the claim. Returns false without reserving anything
    /// if the budget was already at or past capacity.</summary>
    public bool TryReserve(long worstCaseBytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _reserved);
            if (current >= capacityBytes) return false;
            var next = current + worstCaseBytes;
            if (Interlocked.CompareExchange(ref _reserved, next, current) == current)
                return true;
        }
    }

    public void Commit(long worstCaseBytes, long actualBytes) => Interlocked.Add(ref _reserved, actualBytes - worstCaseBytes);

    public void Release(long worstCaseBytes) => Interlocked.Add(ref _reserved, -worstCaseBytes);
}
