namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>On refusal, <see cref="HolderId"/> is one of the holders currently occupying the slot (the
/// earliest-acquired one) — useful for diagnostics, not necessarily the only thing blocking admission once
/// <paramref name="capacity"/> (see <see cref="IOperationalSlotLeaseService.TryAcquireSlotAsync"/>) is
/// greater than 1.</summary>
public record SlotLeaseResult(bool Success, string? HolderId, string? Error);

public interface IOperationalSlotLeaseService
{
    /// <summary>Admits <paramref name="holderId"/> if fewer than <paramref name="capacity"/> other holders
    /// currently have a non-expired lease on <paramref name="slotType"/> — default 1, i.e. today's
    /// single-holder mutex behavior, so any existing caller that omits it is unaffected. A holder
    /// re-acquiring a slot type it already holds renews its own lease rather than being counted twice.</summary>
    Task<SlotLeaseResult> TryAcquireSlotAsync(string slotType, string holderId, TimeSpan duration, int capacity = 1, CancellationToken ct = default);
    Task<bool> TryRenewSlotAsync(string slotType, string holderId, TimeSpan extension, CancellationToken ct = default);
    Task ReleaseSlotAsync(string slotType, string holderId, CancellationToken ct = default);
}
