using System.Threading.Channels;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>In-process work queue of <see cref="Data.Entities.LitigationReportSnapshot"/> ids awaiting case
/// persistence — not job ids: a snapshot, not the job it came from, is the independently-recoverable unit of
/// import work (see <see cref="LitigationCasePersistenceService"/>'s remarks for why). Like the other queues
/// in this codebase it does not survive a restart on its own —
/// <see cref="LitigationCasePersistenceService.RecoverStaleWorkAsync"/>'s startup sweep re-enqueues every
/// non-terminal snapshot.</summary>
public sealed class LitigationCasePersistenceQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long snapshotId) => _channel.Writer.TryWrite(snapshotId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
