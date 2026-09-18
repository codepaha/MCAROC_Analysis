namespace MCAROC_Analysis.Data.Entities;

public enum StorageCapacityReservationState
{
    Active,
    Transferred,
    Released,
    Expired
}

public class StorageCapacityReservation
{
    public Guid ReservationId { get; set; }

    /// <summary>'UploadSession' or 'FilingBatch'</summary>
    public string OwnerType { get; set; } = string.Empty;

    /// <summary>SessionId string or BatchId string</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Canonical volume root, e.g. 'C:\' or 'E:\'</summary>
    public string VolumeRoot { get; set; } = string.Empty;

    public long ReservedBytes { get; set; }

    public StorageCapacityReservationState State { get; set; } = StorageCapacityReservationState.Active;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
}

public class StorageVolumeLease
{
    public string VolumeRoot { get; set; } = string.Empty;
    public long ActiveReservedBytes { get; set; }
    public byte[]? RowVersion { get; set; }
}

/// <summary>One holder's active lease on a capacity-bounded operational slot ('LargeUpload' or
/// 'LargeUnpack'). Multiple rows can share a SlotType — up to whatever capacity the caller passes to
/// TryAcquireSlotAsync — which is what lets several large-archive uploads or unpack batches run at once
/// instead of the whole app being limited to exactly one in flight. A (SlotType, ActiveHolderId) pair is
/// unique: a holder re-acquiring its own slot renews the same row rather than taking a second one.</summary>
public class OperationalSlotLease
{
    public Guid LeaseId { get; set; }

    /// <summary>'LargeUpload' or 'LargeUnpack'</summary>
    public string SlotType { get; set; } = string.Empty;

    public string ActiveHolderId { get; set; } = string.Empty;
    public DateTime AcquiredUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
}
