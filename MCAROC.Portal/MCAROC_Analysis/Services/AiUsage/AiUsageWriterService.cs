using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>Saves queued usage rows in batches, retrying a failed save with a growing delay. A row the database still refuses after the last retry is
/// counted as failed on the queue (and logged) instead of vanishing quietly. On a normal shutdown the queue is closed and everything already queued is
/// saved before the service stops, so a restart does not cost the calls just made; a hard kill still can, which is why the report also says so.</summary>
public sealed class AiUsageWriterService(AiUsageQueue queue, IServiceScopeFactory scopes, ILogger<AiUsageWriterService> logger) : BackgroundService
{
    private const int MaxBatch = 50;
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not the stopping token: the loop ends when the queue is closed and empty, which StopAsync arranges, so queued rows are drained rather than dropped.
        while (await queue.Reader.WaitToReadAsync(CancellationToken.None))
        {
            var batch = new List<AiUsageRecord>(MaxBatch);
            while (batch.Count < MaxBatch && queue.Reader.TryRead(out var record)) batch.Add(record);
            if (batch.Count > 0) await SaveAsync(queue, batch, PersistAsync, RetryDelays, logger);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        await base.StopAsync(cancellationToken);
    }

    private async Task PersistAsync(IReadOnlyList<AiUsageRecord> batch)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AiUsageRecords.AddRange(batch);
        await db.SaveChangesAsync();
    }

    /// <summary>Saves one batch, retrying after each delay; counts it as saved or, when every attempt failed, as failed.</summary>
    internal static async Task SaveAsync(
        AiUsageQueue queue, IReadOnlyList<AiUsageRecord> batch, Func<IReadOnlyList<AiUsageRecord>, Task> persist, TimeSpan[] delays, ILogger logger)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await persist(batch);
                queue.MarkSaved(batch.Count);
                return;
            }
            catch (Exception ex) when (attempt < delays.Length)
            {
                logger.LogWarning(ex, "Could not save {Count} AI usage record(s); retrying", batch.Count);
                await Task.Delay(delays[attempt]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Gave up saving {Count} AI usage record(s); the cost report will be incomplete", batch.Count);
                queue.MarkFailed(batch.Count);
                return;
            }
        }
    }
}
