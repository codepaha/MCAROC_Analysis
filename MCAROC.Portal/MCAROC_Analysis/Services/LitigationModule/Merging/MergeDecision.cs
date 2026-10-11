namespace MCAROC_Analysis.Services.LitigationModule.Merging;

/// <summary>What the merge decision needs to know about one case of a request. Plain values, so the decision is a pure function.</summary>
/// <param name="CaseKey">The shared case key minted by <c>CaseKeyGate</c> (from the identity a portal returned); null when the case never passed the gate.</param>
/// <param name="LiveVerified">The live record is verified for this request: the identity matched and the request's link to the case is Verified (plan 4.3).</param>
/// <param name="MergedIntoCaseId">Set when the case was already merged into another; a merged case is never a candidate again.</param>
public sealed record MergeCandidate(
    long LegacyCaseId, long RequestId, string? CaseKey, bool LiveVerified, int OrderCount, int HearingCount, DateTime FirstSeenUtc, long? MergedIntoCaseId = null);

public enum MergeRefusal
{
    None,
    SameCase,
    AlreadyMerged,
    DifferentRequest,
    NoCaseKey,
    DifferentCaseKey,
    NotLiveVerified
}

public sealed record MergeDecision(bool Allowed, MergeRefusal Refusal, string Reason, long? SurvivorCaseId, long? LoserCaseId);

/// <summary>Decides whether two cases may be merged, and which survives (plan section 4.6). Duplicates are merged, but only when it is certain:
/// <b>both</b> have a gate-complete case key, the keys are <b>equal</b>, they are in the <b>same request</b>, and the live record is <b>verified</b> for both. Same number at another
/// tribunal, another type or another year is another key and never merges. Anything partial or unresolved is never merged; it is only listed as a possible duplicate.</summary>
public static class MergeRules
{
    public static MergeDecision Evaluate(MergeCandidate a, MergeCandidate b)
    {
        if (a.LegacyCaseId == b.LegacyCaseId) return Refuse(MergeRefusal.SameCase, "a case cannot be merged into itself");
        if (a.MergedIntoCaseId is not null || b.MergedIntoCaseId is not null) return Refuse(MergeRefusal.AlreadyMerged, "one of the cases is already merged into another");
        if (a.RequestId != b.RequestId) return Refuse(MergeRefusal.DifferentRequest, "the cases belong to different requests");
        if (string.IsNullOrWhiteSpace(a.CaseKey) || string.IsNullOrWhiteSpace(b.CaseKey)) return Refuse(MergeRefusal.NoCaseKey, "a case without a gate-complete key is never merged");
        if (!string.Equals(a.CaseKey, b.CaseKey, StringComparison.Ordinal)) return Refuse(MergeRefusal.DifferentCaseKey, "the case keys differ (another tribunal, type, number or year)");
        if (!a.LiveVerified || !b.LiveVerified) return Refuse(MergeRefusal.NotLiveVerified, "the live record is not verified for both cases");

        var (survivor, loser) = ChooseSurvivor(a, b);
        return new MergeDecision(true, MergeRefusal.None, "equal gate-complete case keys, same request, live record verified", survivor.LegacyCaseId, loser.LegacyCaseId);
    }

    /// <summary>The survivor is the live-verified case, else the one with more orders and hearings, else the older, else the lower id (so the choice is deterministic).</summary>
    public static (MergeCandidate Survivor, MergeCandidate Loser) ChooseSurvivor(MergeCandidate a, MergeCandidate b)
    {
        if (a.LiveVerified != b.LiveVerified) return a.LiveVerified ? (a, b) : (b, a);
        var aWeight = a.OrderCount + a.HearingCount;
        var bWeight = b.OrderCount + b.HearingCount;
        if (aWeight != bWeight) return aWeight > bWeight ? (a, b) : (b, a);
        if (a.FirstSeenUtc != b.FirstSeenUtc) return a.FirstSeenUtc < b.FirstSeenUtc ? (a, b) : (b, a);
        return a.LegacyCaseId < b.LegacyCaseId ? (a, b) : (b, a);
    }

    /// <summary>Groups a request's cases into the sets that may merge (equal gate-complete keys, live-verified, not already merged). Cases that would be merged are returned together; each
    /// group's survivor is the best candidate and the rest are losers, in a stable order. Partial or keyless cases never appear.</summary>
    public static IReadOnlyList<MergeGroup> FindMergeGroups(IEnumerable<MergeCandidate> candidates) =>
        candidates.Where(c => c.MergedIntoCaseId is null && c.LiveVerified && !string.IsNullOrWhiteSpace(c.CaseKey))
            .GroupBy(c => (c.RequestId, c.CaseKey!))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.RequestId).ThenBy(g => g.Key.Item2, StringComparer.Ordinal)
            .Select(g =>
            {
                var ordered = g.ToList();
                var survivor = ordered.Aggregate((best, next) => ChooseSurvivor(best, next).Survivor);
                return new MergeGroup(g.Key.RequestId, g.Key.Item2, survivor.LegacyCaseId, ordered.Where(c => c.LegacyCaseId != survivor.LegacyCaseId).Select(c => c.LegacyCaseId).OrderBy(i => i).ToList());
            }).ToList();

    private static MergeDecision Refuse(MergeRefusal refusal, string reason) => new(false, refusal, reason, null, null);
}

public sealed record MergeGroup(long RequestId, string CaseKey, long SurvivorCaseId, IReadOnlyList<long> LoserCaseIds);
