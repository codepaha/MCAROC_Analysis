using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>Capacity-bounded admission for a named operational slot ('LargeUpload'/'LargeUnpack') — up to
/// <c>capacity</c> holders may hold a non-expired lease on the same slot type at once, letting several
/// large-archive uploads or unpack batches run in parallel instead of the whole app being serialized to
/// exactly one in flight (the historical behavior, still the default when a caller omits capacity).
///
/// Concurrent acquire attempts for the SAME slot type are fenced with SQL Server's sp_getapplock, keyed by
/// slot type, scoped to the surrounding transaction (auto-released on commit/rollback) — the same pattern
/// FinalizationRecoveryService already uses for its own per-session fencing. Without this, two acquirers
/// could both count "N-1 of N held" and both be admitted, exceeding capacity by one.</summary>
public class OperationalSlotLeaseService(AppDbContext db, ILogger<OperationalSlotLeaseService> logger) : IOperationalSlotLeaseService
{
    public const string LargeUploadSlot = "LargeUpload";
    public const string LargeUnpackSlot = "LargeUnpack";

    public async Task<SlotLeaseResult> TryAcquireSlotAsync(string slotType, string holderId, TimeSpan duration, int capacity = 1, CancellationToken ct = default)
    {
        if (capacity < 1) capacity = 1;

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DECLARE @res INT;
                EXEC @res = sp_getapplock
                    @Resource = {"OperationalSlot_" + slotType},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 30000;
                IF (@res < 0) THROW 50001, 'Lock acquisition timeout in sp_getapplock', 1;", ct);

            var now = DateTime.UtcNow;

            // Opportunistic cleanup — an expired row must never count toward capacity, and this also keeps
            // the table from growing unboundedly across every crashed/abandoned holder.
            await db.OperationalSlotLeases
                .Where(s => s.SlotType == slotType && s.ExpiresUtc <= now)
                .ExecuteDeleteAsync(ct);

            var existing = await db.OperationalSlotLeases
                .FirstOrDefaultAsync(s => s.SlotType == slotType && s.ActiveHolderId == holderId, ct);
            if (existing is not null)
            {
                // Idempotent re-acquire by the same holder (a retry, or a resumed job that already held
                // this slot) — renew in place rather than inserting a second row and double-counting it.
                existing.AcquiredUtc = now;
                existing.ExpiresUtc = now.Add(duration);
                existing.LastHeartbeatUtc = now;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new SlotLeaseResult(true, holderId, null);
            }

            var activeCount = await db.OperationalSlotLeases.CountAsync(s => s.SlotType == slotType, ct);
            if (activeCount >= capacity)
            {
                var blocker = await db.OperationalSlotLeases
                    .Where(s => s.SlotType == slotType)
                    .OrderBy(s => s.AcquiredUtc)
                    .Select(s => s.ActiveHolderId)
                    .FirstOrDefaultAsync(ct);
                await tx.RollbackAsync(ct);
                logger.LogWarning("Operational slot '{SlotType}' is at capacity ({Active}/{Capacity}); refusing {HolderId}",
                    slotType, activeCount, capacity, holderId);
                return new SlotLeaseResult(false, blocker, $"Slot '{slotType}' is busy ({activeCount}/{capacity} held).");
            }

            db.OperationalSlotLeases.Add(new OperationalSlotLease
            {
                LeaseId = Guid.NewGuid(),
                SlotType = slotType,
                ActiveHolderId = holderId,
                AcquiredUtc = now,
                ExpiresUtc = now.Add(duration),
                LastHeartbeatUtc = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new SlotLeaseResult(true, holderId, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Failed to acquire operational slot lease for {SlotType}", slotType);
            return new SlotLeaseResult(false, null, ex.Message);
        }
    }

    public async Task<bool> TryRenewSlotAsync(string slotType, string holderId, TimeSpan extension, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var newExpires = now.Add(extension);

        var affected = await db.OperationalSlotLeases
            .Where(s => s.SlotType == slotType && s.ActiveHolderId == holderId && s.ExpiresUtc > now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ExpiresUtc, newExpires)
                .SetProperty(s => s.LastHeartbeatUtc, now), ct);

        return affected > 0;
    }

    public async Task ReleaseSlotAsync(string slotType, string holderId, CancellationToken ct = default)
    {
        await db.OperationalSlotLeases
            .Where(s => s.SlotType == slotType && s.ActiveHolderId == holderId)
            .ExecuteDeleteAsync(ct);
    }
}
