using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.McaFilings;

public class StorageReservationManager(
    AppDbContext db,
    IOptions<LargeArchiveUploadOptions> options,
    ILogger<StorageReservationManager> logger) : IStorageReservationManager
{
    private readonly LargeArchiveUploadOptions _opts = options.Value;

    public async Task<ReservationResult> TryReserveUploadCapacityAsync(
        Guid sessionId,
        long declaredArchiveSizeBytes,
        string stagingDirectory,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        var stagingVolume = GetCanonicalVolumeRoot(stagingDirectory);
        var destVolume = GetCanonicalVolumeRoot(destinationDirectory);

        var stagingBytes = _opts.CalculateStagingVolumeBytes(declaredArchiveSizeBytes);
        var destBytes = _opts.CalculateDestinationVolumeWorstCaseBytes(declaredArchiveSizeBytes);

        // Group volume requests: if on same volume root, sum bytes; if distinct, check each
        var volumeRequirements = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(stagingVolume, destVolume, StringComparison.OrdinalIgnoreCase))
        {
            volumeRequirements[stagingVolume] = stagingBytes + destBytes;
        }
        else
        {
            volumeRequirements[stagingVolume] = stagingBytes;
            volumeRequirements[destVolume] = destBytes;
        }

        // Canonical ordering (alphabetical) avoids deadlocks
        var orderedVolumes = volumeRequirements.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            db.ChangeTracker.Clear();
            // Lock and synchronize volume lease rows
            foreach (var vol in orderedVolumes)
            {
                var lease = await db.StorageVolumeLeases
                    .FromSqlInterpolated($"SELECT VolumeRoot, ActiveReservedBytes, RowVersion FROM StorageVolumeLeases WITH (UPDLOCK, HOLDLOCK) WHERE VolumeRoot = {vol}")
                    .FirstOrDefaultAsync(ct);

                if (lease is null)
                {
                    // Initialize volume lease if first time
                    lease = new StorageVolumeLease { VolumeRoot = vol, ActiveReservedBytes = 0 };
                    db.StorageVolumeLeases.Add(lease);
                    await db.SaveChangesAsync(ct);
                }

                // Recalculate true active reserved bytes from live active reservations
                var now = DateTime.UtcNow;
                var trueActiveReserved = await db.StorageCapacityReservations
                    .Where(r => r.VolumeRoot == vol && r.State == StorageCapacityReservationState.Active && r.ExpiresUtc > now)
                    .SumAsync(r => (long?)r.ReservedBytes, ct) ?? 0L;

                lease.ActiveReservedBytes = trueActiveReserved;

                // Check physical available free space on volume
                var freeSpace = GetAvailableFreeSpace(vol);
                var needed = volumeRequirements[vol];

                if (freeSpace - trueActiveReserved < needed)
                {
                    await tx.RollbackAsync(ct);
                    var msg = $"Insufficient storage on volume '{vol}'. Free: {freeSpace / (1024 * 1024)}MB, Reserved: {trueActiveReserved / (1024 * 1024)}MB, Needed: {needed / (1024 * 1024)}MB.";
                    logger.LogWarning("{Message}", msg);
                    return new ReservationResult(false, msg, null);
                }

                // Update lease cached total
                lease.ActiveReservedBytes += needed;
            }

            // Create granular reservation records
            var resId = Guid.NewGuid();
            var expires = DateTime.UtcNow.Add(_opts.SessionLifetime);

            if (string.Equals(stagingVolume, destVolume, StringComparison.OrdinalIgnoreCase))
            {
                db.StorageCapacityReservations.Add(new StorageCapacityReservation
                {
                    ReservationId = resId,
                    OwnerType = "UploadSession",
                    OwnerId = sessionId.ToString(),
                    VolumeRoot = stagingVolume,
                    ReservedBytes = volumeRequirements[stagingVolume],
                    State = StorageCapacityReservationState.Active,
                    CreatedUtc = DateTime.UtcNow,
                    LastHeartbeatUtc = DateTime.UtcNow,
                    ExpiresUtc = expires
                });
            }
            else
            {
                db.StorageCapacityReservations.Add(new StorageCapacityReservation
                {
                    ReservationId = resId,
                    OwnerType = "UploadSession",
                    OwnerId = sessionId.ToString(),
                    VolumeRoot = stagingVolume,
                    ReservedBytes = stagingBytes,
                    State = StorageCapacityReservationState.Active,
                    CreatedUtc = DateTime.UtcNow,
                    LastHeartbeatUtc = DateTime.UtcNow,
                    ExpiresUtc = expires
                });

                db.StorageCapacityReservations.Add(new StorageCapacityReservation
                {
                    ReservationId = Guid.NewGuid(),
                    OwnerType = "UploadSession",
                    OwnerId = sessionId.ToString(),
                    VolumeRoot = destVolume,
                    ReservedBytes = destBytes,
                    State = StorageCapacityReservationState.Active,
                    CreatedUtc = DateTime.UtcNow,
                    LastHeartbeatUtc = DateTime.UtcNow,
                    ExpiresUtc = expires
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new ReservationResult(true, null, resId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Failed to acquire storage reservations for session {SessionId}", sessionId);
            return new ReservationResult(false, $"Reservation failed: {ex.Message}", null);
        }
    }

    public async Task<bool> TransitionReservationToBatchAsync(Guid sessionId, long batchId, CancellationToken ct = default)
    {
        var sessionIdStr = sessionId.ToString();
        var batchIdStr = batchId.ToString();

        var affected = await db.StorageCapacityReservations
            .Where(r => r.OwnerType == "UploadSession" && r.OwnerId == sessionIdStr && r.State == StorageCapacityReservationState.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.OwnerType, "FilingBatch")
                .SetProperty(r => r.OwnerId, batchIdStr)
                .SetProperty(r => r.LastHeartbeatUtc, DateTime.UtcNow), ct);

        return affected > 0;
    }

    public async Task ReleaseReservationsAsync(string ownerType, string ownerId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            var volumeRoots = await db.StorageCapacityReservations
                .Where(r => r.OwnerType == ownerType && r.OwnerId == ownerId && r.State == StorageCapacityReservationState.Active)
                .Select(r => r.VolumeRoot)
                .Distinct()
                .OrderBy(v => v)
                .ToListAsync(ct);

            if (volumeRoots.Count == 0)
            {
                await tx.CommitAsync(ct);
                return;
            }

            var now = DateTime.UtcNow;
            await db.StorageCapacityReservations
                .Where(r => r.OwnerType == ownerType && r.OwnerId == ownerId && r.State == StorageCapacityReservationState.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.State, StorageCapacityReservationState.Released)
                    .SetProperty(r => r.LastHeartbeatUtc, now), ct);

            // Re-sync volume aggregate totals
            foreach (var vol in volumeRoots)
            {
                var trueActive = await db.StorageCapacityReservations
                    .Where(r => r.VolumeRoot == vol && r.State == StorageCapacityReservationState.Active && r.ExpiresUtc > now)
                    .SumAsync(r => (long?)r.ReservedBytes, ct) ?? 0L;

                await db.StorageVolumeLeases
                    .Where(v => v.VolumeRoot == vol)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.ActiveReservedBytes, trueActive), ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Failed to release reservations for {OwnerType} {OwnerId}", ownerType, ownerId);
        }
    }

    public async Task<int> SweepExpiredReservationsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            var volumeRoots = await db.StorageCapacityReservations
                .Where(r => r.State == StorageCapacityReservationState.Active && r.ExpiresUtc < now)
                .Select(r => r.VolumeRoot)
                .Distinct()
                .OrderBy(v => v)
                .ToListAsync(ct);

            if (volumeRoots.Count == 0)
            {
                await tx.CommitAsync(ct);
                return 0;
            }

            var count = await db.StorageCapacityReservations
                .Where(r => r.State == StorageCapacityReservationState.Active && r.ExpiresUtc < now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.State, StorageCapacityReservationState.Expired)
                    .SetProperty(r => r.LastHeartbeatUtc, now), ct);

            foreach (var vol in volumeRoots)
            {
                var trueActive = await db.StorageCapacityReservations
                    .Where(r => r.VolumeRoot == vol && r.State == StorageCapacityReservationState.Active && r.ExpiresUtc > now)
                    .SumAsync(r => (long?)r.ReservedBytes, ct) ?? 0L;

                await db.StorageVolumeLeases
                    .Where(v => v.VolumeRoot == vol)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.ActiveReservedBytes, trueActive), ct);
            }

            await tx.CommitAsync(ct);
            logger.LogInformation("Swept {Count} expired storage reservation(s)", count);
            return count;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Failed during sweep of expired storage reservations");
            return 0;
        }
    }

    public static string GetCanonicalVolumeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.IsNullOrWhiteSpace(root) ? "DEFAULT" : root.ToUpperInvariant();
    }

    public virtual long GetAvailableFreeSpace(string volumeRoot)
    {
        try
        {
            var drive = new DriveInfo(volumeRoot);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            // Default fallback if DriveInfo cannot parse virtual path
            return 100_000_000_000L; // 100 GB
        }
    }
}
