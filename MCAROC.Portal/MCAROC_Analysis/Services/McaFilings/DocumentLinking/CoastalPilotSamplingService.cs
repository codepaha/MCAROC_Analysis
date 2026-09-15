using System.Text.Json;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Deliverable 5: assembles the auditable reviewer sample from Deliverable 2's (charge) and
/// Deliverable 3's (financial) already-computed results — never recomputes either pass, matching
/// D4's discipline. Every selection is deterministic: same input, same sample, always (ordered by
/// path, round-robin across strata — no randomness anywhere).
/// </summary>
public static class CoastalPilotSamplingService
{
    public const int MinAutoAcceptedSample = 20;
    public const int MinUnlinkedSample = 20;

    public static CoastalPilotSamplingResult Execute(
        CoastalChargeLinkResult chargeResult,
        CoastalFinancialLinkResult financialResult)
    {
        ArgumentNullException.ThrowIfNull(chargeResult);
        ArgumentNullException.ThrowIfNull(financialResult);

        var records = new List<CoastalPilotSampleRecord>();

        // 1. Hard floor, not a sample: 100% of every PendingReview item, both domains.
        records.AddRange(Ordered(chargeResult.Entries.Where(e => e.Outcome == PilotLinkOutcome.PendingReview))
            .Select(e => BuildChargeRecord(e, $"Charge:{e.Outcome}:{e.Reason}")));
        records.AddRange(Ordered(financialResult.Entries.Where(e => e.Outcome == PilotFinancialLinkOutcome.PendingReview))
            .Select(e => BuildFinancialRecord(e, $"Financial:{e.Outcome}:{e.Reason}")));

        // 2. >=20 AutoAccepted, stratified by charge match mode (the Reason values already ARE the
        // match modes: ExactMatchBothDates/EventDate/FilingDate) and financial basis (Standalone/Consolidated).
        // The stratum key baked into each sample record reflects the ACTUAL grouping used here, not a
        // generic Outcome:Reason label — financial's Reason doesn't vary by basis, so a reviewer needs
        // the basis-based key to see why a given Standalone/Consolidated split was sampled the way it was.
        var chargeAutoPool = Ordered(chargeResult.Entries.Where(e => e.Outcome == PilotLinkOutcome.AutoAccepted))
            .Select(e => (StratumKey: $"Charge:{e.Reason}", Item: e, e.OuterEntryFullPath, e.NestedEntryRelativePath));
        var financialAutoPool = Ordered(financialResult.Entries.Where(e => e.Outcome == PilotFinancialLinkOutcome.AutoAccepted))
            .Select(e => (StratumKey: $"Financial:{e.MatchedBasis}", Item: e, e.OuterEntryFullPath, e.NestedEntryRelativePath));

        records.AddRange(TakeStratifiedRoundRobin(chargeAutoPool, financialAutoPool, MinAutoAcceptedSample, BuildChargeRecord, BuildFinancialRecord));

        // 3. >=20 Unlinked* (out-of-scope + no-candidate), stratified by domain + outcome + reason.
        var chargeUnlinkedPool = Ordered(chargeResult.Entries.Where(e =>
                e.Outcome is PilotLinkOutcome.UnlinkedNoCandidate or PilotLinkOutcome.UnlinkedOutOfScope))
            .Select(e => (StratumKey: $"Charge:{e.Outcome}:{e.Reason}", Item: e, e.OuterEntryFullPath, e.NestedEntryRelativePath));
        var financialUnlinkedPool = Ordered(financialResult.Entries.Where(e =>
                e.Outcome is PilotFinancialLinkOutcome.UnlinkedNoCandidate or PilotFinancialLinkOutcome.UnlinkedOutOfScope))
            .Select(e => (StratumKey: $"Financial:{e.Outcome}:{e.Reason}", Item: e, e.OuterEntryFullPath, e.NestedEntryRelativePath));

        records.AddRange(TakeStratifiedRoundRobin(chargeUnlinkedPool, financialUnlinkedPool, MinUnlinkedSample, BuildChargeRecord, BuildFinancialRecord));

        return new CoastalPilotSamplingResult(records);
    }

    private static IEnumerable<CoastalChargeLinkResultEntry> Ordered(IEnumerable<CoastalChargeLinkResultEntry> entries) =>
        entries.OrderBy(e => e.OuterEntryFullPath, StringComparer.Ordinal).ThenBy(e => e.NestedEntryRelativePath, StringComparer.Ordinal);

    private static IEnumerable<CoastalFinancialLinkResultEntry> Ordered(IEnumerable<CoastalFinancialLinkResultEntry> entries) =>
        entries.OrderBy(e => e.OuterEntryFullPath, StringComparer.Ordinal).ThenBy(e => e.NestedEntryRelativePath, StringComparer.Ordinal);

