using System;
using System.Threading;
using System.Threading.Tasks;

namespace MCAROC_Analysis.Services.Registry;

/// <summary>
/// Coordinates promotion admission and aggregate rebuilds to prevent live-table scan hazards,
/// ensuring mutual exclusion between active promotion mutations and aggregate calculations.
/// </summary>
public interface IRegistryPromotionCoordinator
{
    /// <summary>
    /// Checks whether a promotion is actively admitted or in progress.
    /// </summary>
    bool IsPromotionActive(out long? activeJobId);

    /// <summary>
    /// Exclusively admits a sync job for live-table promotion.
    /// The returned scope holds an exclusive session application lock and must remain
    /// open across all batch mutations and terminal job-state writes.
    /// </summary>
    Task<IAsyncDisposable> AcquirePromotionAdmissionAsync(
        long syncRunId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire the shared gate for aggregate rebuild.
    /// Returns null immediately if an exclusive promotion lock is currently held on this or any remote node.
    /// The returned scope holds a shared session application lock and must remain open
    /// from before the first aggregate query through cache insertion.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken cancellationToken = default);
}
