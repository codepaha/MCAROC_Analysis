using MCAROC_Analysis.Services.Chat;

namespace MCAROC_Analysis.Tests;

public class DocumentChunkingQueueTests
{
    private static async Task<List<long>> DrainAsync(DocumentChunkingQueue queue, int quietMs = 200, Action<long>? onEach = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(quietMs));
        var received = new List<long>();
        try
        {
            await foreach (var id in queue.ReadAllAsync(cts.Token))
            {
                received.Add(id);
                onEach?.Invoke(id);
            }
        }
        catch (OperationCanceledException) { }
        return received;
    }

    [Fact]
    public async Task Enqueue_coalesces_duplicate_batchIds_still_waiting()
    {
        var queue = new DocumentChunkingQueue();
        for (var i = 0; i < 5; i++)
            queue.Enqueue(42L);

        var received = await DrainAsync(queue);

        Assert.Equal(new[] { 42L }, received);
    }

    [Fact]
    public async Task Enqueue_is_allowed_again_once_the_batchId_has_been_handed_out()
    {
        var queue = new DocumentChunkingQueue();
        queue.Enqueue(7L);
        queue.Enqueue(7L);

        var reEnqueued = false;
        var received = await DrainAsync(queue, quietMs: 300, onEach: _ =>
        {
            if (reEnqueued) return;
            reEnqueued = true;
            queue.Enqueue(7L); // a failure discovered during this pass re-enqueues for one more
        });

        Assert.Equal(new[] { 7L, 7L }, received);
    }
}
