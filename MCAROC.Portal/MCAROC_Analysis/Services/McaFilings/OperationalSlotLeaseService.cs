using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.McaFilings;

public class OperationalSlotLeaseService(AppDbContext db, ILogger<OperationalSlotLeaseService> logger) : IOperationalSlotLeaseService
{
    public const string LargeUploadSlot = "LargeUpload";
    public const string LargeUnpackSlot = "LargeUnpack";

    public async Task<SlotLeaseResult> TryAcquireSlotAsync(string slotType, string holderId, TimeSpan duration, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            db.ChangeTracker.Clear();
            var lease = await db.OperationalSlotLeases
                .FromSqlInterpolated($"SELECT SlotType, ActiveHolderId, AcquiredUtc, ExpiresUtc, LastHeartbeatUtc FROM OperationalSlotLeases WITH (UPDLOCK, HOLDLOCK) WHERE SlotType = {slotType}")
                .FirstOrDefaultAsync(ct);

            var now = DateTime.UtcNow;

            if (lease is null)
            {
                lease = new OperationalSlotLease
                {
                    SlotType = slotType,
                    ActiveHolderId = holderId,
                    AcquiredUtc = now,
                    ExpiresUtc = now.Add(duration),
                    LastHeartbeatUtc = now
                };
                db.OperationalSlotLeases.Add(lease);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new SlotLeaseResult(true, holderId, null);
            }

            // Check if slot is free or previous holder's lease expired
            if (lease.ActiveHolderId is null || lease.ExpiresUtc < now || lease.ActiveHolderId == holderId)
            {
                lease.ActiveHolderId = holderId;
                lease.AcquiredUtc = now;
                lease.ExpiresUtc = now.Add(duration);
                lease.LastHeartbeatUtc = now;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new SlotLeaseResult(true, holderId, null);
            }

            // Slot is actively held by someone else
            await tx.RollbackAsync(ct);
            logger.LogWarning("Operational slot '{SlotType}' is currently held by {HolderId} until {ExpiresUtc}", slotType, lease.ActiveHolderId, lease.ExpiresUtc);
            return new SlotLeaseResult(false, lease.ActiveHolderId, $"Slot '{slotType}' is busy (held by another operation).");
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
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ActiveHolderId, (string?)null)
                .SetProperty(s => s.ExpiresUtc, (DateTime?)null)
                .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);
    }
}
