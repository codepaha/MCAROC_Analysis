using System.Threading.Channels;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>In-process work queue of <see cref="Data.Entities.AutoFetchJob"/> ids. Like the other queues
/// here it does not survive a restart on its own — <see cref="AutoFetchWorker"/>'s startup sweep
/// re-enqueues every non-terminal job.</summary>
public sealed class AutoFetchQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long jobId) => _channel.Writer.TryWrite(jobId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
