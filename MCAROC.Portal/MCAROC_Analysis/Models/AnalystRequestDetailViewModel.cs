using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public sealed class AnalystRequestDetailViewModel
{
    public long RequestId { get; init; }
    public string RequestNumber { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public EntityType EntityType { get; init; }
    public string? Cin { get; init; }
    public string? Llpin { get; init; }
    public string? Pan { get; init; }
    public RequestStatus Status { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? UpdatedUtc { get; init; }
    public bool NeedsReview { get; init; }
    public string? AttentionReason { get; init; }
    public IReadOnlyList<AnalystRequestDocumentViewModel> Documents { get; init; } = [];
}

public sealed class AnalystRequestDocumentViewModel
{
    public long DocumentId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public DocumentType Type { get; init; }
    public DocumentUploadStatus Status { get; init; }
    public DateTime UploadedUtc { get; init; }
}
