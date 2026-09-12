using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>The 4 fields ReviewPriorityCalculator's evaluator actually reads — a minimal projection so
/// both the pre-persist FindingDraft and the post-persist AnalysisFinding shapes can feed the exact same
/// evaluation logic (Calculate/Explain), instead of two hand-written copies of the same branching that
/// could silently drift apart.</summary>
public readonly record struct FindingSignal(FindingSection Section, FindingSeverity Severity, TemporalStatus TemporalStatus, string Code)
{
    public static FindingSignal From(FindingDraft draft) => new(draft.Section, draft.Severity, draft.TemporalStatus, draft.Code);
    public static FindingSignal From(AnalysisFinding finding) => new(finding.Section, finding.Severity, finding.TemporalStatus, finding.Code);
}
