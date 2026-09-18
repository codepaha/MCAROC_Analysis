using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings;

public record ReservationResult(bool Success, string? Error, Guid? ReservationId);

/// <summary>
/// Manages atomic storage capacity reservations per volume root, safeguarding disk headroom
/// for staging, final archive, and uncompressed PDF extraction across uploads and unpacks.
/// </summary>
public interface IStorageReservationManager
{
    /// <summary>
    /// Atomically acquires storage capacity reservations across volume roots in canonical order.
    /// All-or-nothing: if either volume lacks space, transaction rolls back cleanly.
    /// </summary>
    Task<ReservationResult> TryReserveUploadCapacityAsync(
        Guid sessionId,
        long declaredArchiveSizeBytes,
        string stagingDirectory,
        string destinationDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Generic single-volume reservation for any other owner that needs to reserve disk headroom before
    /// writing — e.g. the auto-fetch pipeline reserving space for downloaded filing PDFs, which (unlike the
    /// resumable-upload flow) has no single declared archive size or fixed staging/destination pair.
    /// Draws from and updates the same StorageVolumeLease/StorageCapacityReservation ledger as
    /// <see cref="TryReserveUploadCapacityAsync"/>, so a large-archive upload and an auto-fetch job never
    /// independently believe the same free disk space is available to both of them.
    /// </summary>
    Task<ReservationResult> TryReserveAsync(
        string ownerType, string ownerId, string directory, long bytes, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>
    /// Transitions an existing reservation ownership from an upload session to an McaFilingBatch.
    /// </summary>
    Task<bool> TransitionReservationToBatchAsync(Guid sessionId, long batchId, CancellationToken ct = default);

    /// <summary>
    /// Releases all active reservations for the specified owner.
    /// </summary>
    Task ReleaseReservationsAsync(string ownerType, string ownerId, CancellationToken ct = default);

    /// <summary>
    /// Background/startup sweep: releases reservations whose ExpiresUtc has passed.
    /// </summary>
    Task<int> SweepExpiredReservationsAsync(CancellationToken ct = default);
}
