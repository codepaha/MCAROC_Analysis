using System.Threading.Channels;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>The hand-off between the model clients and the writer that saves usage rows. Recording a call is a queue insert, so it never waits on the
/// database; <see cref="AiUsageWriterService"/> saves what is queued, retries, and on shutdown finishes what is still waiting. What cannot be saved is
/// counted here, so the cost report can say its totals may be low rather than looking complete.</summary>
public sealed class AiUsageQueue
{
    private readonly Channel<AiUsageRecord> _channel = Channel.CreateUnbounded<AiUsageRecord>(new UnboundedChannelOptions { SingleReader = true });
    private int _pending;
    private long _failed;

    public ChannelReader<AiUsageRecord> Reader => _channel.Reader;

    /// <summary>Records queued or being saved right now.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Records given up on since the app started: the database refused them through every retry, or the queue had already closed.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    public void Enqueue(AiUsageRecord record)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(record)) return;
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _failed);
    }

    internal void MarkSaved(int count) => Interlocked.Add(ref _pending, -count);

    internal void MarkFailed(int count)
    {
        Interlocked.Add(ref _pending, -count);
        Interlocked.Add(ref _failed, count);
    }

    /// <summary>No more records will be accepted; the writer drains what is queued and stops.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
