namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Deliverable 4: combines Deliverable 2's (<see cref="CoastalChargeLinkService"/>) and
/// Deliverable 3's (<see cref="CoastalFinancialLinkService"/>) already-computed results into one
/// unified per-document view. Pure join/reconciliation over the two passes' outputs — never
/// recomputes either one, per the issue's own scoping ("D4's job is to combine them into one
/// reconciled view, not to recompute either pass").
/// </summary>
public static class CoastalPilotReconciliationService
{
    public static CoastalPilotReconciliationResult Execute(
        CoastalChargeLinkResult chargeResult,
        CoastalFinancialLinkResult financialResult)
    {
        ArgumentNullException.ThrowIfNull(chargeResult);
        ArgumentNullException.ThrowIfNull(financialResult);

        var financialByKey = financialResult.Entries.ToDictionary(
            e => (e.OuterEntryFullPath, e.NestedEntryRelativePath));

        var entries = new List<CoastalPilotReconciliationEntry>(chargeResult.Entries.Count);
        var collisions = new List<CoastalPilotReconciliationEntry>();

        foreach (var chargeEntry in chargeResult.Entries)
        {
            var key = (chargeEntry.OuterEntryFullPath, chargeEntry.NestedEntryRelativePath);
            if (!financialByKey.TryGetValue(key, out var financialEntry))
                throw new InvalidOperationException(
                    "D3 financial-link result has no entry for a D2 manifest entry: " +
                    $"{chargeEntry.OuterEntryFullPath} / {chargeEntry.NestedEntryRelativePath}. " +
                    "D4 requires both passes to cover the identical manifest.");

            var unified = Classify(chargeEntry.Outcome, financialEntry.Outcome);

            var entry = new CoastalPilotReconciliationEntry
            {
                OuterEntryFullPath = chargeEntry.OuterEntryFullPath,
                NestedEntryRelativePath = chargeEntry.NestedEntryRelativePath,
                Sha256Hex = chargeEntry.Sha256Hex,
                UnifiedOutcome = unified,
                ChargeOutcome = chargeEntry.Outcome,
                ChargeReason = chargeEntry.Reason,
                FinancialOutcome = financialEntry.Outcome,
                FinancialReason = financialEntry.Reason
            };
            entries.Add(entry);

            if (chargeEntry.Outcome == PilotLinkOutcome.AutoAccepted
                && financialEntry.Outcome == PilotFinancialLinkOutcome.AutoAccepted)
                collisions.Add(entry);
        }

        // Symmetry check: D3 must not carry an entry absent from D2's manifest — both passes are
        // supposed to run over the identical 814-entry corpus.
        var chargeKeys = chargeResult.Entries
            .Select(e => (e.OuterEntryFullPath, e.NestedEntryRelativePath))
            .ToHashSet();
        foreach (var financialEntry in financialResult.Entries)
        {
            var key = (financialEntry.OuterEntryFullPath, financialEntry.NestedEntryRelativePath);
            if (!chargeKeys.Contains(key))
                throw new InvalidOperationException(
                    "D2 charge-link result has no entry for a D3 manifest entry: " +
                    $"{financialEntry.OuterEntryFullPath} / {financialEntry.NestedEntryRelativePath}.");
        }

        return new CoastalPilotReconciliationResult(entries, collisions);
    }

    /// <summary>Priority order, most specific/actionable first. Every branch falls through to
    /// <see cref="PilotUnifiedOutcome.UnlinkedOutOfScope"/>, so every entry gets exactly one bucket
    /// by construction — the 814-entry total invariant holds automatically, it doesn't need a
    /// separate exhaustiveness check.</summary>
    private static PilotUnifiedOutcome Classify(PilotLinkOutcome charge, PilotFinancialLinkOutcome financial)
    {
        if (charge == PilotLinkOutcome.ManifestDuplicateBypassed
            && financial == PilotFinancialLinkOutcome.ManifestDuplicateBypassed)
            return PilotUnifiedOutcome.Duplicate;

        if (charge == PilotLinkOutcome.AutoAccepted) return PilotUnifiedOutcome.LinkedAsCharge;
        if (financial == PilotFinancialLinkOutcome.AutoAccepted) return PilotUnifiedOutcome.LinkedAsFinancial;

        if (charge == PilotLinkOutcome.PendingReview || financial == PilotFinancialLinkOutcome.PendingReview)
            return PilotUnifiedOutcome.PendingReview;

        if (charge == PilotLinkOutcome.UnlinkedNoCandidate || financial == PilotFinancialLinkOutcome.UnlinkedNoCandidate)
            return PilotUnifiedOutcome.UnlinkedNoCandidate;

        return PilotUnifiedOutcome.UnlinkedOutOfScope;
    }
}
