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

public class OperationalSlotLease
{
    /// <summary>'LargeUpload' or 'LargeUnpack'</summary>
    public string SlotType { get; set; } = string.Empty;

    public string? ActiveHolderId { get; set; }
    public DateTime? AcquiredUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
}
