using System.Threading.Channels;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class PreLoginReportQueue
{
    private readonly Channel<long> channel = Channel.CreateUnbounded<long>();
    public void Enqueue(long jobId) => channel.Writer.TryWrite(jobId);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken) => channel.Reader.ReadAllAsync(cancellationToken);
}
