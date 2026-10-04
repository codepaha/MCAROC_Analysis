namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Runtime configuration for litigation order chunking and background re-embedding migration.</summary>
public record LitigationChunkingOptions
{
    public int MaxConcurrentChunking { get; init; } = 4;
    public int OutdatedBatchSize { get; init; } = 100;
    public int QueueBackpressureLimit { get; init; } = 20;
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan BatchThrottleInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public long? RequestId { get; init; }
}
