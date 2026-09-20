using System.Threading.Channels;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>In-process work queue of <see cref="Data.Entities.LitigationOrderDocument"/> ids awaiting PDF
/// retrieval — mirrors <see cref="LitigationCasePersistenceQueue"/>'s own shape and reasoning exactly. Does
/// not survive a restart on its own — <see cref="LitigationOrderDocumentService.RecoverStaleWorkAsync"/>'s
/// startup sweep re-enqueues every non-terminal document.</summary>
public sealed class LitigationOrderDocumentQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long orderDocumentId) => _channel.Writer.TryWrite(orderDocumentId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
