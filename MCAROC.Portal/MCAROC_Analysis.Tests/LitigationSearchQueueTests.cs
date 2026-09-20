using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationSearchQueueTests
{
    [Fact]
    public async Task Enqueue_then_ReadAllAsync_yields_the_same_id()
    {
        var queue = new LitigationSearchQueue();
        queue.Enqueue(42L);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var id in queue.ReadAllAsync(cts.Token))
        {
            Assert.Equal(42L, id);
            return;
        }

        Assert.Fail("Expected exactly one item from the queue.");
    }

    [Fact]
    public async Task Multiple_enqueues_are_each_delivered_in_order()
    {
        var queue = new LitigationSearchQueue();
        queue.Enqueue(1L);
        queue.Enqueue(2L);
        queue.Enqueue(3L);

        var received = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var id in queue.ReadAllAsync(cts.Token))
            {
                received.Add(id);
                if (received.Count == 3) break;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal([1L, 2L, 3L], received);
    }
}
