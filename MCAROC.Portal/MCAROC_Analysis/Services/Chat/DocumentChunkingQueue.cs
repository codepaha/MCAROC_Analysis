using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>In-process work queue of BatchIds awaiting chunking/embedding. Mirrors FilingProcessingQueue —
/// does not survive an app restart mid-run on its own; DocumentChunkingWorker's startup recovery sweep
/// covers a crash/restart.
///
/// Coalesced: a BatchId already queued and not yet handed to the worker is not written again. Chunking
/// re-enqueues the whole batch on every retryable per-document failure (DocumentChunkingOrchestrator), so
/// without this a batch of N documents all failing transiently could enqueue the batch ~N times and the
/// worker would run N full pending-scan + fan-out passes. With coalescing a wave of failures collapses to
/// one (occasionally two) follow-up passes.</summary>
public class DocumentChunkingQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    private readonly HashSet<long> _queued = [];
    private readonly Lock _gate = new();

    public void Enqueue(long batchId)
    {
        lock (_gate)
        {
            if (!_queued.Add(batchId))
                return; // already waiting in the channel
        }
        _channel.Writer.TryWrite(batchId);
    }

    public async IAsyncEnumerable<long> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var batchId in _channel.Reader.ReadAllAsync(ct))
        {
            // Cleared before yielding: a failure discovered during this pass can legitimately re-enqueue
            // the batch for one more pass.
            lock (_gate)
                _queued.Remove(batchId);
            yield return batchId;
        }
    }
}
