using System.Threading.Channels;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>In-process work queue of <see cref="Data.Entities.LitigationOrderDocument"/> ids awaiting
/// chunking/embedding (#244/LIT-04). Deliberately the simple per-document shape — mirrors
/// <see cref="LitigationOrderDocumentQueue"/>, not <c>Services.Chat.DocumentChunkingQueue</c>'s batch-level
/// coalescing: that queue's extra complexity solves a problem specific to MCA filings (many documents in one
/// upload batch finishing chunking-eligibility in quick succession, which would otherwise trigger overlapping
/// full-batch rescans). Litigation orders have no equivalent batch concept — each order document becomes
/// chunking-eligible independently, exactly once, right after its own download publishes — so there is
/// nothing to coalesce. Does not survive a restart on its own —
/// <c>LitigationOrderChunkingOrchestrator.RecoverStaleWorkAsync</c>'s startup sweep re-enqueues every
/// non-terminal document.</summary>
public sealed class LitigationOrderChunkingQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long orderDocumentId) => _channel.Writer.TryWrite(orderDocumentId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
