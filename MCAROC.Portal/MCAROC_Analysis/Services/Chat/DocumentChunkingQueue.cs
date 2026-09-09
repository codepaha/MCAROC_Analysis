using System.Threading.Channels;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>In-process work queue of BatchIds awaiting chunking/embedding. Mirrors FilingProcessingQueue —
/// does not survive an app restart mid-run on its own; DocumentChunkingWorker's startup recovery sweep
/// covers a crash/restart.</summary>
public class DocumentChunkingQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public void Enqueue(long batchId) => _channel.Writer.TryWrite(batchId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
