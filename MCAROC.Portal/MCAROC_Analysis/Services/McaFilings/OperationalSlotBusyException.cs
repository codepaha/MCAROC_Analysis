namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>
/// Thrown when an operational slot lease (e.g. LargeUnpackSlot) cannot be acquired because another
/// batch or worker actively holds it. Signals a transient retriable stand-down condition rather than
/// a fatal failure.
/// </summary>
public class OperationalSlotBusyException(string slotType, string? activeHolderId, string message)
    : InvalidOperationException(message)
{
    public string SlotType { get; } = slotType;
    public string? ActiveHolderId { get; } = activeHolderId;
}
