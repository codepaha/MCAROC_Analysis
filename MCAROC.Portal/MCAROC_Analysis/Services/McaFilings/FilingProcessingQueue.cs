using System.Threading.Channels;

namespace MCAROC_Analysis.Services.McaFilings;

public abstract record FilingWorkItem;
public record UnpackBatchWorkItem(long BatchId) : FilingWorkItem;
public record ProcessDocumentWorkItem(long FilingDocumentId) : FilingWorkItem;
public record ExtractFilingWorkItem(long FilingId) : FilingWorkItem;

/// <summary>In-process work queue for the MCA Filings pipeline. Known, stated gap (per plan): this does
/// not survive an app restart mid-batch on its own — FilingProcessingWorker's startup recovery sweep
/// (re-enqueuing stale non-terminal rows) is what covers a crash/restart, not this queue itself.</summary>
public class FilingProcessingQueue
{
    private readonly Channel<FilingWorkItem> _channel = Channel.CreateUnbounded<FilingWorkItem>();

    public void Enqueue(FilingWorkItem item) => _channel.Writer.TryWrite(item);

    public IAsyncEnumerable<FilingWorkItem> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
