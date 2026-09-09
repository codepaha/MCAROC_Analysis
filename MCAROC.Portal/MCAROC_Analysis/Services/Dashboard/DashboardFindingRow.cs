using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>Flat projection of one AnalysisFinding plus its owning request's dedup key — the shape every
/// pure in-memory dashboard aggregator (TopRiskIndicatorBuilder, KeyFindingSelector) operates on, so they're
/// testable without a database.</summary>
public record DashboardFindingRow(
    long RequestId,
    string EntityKey,
    string Code,
    string Title,
    FindingSection Section,
    FindingSeverity Severity,
    TemporalStatus TemporalStatus,
    int DisplayPriority,
    DateOnly? ObservationDate);
