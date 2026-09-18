namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Atomic byte budget for one auto-fetch job's aggregate download cap — a genuine hard cap:
/// <see cref="Reserved"/> can never exceed <c>capacityBytes</c>, not even by one in-flight transfer.
///
/// The original version of this cap checked <c>bytes &gt;= aggregateCap</c> before starting a download and
/// only added the real file size to <c>bytes</c> after it finished — two separate, unsynchronized steps.
/// Under real concurrency (<c>DownloadConcurrency</c> workers), several of them could read "under cap"
/// before any of them had added anything, each then write up to <c>MaxResponseBytes</c>, and only find out
/// afterward — letting the job overshoot the cap by up to <c>DownloadConcurrency × MaxResponseBytes</c>.
///
/// A first fix made admission atomic but still let the ONE reservation that crossed the threshold through
/// (admitting whenever <c>current &lt; capacity</c>, regardless of whether adding the new reservation would
/// push past it) — bounding the overshoot to one transfer's worth instead of eliminating it. That is still
/// an overshoot: <c>MaxAggregateDownloadBytes</c> is a configured limit, not an approximate one.
/// <see cref="TryReserve"/> now requires <c>current + worstCaseBytes &lt;= capacityBytes</c> before
/// admitting — a reservation that would cross the cap is refused outright, even when some headroom remains
/// below it. The one cost is that a sliver of capacity smaller than <c>worstCaseBytes</c> can go unused
/// right at the boundary; that is the correct trade for a safety cap, not a defect — a partially-streamed
/// file would have to be truncated mid-transfer to use that sliver exactly, which is worse (a corrupt file)
/// than declining to start it.
///
/// The claim happens BEFORE a transfer starts, as a single atomic compare-and-swap — no two callers can
/// ever both observe "room for the same last slice of budget". <see cref="Commit"/> trues a reservation
/// down to what a completed transfer actually wrote (freeing room for more when the real size came in
/// under the worst case); <see cref="Release"/> gives back a reservation for a transfer that never wrote
/// anything.</summary>
public sealed class AggregateDownloadBudget(long capacityBytes)
{
    private long _reserved;

    /// <summary>Bytes currently reserved — worst-case for in-flight transfers, actual for committed ones.
    /// Never authoritative for "bytes really on disk" while transfers are in flight; use a separate
    /// counter for progress display if that distinction matters to the caller. Never exceeds
    /// <c>capacityBytes</c> as a result of <see cref="TryReserve"/> alone — see <see cref="SeedKnownUsage"/>
    /// for the one case where it legitimately can.</summary>
    public long Reserved => Interlocked.Read(ref _reserved);

    /// <summary>Accounts for usage this budget didn't itself admit — e.g. files already staged on disk
    /// from a previous, interrupted run of the same job. Not capacity-checked: it is a statement of fact
    /// about what already exists, not a new admission decision, and can legitimately push the budget over
    /// capacity (correctly refusing every new reservation afterward).</summary>
    public void SeedKnownUsage(long bytes) => Interlocked.Add(ref _reserved, bytes);

    /// <summary>Atomically claims <paramref name="worstCaseBytes"/> of budget if, and only if, doing so
    /// would not push the total past capacity — a reservation that would cross the cap is refused outright,
    /// never partially admitted. Returns false without reserving anything otherwise.</summary>
    public bool TryReserve(long worstCaseBytes)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _reserved);
            var next = current + worstCaseBytes;
            if (next > capacityBytes) return false;
            if (Interlocked.CompareExchange(ref _reserved, next, current) == current)
                return true;
        }
    }

    public void Commit(long worstCaseBytes, long actualBytes) => Interlocked.Add(ref _reserved, actualBytes - worstCaseBytes);

    public void Release(long worstCaseBytes) => Interlocked.Add(ref _reserved, -worstCaseBytes);
}
