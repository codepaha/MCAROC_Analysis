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

    /// <summary>Exposed so <see cref="AutoFetchWorker"/> can gate on the reference-tool breaker *before*
    /// taking an item off the channel (docs/pipeline-automation-plan.md §5.4 mechanism 1) — an item must
    /// never be dequeued while the breaker is open, since a dequeued-but-unprocessable job has nowhere to
    /// go back to.</summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);
    public bool TryRead(out long jobId) => _channel.Reader.TryRead(out jobId);
}
