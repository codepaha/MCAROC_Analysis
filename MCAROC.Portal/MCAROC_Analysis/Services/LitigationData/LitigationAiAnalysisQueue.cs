using System.Threading.Channels;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Wake-up queue only; authoritative work state is LitigationAiAnalysisRuns, so startup recovery
/// re-enqueues durable Pending/expired-InProgress rows after any process restart.</summary>
public sealed class LitigationAiAnalysisQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long runId) => _channel.Writer.TryWrite(runId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
