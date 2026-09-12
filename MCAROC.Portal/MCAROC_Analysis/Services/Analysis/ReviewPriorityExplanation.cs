using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>ReviewPriorityCalculator.Explain's result — the same ReviewPriority Calculate would have
/// produced from the same underlying findings, plus every condition that fired, in a fixed evaluation
/// order (never re-sorted by "importance"). Computed fresh at view time from persisted AnalysisFinding
/// rows, never stored — mechanically impossible for the AI synthesis path to supply or alter.</summary>
public sealed record ReviewPriorityExplanation(ReviewPriority Priority, IReadOnlyList<ReviewPriorityReason> Reasons)
{
    /// <summary>First reason in evaluation order, for a concise one-line badge. Null only when Priority is
    /// Low, since Low has no escalating condition to name.</summary>
    public ReviewPriorityReason? PrimaryReason => Reasons.Count > 0 ? Reasons[0] : null;

    /// <summary>e.g. "2+ Critical findings across multiple domains" or, when 2+ reasons fired, joined with
    /// " + " — never silently drops a reason down to just the primary one.</summary>
    public string DescribeAll() => Reasons.Count > 0
        ? string.Join(" + ", Reasons.Select(r => r.Describe()))
        : "no escalating condition";
}
