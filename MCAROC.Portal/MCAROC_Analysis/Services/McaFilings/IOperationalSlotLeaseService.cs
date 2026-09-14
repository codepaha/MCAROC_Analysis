namespace MCAROC_Analysis.Services.McaFilings;

public record SlotLeaseResult(bool Success, string? HolderId, string? Error);

public interface IOperationalSlotLeaseService
{
    Task<SlotLeaseResult> TryAcquireSlotAsync(string slotType, string holderId, TimeSpan duration, CancellationToken ct = default);
    Task<bool> TryRenewSlotAsync(string slotType, string holderId, TimeSpan extension, CancellationToken ct = default);
    Task ReleaseSlotAsync(string slotType, string holderId, CancellationToken ct = default);
}
