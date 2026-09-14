using System.Threading.Channels;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>In-process work queue of CalculationAiAuditRunIds awaiting a Vertex AI second-line review
/// pass. Mirrors AnalysisQueue exactly, including its known gap: an item sitting in this unbounded,
/// in-memory channel does not survive an app restart on its own — CalculationAiAuditOrchestrator's
/// startup recovery sweep re-enqueues every Pending/InProgress row from the database instead.</summary>
public class CalculationAiAuditQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public void Enqueue(long calculationAiAuditRunId) => _channel.Writer.TryWrite(calculationAiAuditRunId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