    /// <summary>Round-robins across every stratum from both domain pools (ordered by stratum key,
    /// then by path within each stratum) until <paramref name="minTotal"/> records are taken or every
    /// stratum is exhausted — deterministic, and guaranteed to touch every available stratum at least
    /// once before repeating any, so a small stratum is never crowded out by a large one.</summary>
    private static List<CoastalPilotSampleRecord> TakeStratifiedRoundRobin(
        IEnumerable<(string StratumKey, CoastalChargeLinkResultEntry Item, string OuterEntryFullPath, string NestedEntryRelativePath)> chargePool,
        IEnumerable<(string StratumKey, CoastalFinancialLinkResultEntry Item, string OuterEntryFullPath, string NestedEntryRelativePath)> financialPool,
        int minTotal,
        Func<CoastalChargeLinkResultEntry, string, CoastalPilotSampleRecord> buildCharge,
        Func<CoastalFinancialLinkResultEntry, string, CoastalPilotSampleRecord> buildFinancial)
    {
        var chargeStrata = chargePool
            .GroupBy(x => x.StratumKey)
            .Select(g => (g.Key, Queue: new Queue<CoastalChargeLinkResultEntry>(g
                .OrderBy(x => x.OuterEntryFullPath, StringComparer.Ordinal).ThenBy(x => x.NestedEntryRelativePath, StringComparer.Ordinal)
                .Select(x => x.Item))));
        var financialStrata = financialPool
            .GroupBy(x => x.StratumKey)
            .Select(g => (g.Key, Queue: new Queue<CoastalFinancialLinkResultEntry>(g
                .OrderBy(x => x.OuterEntryFullPath, StringComparer.Ordinal).ThenBy(x => x.NestedEntryRelativePath, StringComparer.Ordinal)
                .Select(x => x.Item))));

        // A stable, deterministic interleave: alternate one dequeue attempt per stratum, charge
        // strata first (ordered by their own group key via GroupBy's stable ordering over the
        // already-sorted pool), then financial strata, repeating rounds until minTotal is reached.
        var chargeQueues = chargeStrata.ToList();
        var financialQueues = financialStrata.ToList();

        var taken = new List<CoastalPilotSampleRecord>();
        var anyRemaining = true;
        while (taken.Count < minTotal && anyRemaining)
        {
            anyRemaining = false;
            foreach (var (key, q) in chargeQueues)
            {
                if (taken.Count >= minTotal) break;
                if (q.Count == 0) continue;
                taken.Add(buildCharge(q.Dequeue(), key));
                anyRemaining = true;
            }
            if (taken.Count >= minTotal) break;
            foreach (var (key, q) in financialQueues)
            {
                if (taken.Count >= minTotal) break;
                if (q.Count == 0) continue;
                taken.Add(buildFinancial(q.Dequeue(), key));
                anyRemaining = true;
            }
        }

        return taken;
    }

    private static CoastalPilotSampleRecord BuildChargeRecord(CoastalChargeLinkResultEntry e, string stratumKey)
    {
        string snippet;
        try
        {
            using var doc = JsonDocument.Parse(e.EvidenceJson);
            var root = doc.RootElement;
            string? Prop(string name) => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
            var filing = Prop("FilingDate");
            var eventDate = Prop("EventDate");
            var rule = Prop("ClassificationRule");
            snippet = $"FilingDate={filing ?? "-"}, EventDate={eventDate ?? "-"}, ClassificationRule={rule ?? "-"}, " +
                      $"MatchedEventType={e.MatchedEventType?.ToString() ?? "-"}, DateMatchMode={e.DateMatchMode}";
        }
        catch (JsonException)
        {
            snippet = "(EvidenceJson could not be parsed)";
        }

        return new CoastalPilotSampleRecord
        {
            Sha256Hex = e.Sha256Hex,
            OuterEntryFullPath = e.OuterEntryFullPath,
            NestedEntryRelativePath = e.NestedEntryRelativePath,
            Domain = SampleDomain.Charge,
            StratumKey = stratumKey,
            InferredOutcome = e.Outcome.ToString(),
            InferredReason = e.Reason.ToString(),
            EvidenceSnippet = snippet,
            EvidenceJson = e.EvidenceJson
        };
    }

    private static CoastalPilotSampleRecord BuildFinancialRecord(CoastalFinancialLinkResultEntry e, string stratumKey)
    {
        var snippet = !string.IsNullOrWhiteSpace(e.EvidenceTextQuote)
            ? e.EvidenceTextQuote!
            : e.TargetCoordinates is { } c
                ? $"{c.SheetName} row {c.SourceRowNumber}, column {c.SourceColumnNumber} ({c.SourceColumnHeader}) — {c.RowLabel ?? c.TargetField ?? "target"}"
                : $"MatchedValue={e.MatchedValue?.ToString() ?? "-"}, Basis={e.MatchedBasis?.ToString() ?? "-"}, FY={e.MatchedFinancialYear?.ToString() ?? "-"}";

        return new CoastalPilotSampleRecord
        {
            Sha256Hex = e.Sha256Hex,
            OuterEntryFullPath = e.OuterEntryFullPath,
            NestedEntryRelativePath = e.NestedEntryRelativePath,
            Domain = SampleDomain.Financial,
            StratumKey = stratumKey,
            InferredOutcome = e.Outcome.ToString(),
            InferredReason = e.Reason.ToString(),
            EvidenceSnippet = snippet,
            EvidencePageNumber = e.EvidencePageNumber,
            EvidenceSheetName = e.TargetCoordinates?.SheetName,
            EvidenceSourceRow = e.TargetCoordinates?.SourceRowNumber,
            EvidenceSourceColumn = e.TargetCoordinates?.SourceColumnNumber,
            EvidenceJson = e.EvidenceJson
        };
    }
}
