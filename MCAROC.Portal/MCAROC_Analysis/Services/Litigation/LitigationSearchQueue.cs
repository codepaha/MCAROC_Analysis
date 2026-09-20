using System.Threading.Channels;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>In-process work queue of <see cref="Data.Entities.LitigationSearchJob"/> ids. Like the other
/// queues in this codebase it does not survive a restart on its own — <see cref="LitigationSearchWorker"/>'s
/// startup sweep re-enqueues every non-terminal job whose lease (if any) has expired.</summary>
public sealed class LitigationSearchQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long jobId) => _channel.Writer.TryWrite(jobId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
