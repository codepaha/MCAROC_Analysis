using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models.CalculationAssurance;

/// <summary>Read model for the #164 internal calculation-audit review page. Assembled once per request in
/// CalculationAuditController.Index — no business logic here, purely a shape for the view.</summary>
public record CalculationAuditPageModel(
    long RequestId,
    string RequestNumber,
    string CompanyName,
    IReadOnlyList<SnapshotSummary> Snapshots,
    SnapshotSummary? Selected,
    IReadOnlyList<CalculationLedgerEntry> LedgerEntries,
    IReadOnlyList<CalculationCheckResult> CheckResults,
    IReadOnlyList<DiscrepancyRow> Discrepancies,
    CalculationAiAuditRun? AiAuditRun);

/// <summary>One audited (Request, IngestionRun, AnalysisRun) tuple. Old snapshots stay fully browsable —
/// nothing here is ever deleted — IsCurrent marks the one DossierCache/the delivery gate would actually
/// resolve to for a live download today.</summary>
public record SnapshotSummary(long CalculationAuditSnapshotId, long IngestionRunId, long AnalysisRunId, DateTime CreatedUtc, bool IsCurrent);

public record DiscrepancyRow(
    CalculationDiscrepancy Discrepancy,
    CalculationLedgerEntry? PrimaryLedgerEntry,
    IReadOnlyList<CalculationDiscrepancyApproval> Approvals,
    bool HasActiveHold);
