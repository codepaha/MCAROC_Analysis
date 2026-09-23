using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public sealed class AnalystDashboardViewModel
{
    public string AnalystName { get; init; } = string.Empty;
    public int AssignedCount { get; init; }
    public int AwaitingUploadCount { get; init; }
    public int ProcessingCount { get; init; }
    public int ReviewNeededCount { get; init; }
    public int ReadyCount { get; init; }
    public int FailedOrAttentionCount { get; init; }
    public string? Search { get; init; }
    public RequestStatus? Status { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalFilteredCount { get; init; }
    public IReadOnlyList<AnalystQueueItemViewModel> Requests { get; init; } = [];
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalFilteredCount / (double)PageSize));
}

public sealed class AnalystQueueItemViewModel
{
    public long RequestId { get; init; }
    public string RequestNumber { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string? Cin { get; init; }
    public string? Llpin { get; init; }
    public EntityType EntityType { get; init; }
    public RequestStatus Status { get; init; }
    public DateTime CreatedUtc { get; init; }
    public bool NeedsReview { get; init; }
    public string? AttentionReason { get; init; }
}
