using System.Threading.Channels;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>In-process work queue of completed <see cref="Data.Entities.LitigationSearchJob"/> ids awaiting
/// case persistence. Like the other queues in this codebase it does not survive a restart on its own —
/// <see cref="LitigationCasePersistenceWorker"/>'s startup sweep re-enqueues every completed job with no
/// source-report rows yet.</summary>
public sealed class LitigationCasePersistenceQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long jobId) => _channel.Writer.TryWrite(jobId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
