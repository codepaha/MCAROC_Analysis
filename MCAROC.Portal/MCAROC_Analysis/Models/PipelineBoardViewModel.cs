using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public sealed class PipelineBoardViewModel
{
    public bool CoordinatorEnabled { get; init; }
    /// <summary><c>Off</c>, <c>Observe</c> or <c>Enforce</c>.</summary>
    public string Mode { get; init; } = "Off";
    public PipelineOutcome? Outcome { get; init; }
    public int? StuckMinutes { get; init; }
    public string? ReasonCode { get; init; }
    public IReadOnlyList<PipelineBoardRow> Rows { get; init; } = [];
    public IReadOnlyDictionary<PipelineOutcome, int> Counts { get; init; } = new Dictionary<PipelineOutcome, int>();
}

public sealed class PipelineBoardRow
{
    public long PipelineRunId { get; init; }
    public long RequestId { get; init; }
    public string RequestNumber { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public PipelineRunTrigger Trigger { get; init; }
    public PipelineOutcome Outcome { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? CoreReadyUtc { get; init; }
    /// <summary>When any stage last changed — the basis for "stuck for more than N minutes".</summary>
    public DateTime LastChangeUtc { get; init; }
    public IReadOnlyList<PipelineStageStatusDto> Stages { get; init; } = [];
}

public sealed record PipelineStageStatusDto(
    string Stage, string State, string? SkipKind, string? ReasonCode, string? ReasonDetail, long? SourceRef, DateTime UpdatedUtc);
