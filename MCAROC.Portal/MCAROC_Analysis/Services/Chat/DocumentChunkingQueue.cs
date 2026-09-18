using System.Threading.Channels;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>In-process work queue of BatchIds awaiting chunking/embedding. Mirrors FilingProcessingQueue —
/// does not survive an app restart mid-run on its own; DocumentChunkingWorker's startup recovery sweep
/// covers a crash/restart.
///
/// Coalesced across three states, not just "waiting in the channel":
/// <list type="bullet">
/// <item><b>Queued</b> — written to the channel, not yet dequeued. Any further <see cref="Enqueue"/> is a
/// pure no-op: the single already-queued entry will see everything pending once it runs.</item>
/// <item><b>Running</b> — the worker has dequeued it and called <see cref="MarkStarted"/>; its
/// scan-and-fan-out pass is in progress. An <see cref="Enqueue"/> now cannot be a no-op (this pass has
/// already read its own snapshot of pending documents and won't see anything that completes afterward),
/// so it flags <b>RunningNeedsFollowUp</b> instead.</item>
/// <item>Once <see cref="MarkDone"/> is called, a flagged batch is re-enqueued exactly once — never more,
/// regardless of how many completions arrived during the pass.</item>
/// </list>
/// This is what makes the per-document trigger in FilingBatchProcessor.ProcessDocumentAsync safe: with N
/// documents finishing in quick succession the worker runs at most one pass plus one follow-up, and the
/// follow-up's pending-scan picks up everything that completed during the first. The earlier design
/// cleared its marker on dequeue (conflating "queued" and "running"), so every completion during an
/// in-flight pass started ANOTHER overlapping full-batch scan — each re-selecting every still-pending
/// document and creating a task per document that then only no-ops against the atomic claim — roughly
/// quadratic duplicate work under a backlog. The same coalescing also covers
/// DocumentChunkingOrchestrator re-enqueueing the batch on each retryable per-document failure.</summary>
public class DocumentChunkingQueue
{
    private enum BatchState { Queued, Running, RunningNeedsFollowUp }

    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    private readonly Dictionary<long, BatchState> _state = [];
    private readonly Lock _gate = new();

    public void Enqueue(long batchId)
    {
        lock (_gate)
        {
            if (_state.TryGetValue(batchId, out var state))
            {
                if (state == BatchState.Running)
                    _state[batchId] = BatchState.RunningNeedsFollowUp;
                return; // Queued or already RunningNeedsFollowUp — already covered
            }
            _state[batchId] = BatchState.Queued;
        }
        _channel.Writer.TryWrite(batchId);
    }

    /// <summary>Called by the worker the moment it dequeues a batch, before starting its pass — the
    /// transition from "queued" to "running" that decides whether a concurrent Enqueue can still be a
    /// no-op (not yet) or must flag a follow-up (from now on).</summary>
    public void MarkStarted(long batchId)
    {
        lock (_gate)
            _state[batchId] = BatchState.Running;
    }

    /// <summary>Called once a batch's pass has fully completed (success or failure). Re-enqueues exactly
    /// once if the batch was flagged for a follow-up while that pass ran.</summary>
    public void MarkDone(long batchId)
    {
        bool again;
        lock (_gate)
        {
            again = _state.TryGetValue(batchId, out var state) && state == BatchState.RunningNeedsFollowUp;
            _state.Remove(batchId);
        }
        if (again)
            Enqueue(batchId);
    }

    /// <summary>True while the batch is queued or being processed. Exposed for tests.</summary>
    public bool IsActive(long batchId)
    {
        lock (_gate)
            return _state.ContainsKey(batchId);
    }

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
