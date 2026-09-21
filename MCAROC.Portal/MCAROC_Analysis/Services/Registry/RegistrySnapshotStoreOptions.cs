namespace MCAROC_Analysis.Services.Registry;

/// <summary>
/// Configuration options for durable, cross-node aggregate snapshot storage.
/// Bound from configuration section "RegistrySnapshotStore".
/// </summary>
public class RegistrySnapshotStoreOptions
{
    public const string SectionName = "RegistrySnapshotStore";

    /// <summary>
    /// Root directory path for shared aggregate snapshots across portal nodes.
    /// In production environments, this setting is mandatory and must resolve to a shared durable volume (SMB/NFS).
    /// In non-production environments, falls back to "App_Data/RegistrySnapshots" if omitted.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>
    /// Number of most recent completed snapshots to retain on disk before pruning older snapshots.
    /// Defaults to 5 snapshots.
    /// </summary>
    public int RetentionCount { get; set; } = 5;
}
