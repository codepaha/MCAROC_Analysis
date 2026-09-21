using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Models.Registry;

namespace MCAROC_Analysis.Services.Registry;

/// <summary>
/// Cross-node storage contract for verified registry aggregate snapshots.
/// Provides L2 caching across portal nodes to prevent full-table scan fan-out.
/// </summary>
public interface IRegistrySnapshotStore
{
    /// <summary>
    /// Retrieves a verified snapshot by sync job ID from shared storage.
    /// Returns null if absent, corrupted, or unsupported payload version.
    /// </summary>
    Task<RegistryAggregateData?> GetSnapshotAsync(long jobId, CancellationToken ct = default);

    /// <summary>
    /// Persists a verified snapshot under an integrity envelope via atomic same-volume write.
    /// Prunes older snapshot files beyond configured retention.
    /// </summary>
    Task SaveSnapshotAsync(long jobId, RegistryAggregateData data, CancellationToken ct = default);
}
