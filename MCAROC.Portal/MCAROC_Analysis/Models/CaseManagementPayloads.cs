namespace MCAROC_Analysis.Models;

public sealed record SingleCaseEditPayload(
    string? Status,
    string? CaseStage,
    bool IsExcludedFromReport,
    string? ExclusionReason,
    string? AnalystNote);

public sealed record BatchUpdateFilter(
    string? Category = null,
    string? Direction = null,
    string? StatusBucket = null,
    string? SearchText = null);

public sealed record BatchUpdateCasesPayload(
    IReadOnlyList<string> CaseRowIds,
    bool SelectAllMatchingFilter,
    BatchUpdateFilter? Filter,
    string Operation, // "Exclude" | "Include" | "UpdateStatus"
    string? ExclusionReason,
    string? NewStatus,
    string? AnalystNote,
    int SubmittedDataVersion);

public sealed record BatchUpdateResult
{
    public int UpdatedCount { get; init; }
    public int SkippedConcludedCount { get; init; }
    public int NewDataVersion { get; init; }
    public string Message { get; init; } = string.Empty;
}

