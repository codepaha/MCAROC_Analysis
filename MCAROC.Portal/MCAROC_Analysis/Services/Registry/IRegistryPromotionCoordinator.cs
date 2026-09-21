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
    /// Attempts to acquire the cluster-wide gate for aggregate rebuild.
    /// Acquires a shared lock on "CompanyMaster_PromotionAdmission" (blocking promotions)
    /// and an exclusive lock on "CompanyMaster_AggregateRebuild" (blocking concurrent rebuilds).
    /// Returns null immediately if an exclusive promotion lock or an aggregate rebuild lock is currently held on this or any remote node.
    /// The returned scope holds the required session application locks and must remain open
    /// from before the first aggregate query through shared-store persistence.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs an advisory, moment-in-time probe to check whether promotion admission is currently locked
    /// by an active promotion on this or a remote node.
    /// Does not guarantee promotion will not start after the probe completes; callers serving cached aggregates
    /// use this to stamp warm snapshots with in-progress telemetry if promotion is detected.
    /// </summary>
    Task<bool> TryProbePromotionAdmissionAsync(CancellationToken cancellationToken = default);
}

