using System.Threading.Channels;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>In-process work queue of RequestIds awaiting a rule-engine + AI-synthesis pass. Known, stated
/// gap (mirrors FilingProcessingQueue): does not survive an app restart mid-run on its own —
/// AnalysisWorker's startup recovery sweep is what covers a crash/restart.</summary>
public class AnalysisQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public void Enqueue(long requestId) => _channel.Writer.TryWrite(requestId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
