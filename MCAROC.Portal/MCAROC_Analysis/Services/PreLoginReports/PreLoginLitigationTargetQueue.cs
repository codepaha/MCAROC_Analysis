using System.Threading.Channels;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class PreLoginLitigationTargetQueue
{
    private readonly Channel<long> channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = false });

    public void Enqueue(long targetId) => channel.Writer.TryWrite(targetId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);
}

