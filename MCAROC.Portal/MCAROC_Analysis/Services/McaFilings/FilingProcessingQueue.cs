namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>A unit of MCA Filings work. <see cref="Lane"/> is the batch it belongs to: work is handed out fairly across lanes, so one
/// company's 500 documents never make the next company's wait for all of them (0 = no particular batch).</summary>
public abstract record FilingWorkItem
{
    public long Lane { get; init; }
}
public record UnpackBatchWorkItem(long BatchId, int RetryCount = 0) : FilingWorkItem;
public record ProcessDocumentWorkItem(long FilingDocumentId) : FilingWorkItem;
public record ExtractFilingWorkItem(long FilingId) : FilingWorkItem;

public enum FilingWorkKind { Unpack, Document, Extraction }

/// <summary>In-process work queue for the MCA Filings pipeline: one queue per kind of work (unpack, OCR/classification, AI extraction), each
/// handing out work <b>round-robin across batches</b>. Requests processed side by side each make progress from the start instead of queueing
/// behind whichever batch unpacked first (owner, 2026-10-13: 10-20 requests a day now, 100 later).
/// Known, stated gap (per plan): this does not survive an app restart mid-batch on its own — FilingProcessingWorker's startup recovery sweep
/// (re-enqueuing stale non-terminal rows) is what covers a crash/restart, not this queue itself.</summary>
public class FilingProcessingQueue
{
    private readonly Dictionary<FilingWorkKind, FairLanes> _queues = new()
    {
        [FilingWorkKind.Unpack] = new(), [FilingWorkKind.Document] = new(), [FilingWorkKind.Extraction] = new()
    };

    public static FilingWorkKind KindOf(FilingWorkItem item) => item switch
    {
        UnpackBatchWorkItem => FilingWorkKind.Unpack,
        ExtractFilingWorkItem => FilingWorkKind.Extraction,
        _ => FilingWorkKind.Document
    };

    public void Enqueue(FilingWorkItem item)
    {
        // An unpack is its own batch's lane.
        if (item is UnpackBatchWorkItem u && item.Lane == 0) item = u with { Lane = u.BatchId };
        _queues[KindOf(item)].Enqueue(item);
    }

    public void EnqueueDelayed(FilingWorkItem item, TimeSpan delay, CancellationToken ct = default)
    {
        if (delay <= TimeSpan.Zero)
        {
            Enqueue(item);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                Enqueue(item);
            }
            catch (OperationCanceledException)
            {
                // Host/worker shutdown cancelled delayed enqueue
            }
        }, ct);
    }

    /// <summary>The next item of <paramref name="kind"/>, taken from the next batch in rotation. Waits while there is none.</summary>
    public Task<FilingWorkItem> DequeueAsync(FilingWorkKind kind, CancellationToken ct) => _queues[kind].DequeueAsync(ct);

    public int Count(FilingWorkKind kind) => _queues[kind].Count;

    /// <summary>Per-batch FIFO queues served round-robin: each dequeue takes one item from the batch whose turn it is, then moves that batch to
    /// the back of the rotation if it still has work.</summary>
    private sealed class FairLanes
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, Queue<FilingWorkItem>> _lanes = new();
        private readonly LinkedList<long> _rotation = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _count;

        public int Count { get { lock (_sync) return _count; } }

        public void Enqueue(FilingWorkItem item)
        {
            lock (_sync)
            {
                if (!_lanes.TryGetValue(item.Lane, out var lane))
                {
                    lane = new Queue<FilingWorkItem>();
                    _lanes[item.Lane] = lane;
                    _rotation.AddLast(item.Lane);
                }
                lane.Enqueue(item);
                _count++;
            }
            _available.Release();
        }

        public async Task<FilingWorkItem> DequeueAsync(CancellationToken ct)
        {
            await _available.WaitAsync(ct);
            lock (_sync)
            {
                var laneKey = _rotation.First!.Value;
                _rotation.RemoveFirst();
                var lane = _lanes[laneKey];
                var item = lane.Dequeue();
                _count--;
                if (lane.Count > 0) _rotation.AddLast(laneKey);
                else _lanes.Remove(laneKey);
                return item;
            }
        }
    }
}
