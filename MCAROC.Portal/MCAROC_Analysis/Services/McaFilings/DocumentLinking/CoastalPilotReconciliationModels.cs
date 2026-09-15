using System.Text;
using System.Text.Json;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Deliverable 4's unified per-document classification, combining Deliverable 2 (charge-link) and
/// Deliverable 3 (financial-link) outcomes over the same 814-entry Coastal manifest.
/// </summary>
public enum PilotUnifiedOutcome
{
    Duplicate,
    LinkedAsCharge,
    LinkedAsFinancial,
    PendingReview,
    UnlinkedNoCandidate,
    UnlinkedOutOfScope
}

/// <summary>
/// One manifest entry's reconciled classification, carrying both domain passes' own outcome/reason
/// alongside the unified bucket so a reviewer can see why it landed there.
/// </summary>
public sealed record CoastalPilotReconciliationEntry
{
    public required string OuterEntryFullPath { get; init; }
    public required string NestedEntryRelativePath { get; init; }
    public required string Sha256Hex { get; init; }
    public required PilotUnifiedOutcome UnifiedOutcome { get; init; }
    public required PilotLinkOutcome ChargeOutcome { get; init; }
    public required PilotLinkReason ChargeReason { get; init; }
    public required PilotFinancialLinkOutcome FinancialOutcome { get; init; }
    public required PilotFinancialLinkReason FinancialReason { get; init; }
}

/// <summary>
/// Result of reconciling a Deliverable 2 charge-link result and a Deliverable 3 financial-link
/// result into one unified view. <see cref="CrossDomainCollisions"/> must be empty — a document
/// accepted as both a charge link AND a financial link would mean one of the two deterministic
/// passes disagrees with the other about what kind of document it is; D4's whole purpose is making
/// that provable, not assumed.
/// </summary>
public sealed record CoastalPilotReconciliationResult(
    IReadOnlyList<CoastalPilotReconciliationEntry> Entries,
    IReadOnlyList<CoastalPilotReconciliationEntry> CrossDomainCollisions)
{
    public int TotalPdfs => Entries.Count;
    public bool HasCrossDomainCollisions => CrossDomainCollisions.Count > 0;

    public int DuplicateCount => Entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.Duplicate);
    public int LinkedAsChargeCount => Entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.LinkedAsCharge);
    public int LinkedAsFinancialCount => Entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.LinkedAsFinancial);
    public int PendingReviewCount => Entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.PendingReview);
    public int UnlinkedNoCandidateCount => Entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.UnlinkedNoCandidate);
    public int UnlinkedOutOfScopeCount => Entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.UnlinkedOutOfScope);

    /// <summary>The committed-fixture JSON shape — a flat array, matching coastal_d2_baseline.json /
    /// coastal_d3_baseline.json's convention exactly, so the same baseline-comparison test pattern applies.</summary>
    public string ToJson() => JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Coastal Pilot Reconciliation Report (Deliverable 4)");
        sb.AppendLine();
        sb.AppendLine($"Total PDFs: {TotalPdfs}");
        sb.AppendLine($"Cross-domain collisions: {CrossDomainCollisions.Count}");
        sb.AppendLine();
        sb.AppendLine("## By outcome");
        sb.AppendLine();
        sb.AppendLine("| Outcome | Count |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Duplicate | {DuplicateCount} |");
        sb.AppendLine($"| LinkedAsCharge | {LinkedAsChargeCount} |");
        sb.AppendLine($"| LinkedAsFinancial | {LinkedAsFinancialCount} |");
        sb.AppendLine($"| PendingReview | {PendingReviewCount} |");
        sb.AppendLine($"| UnlinkedNoCandidate | {UnlinkedNoCandidateCount} |");
        sb.AppendLine($"| UnlinkedOutOfScope | {UnlinkedOutOfScopeCount} |");

        var pendingCharge = Entries.Where(e => e.ChargeOutcome == PilotLinkOutcome.PendingReview).ToList();
        var pendingFinancial = Entries.Where(e => e.FinancialOutcome == PilotFinancialLinkOutcome.PendingReview).ToList();
        if (pendingCharge.Count > 0 || pendingFinancial.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## PendingReview — by domain reason");
            sb.AppendLine();
            sb.AppendLine("| Domain | Reason | Count |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in pendingCharge.GroupBy(e => e.ChargeReason).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
                sb.AppendLine($"| Charge | {g.Key} | {g.Count()} |");
            foreach (var g in pendingFinancial.GroupBy(e => e.FinancialReason).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
                sb.AppendLine($"| Financial | {g.Key} | {g.Count()} |");
        }

        return sb.ToString();
    }
}
