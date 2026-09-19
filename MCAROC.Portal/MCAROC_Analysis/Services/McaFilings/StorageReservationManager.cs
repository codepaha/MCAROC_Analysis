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
        var stagingVolume = ResolveVolumeRoot(stagingDirectory);
        var destVolume = ResolveVolumeRoot(destinationDirectory);

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

    public async Task<ReservationResult> TryReserveAsync(
        string ownerType, string ownerId, string directory, long bytes, TimeSpan lifetime, CancellationToken ct = default)
    {
        var volume = ResolveVolumeRoot(directory);

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            // Deliberately no db.ChangeTracker.Clear() here, unlike TryReserveUploadCapacityAsync: that
            // method's only caller (RequestsUploadController.Initiate) never touches its own tracked
            // entities again after calling it, so clearing is harmless there. This method's caller
            // (AutoFetchJobService) holds a long-lived tracked AutoFetchJob/McaRequest across its entire
            // multi-stage run and keeps mutating + saving them long after this call returns — clearing the
            // whole tracker here silently detaches those entities, so every subsequent SaveChangesAsync()
            // for them becomes a no-op (found live: a job's status/progress simply stopped persisting
            // right after its first successful reservation, with no exception anywhere to explain why).
            // Staleness of an already-tracked StorageVolumeLease/StorageCapacityReservation row is not a
            // real risk here either way: this method is called at most once per AppDbContext instance
            // (each job run gets its own fresh scoped context), so nothing could have tracked one already.
            var lease = await db.StorageVolumeLeases
                .FromSqlInterpolated($"SELECT VolumeRoot, ActiveReservedBytes, RowVersion FROM StorageVolumeLeases WITH (UPDLOCK, HOLDLOCK) WHERE VolumeRoot = {volume}")
                .FirstOrDefaultAsync(ct);

            if (lease is null)
            {
                lease = new StorageVolumeLease { VolumeRoot = volume, ActiveReservedBytes = 0 };
                db.StorageVolumeLeases.Add(lease);
                await db.SaveChangesAsync(ct);
            }

            // Recalculate true active reserved bytes from live active reservations, same as
            // TryReserveUploadCapacityAsync — the cached lease value is never trusted on its own.
            var now = DateTime.UtcNow;
            var trueActiveReserved = await db.StorageCapacityReservations
                .Where(r => r.VolumeRoot == volume && r.State == StorageCapacityReservationState.Active && r.ExpiresUtc > now)
                .SumAsync(r => (long?)r.ReservedBytes, ct) ?? 0L;
            lease.ActiveReservedBytes = trueActiveReserved;

            var freeSpace = GetAvailableFreeSpace(volume);
            if (freeSpace - trueActiveReserved < bytes)
            {
                await tx.RollbackAsync(ct);
                // The line above set lease.ActiveReservedBytes = trueActiveReserved, marking the tracked
                // entity Modified — rolling back the SQL transaction undoes that in the database but does
                // nothing to EF's own change tracker, which has no concept of a SQL rollback. Left as-is,
                // a LATER call to this method on the same AppDbContext (e.g. a retry once space frees up)
                // would query for a fresh row, find this entity already tracked and Modified, and EF would
                // keep serving the stale in-memory values — including a RowVersion the real row has since
                // moved past — instead of the fresh read, eventually failing that later call's own
                // SaveChangesAsync with a spurious concurrency exception. Detaching just this one entity
                // (not the whole tracker — the caller may hold other, unrelated tracked entities of its
                // own across this call, see the remarks on why this method never calls ChangeTracker.Clear)
                // forces the next call to start from a genuinely fresh read.
                db.Entry(lease).State = EntityState.Detached;
                var msg = $"Insufficient storage on volume '{volume}'. Free: {freeSpace / (1024 * 1024)}MB, Reserved: {trueActiveReserved / (1024 * 1024)}MB, Needed: {bytes / (1024 * 1024)}MB.";
                logger.LogWarning("{Message}", msg);
                return new ReservationResult(false, msg, null);
            }

            lease.ActiveReservedBytes += bytes;

            var resId = Guid.NewGuid();
            db.StorageCapacityReservations.Add(new StorageCapacityReservation
            {
                ReservationId = resId,
                OwnerType = ownerType,
                OwnerId = ownerId,
                VolumeRoot = volume,
                ReservedBytes = bytes,
                State = StorageCapacityReservationState.Active,
                CreatedUtc = now,
                LastHeartbeatUtc = now,
                ExpiresUtc = now.Add(lifetime)
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ReservationResult(true, null, resId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Failed to acquire storage reservation for {OwnerType} {OwnerId}", ownerType, ownerId);
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
            throw;
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

    /// <summary>Instance seam around <see cref="GetCanonicalVolumeRoot"/> so tests can substitute a
    /// platform-independent volume key. On Linux, backslashes are ordinary filename characters rather than
    /// separators, so a Windows-style UNC path (e.g. a test's synthetic "\\vol-&lt;guid&gt;\share\...")
    /// never resolves to its own root — <c>Path.GetFullPath</c> treats it as one relative segment under the
    /// working directory and <c>Path.GetPathRoot</c> collapses it to "/", the same volume every real temp
    /// directory on the box also resolves to. Two tests (or two unrelated test classes) that each believe
    /// they hold an isolated synthetic volume then silently share one real DB bucket and contend for the
    /// same tracked headroom. Overriding this in a test to return the input path unchanged keeps each
    /// caller's already-unique path as its own volume key regardless of OS path semantics.</summary>
    protected virtual string ResolveVolumeRoot(string path) => GetCanonicalVolumeRoot(path);

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
