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
