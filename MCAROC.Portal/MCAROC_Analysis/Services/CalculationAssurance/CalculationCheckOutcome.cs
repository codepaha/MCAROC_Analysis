using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>One deterministic check's verdict — mirrors RuleEvaluationOutcome's Triggered/NotTriggered/
/// NotEvaluated shape deliberately, but lives in this separate, new folder rather than touching the rule
/// engine itself (off-limits to this lane per the working agreement). NotEvaluated is never a passing
/// result and is never conflated with NotTriggered — a check that could not run says so explicitly.</summary>
public sealed class CalculationCheckOutcome
{
    public string CheckKey { get; }
    public CalculationCheckStatus Status { get; }

    /// <summary>Set only when Status == Triggered.</summary>
    public CalculationDiscrepancySeverity? Severity { get; }

    public string? DetailJson { get; }

    /// <summary>The CalculationLedgerEntry ids this outcome's evidence is grounded in. A Triggered
    /// outcome must carry at least one — this is what CalculationDiscrepancy.PrimaryLedgerEntryId anchors
    /// to when the outcome auto-creates a discrepancy.</summary>
    public IReadOnlyList<long> RelatedLedgerEntryIds { get; }

    public string? NotEvaluatedReason { get; }

    private CalculationCheckOutcome(
        string checkKey, CalculationCheckStatus status, CalculationDiscrepancySeverity? severity,
        string? detailJson, IReadOnlyList<long> relatedLedgerEntryIds, string? notEvaluatedReason)
    {
        CheckKey = checkKey;
        Status = status;
        Severity = severity;
        DetailJson = detailJson;
        RelatedLedgerEntryIds = relatedLedgerEntryIds;
        NotEvaluatedReason = notEvaluatedReason;
    }

    public static CalculationCheckOutcome Triggered(
        string checkKey, CalculationDiscrepancySeverity severity, string detailJson, IReadOnlyList<long> relatedLedgerEntryIds)
    {
        if (relatedLedgerEntryIds.Count == 0)
            throw new ArgumentException("A Triggered outcome must carry at least one related ledger entry id.", nameof(relatedLedgerEntryIds));
        return new(checkKey, CalculationCheckStatus.Triggered, severity, detailJson, relatedLedgerEntryIds, null);
    }

    public static CalculationCheckOutcome NotTriggered(string checkKey, IReadOnlyList<long> relatedLedgerEntryIds) =>
        new(checkKey, CalculationCheckStatus.NotTriggered, null, null, relatedLedgerEntryIds, null);

    public static CalculationCheckOutcome NotEvaluated(string checkKey, string reason, IReadOnlyList<long>? relatedLedgerEntryIds = null) =>
        new(checkKey, CalculationCheckStatus.NotEvaluated, null, null, relatedLedgerEntryIds ?? [], reason);
}
