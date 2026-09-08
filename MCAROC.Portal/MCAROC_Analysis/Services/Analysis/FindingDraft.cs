using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Pre-persistence finding, produced by a domain rule (or a deterministic/AI cross-section rule).
/// RuleEngine turns the final set of these into AnalysisFinding rows after consolidation and cross-section
/// evaluation. ConsolidationGroup is transient (FindingConsolidator-only) — it does not persist as a column;
/// once merged, a finding is just a finding.</summary>
public record FindingDraft(
    FindingSection Section,
    FindingSeverity Severity,
    TemporalStatus TemporalStatus,
    string Code,
    string Title,
    string SummaryText,
    string? MetricsJson = null,
    IReadOnlyList<string>? SupportingSignalCodes = null,
    string? SourceReferenceJson = null,
    string? PeriodLabel = null,
    DateOnly? ObservationDate = null,
    int DisplayPriority = 0,
    string? ConsolidationGroup = null)
{
    public string? SupportingSignalsJson => SupportingSignalCodes is { Count: > 0 }
        ? System.Text.Json.JsonSerializer.Serialize(SupportingSignalCodes)
        : null;
}
