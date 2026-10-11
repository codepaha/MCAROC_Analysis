using System.Text.Json;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

public enum ObservationSource { Vendor = 1, DrtPortal = 2, ConsumerPortal = 3 }

public enum ObservationOutcome { Found = 1, NotFound = 2, Error = 3, IdentityRejected = 4 }

/// <summary>The compact parsed snapshot of one portal response (no raw body is ever stored; plan section 4.1). Adapters map their own responses into it.
/// Values are as the source gave them; the engine decides what is missing.</summary>
public sealed record ParsedCaseSnapshot(
    string? Status, string? Stage, string? Court, string? FilingDate, string? LastHearingDate, string? NextHearingDate, string? DecisionDate, string? DisposalNature,
    string? SuitAmount, int HearingCount, bool DecisionDateDerived = false);

/// <summary>One observation, as the projection engine sees it. <paramref name="Verified"/> is true only for a Found response whose identity check passed (plan section 4.3).</summary>
public sealed record ProjectionObservation(
    long ObservationId, ObservationSource Source, ObservationOutcome Outcome, bool Verified, DateTime RetrievedUtc, ParsedCaseSnapshot Snapshot);

/// <summary>Where a field's current value came from.</summary>
public sealed record FieldSource(string Source, DateTime WhenUtc, long ObservationId, bool ConfirmedEmpty = false);

/// <summary>The verified projection of a case: the last verified observation folded in field by field. Immutable; the engine returns a new one.</summary>
public sealed record LiveProjectionState(
    string? Status, string? Stage, string? Court, string? FilingDate, string? LastHearingDate, string? NextHearingDate, string? DecisionDate, string? DisposalNature, string? SuitAmount,
    int HearingCount, DateTime? LastRetrievedUtc, long? LastObservationId,
    IReadOnlyDictionary<string, FieldSource> Sources, string? HeldChange)
{
    public static LiveProjectionState Empty { get; } = new(null, null, null, null, null, null, null, null, null, 0, null, null, new Dictionary<string, FieldSource>(), null);
}

public enum ProjectionOutcome { Applied, IgnoredNotVerified, IgnoredLate, HeldStatusRegression, HeldShrinkingHistory }

public sealed record ProjectionResult(LiveProjectionState State, ProjectionOutcome Outcome, IReadOnlyList<string> ChangedFields);

/// <summary>Folds a verified observation into a case's projection (plan sections 4.1 and 4.2e). Pure and deterministic.
/// <list type="bullet">
/// <item>Only a <b>verified</b> observation can change the projection; an unverified, not-found or errored one is recorded elsewhere and never edits a value.</item>
/// <item>A <b>late</b> observation (retrieved before the one already applied) is ignored, so a slow agent cannot undo newer data.</item>
/// <item>A missing value <b>never erases</b> a held one (partial responses are normal). Three fields are the exception, because empty is itself information: next hearing date, decision
/// date and suit amount. For these a verified, <i>complete</i> response that returns empty records <b>confirmed empty</b> and replaces a stale value. A complete response has a status and a filing date.</item>
/// <item>A status that moves a disposed case back to pending, or a hearing history that shrinks, is <b>held</b> until a second observation agrees; one odd response never rewrites a case.</item>
/// </list></summary>
public static class LiveProjectionEngine
{
    public const string FStatus = "status", FStage = "stage", FCourt = "court", FFilingDate = "filing date", FLastHearing = "last hearing date", FNextHearing = "next hearing date",
        FDecisionDate = "decision date", FDisposalNature = "disposal nature", FSuitAmount = "suit amount";

    private static readonly HashSet<string> MeaningfulEmpty = [FNextHearing, FDecisionDate, FDisposalNature, FSuitAmount];

    public static ProjectionResult Apply(LiveProjectionState current, ProjectionObservation o)
    {
        if (!o.Verified || o.Outcome != ObservationOutcome.Found)
            return new(current, ProjectionOutcome.IgnoredNotVerified, []);
        if (current.LastRetrievedUtc is { } last && o.RetrievedUtc < last)
            return new(current, ProjectionOutcome.IgnoredLate, []);

        var snap = o.Snapshot;

        // Guard: a disposed case does not go back to pending on one response.
        var heldBefore = current.HeldChange;
        var currentBucket = LitigationCaseStatusClassifier.Classify(current.Status, current.Stage);
        var incomingBucket = LitigationCaseStatusClassifier.Classify(snap.Status, snap.Stage);
        if (currentBucket == LitigationCaseStatusBucket.Disposed && incomingBucket == LitigationCaseStatusBucket.Pending && !CaseValue.IsMissing(snap.Status))
        {
            var marker = "status:" + snap.Status!.Trim().ToUpperInvariant();
            if (heldBefore != marker) return new(current with { HeldChange = marker }, ProjectionOutcome.HeldStatusRegression, []);
        }

        // Guard: the hearing history does not shrink on one response.
        if (current.HearingCount > 0 && snap.HearingCount > 0 && snap.HearingCount < current.HearingCount)
        {
            var marker = "history:" + snap.HearingCount;
            if (heldBefore != marker) return new(current with { HeldChange = marker }, ProjectionOutcome.HeldShrinkingHistory, []);
        }

        var complete = !CaseValue.IsMissing(snap.Status) && !CaseValue.IsMissing(snap.FilingDate);
        var sources = new Dictionary<string, FieldSource>(current.Sources);
        var changed = new List<string>();
        var src = SourceName(o.Source);

        string? Merge(string field, string? held, string? incoming)
        {
            if (!CaseValue.IsMissing(incoming))
            {
                if (!string.Equals(held, incoming, StringComparison.Ordinal) || sources.GetValueOrDefault(field)?.ConfirmedEmpty == true)
                {
                    changed.Add(field);
                    sources[field] = new FieldSource(src, o.RetrievedUtc, o.ObservationId);
                }
                return incoming;
            }
            if (MeaningfulEmpty.Contains(field) && complete)
            {
                // Confirmed empty: the portal itself says there is nothing, so a stale held value is replaced and not resurrected.
                if (!CaseValue.IsMissing(held) || sources.GetValueOrDefault(field)?.ConfirmedEmpty != true) changed.Add(field);
                sources[field] = new FieldSource(src, o.RetrievedUtc, o.ObservationId, ConfirmedEmpty: true);
                return null;
            }
            return held;   // missing never erases
        }

        var next = current with
        {
            Status = Merge(FStatus, current.Status, snap.Status),
            Stage = Merge(FStage, current.Stage, snap.Stage),
            Court = Merge(FCourt, current.Court, snap.Court),
            FilingDate = Merge(FFilingDate, current.FilingDate, snap.FilingDate),
            LastHearingDate = Merge(FLastHearing, current.LastHearingDate, snap.LastHearingDate),
            NextHearingDate = Merge(FNextHearing, current.NextHearingDate, snap.NextHearingDate),
            DecisionDate = Merge(FDecisionDate, current.DecisionDate, snap.DecisionDate),
            DisposalNature = Merge(FDisposalNature, current.DisposalNature, snap.DisposalNature),
            SuitAmount = Merge(FSuitAmount, current.SuitAmount, snap.SuitAmount),
            HearingCount = snap.HearingCount > 0 ? snap.HearingCount : current.HearingCount,
            LastRetrievedUtc = o.RetrievedUtc, LastObservationId = o.ObservationId,
            Sources = sources, HeldChange = null
        };
        if (snap.DecisionDateDerived && !CaseValue.IsMissing(snap.DecisionDate) && sources.TryGetValue(FDecisionDate, out var ds))
            sources[FDecisionDate] = ds with { Source = "Derived" };
        return new(next, ProjectionOutcome.Applied, changed);
    }

    /// <summary>The "confirmed empty" marks as the eligibility rule consumes them (field name to when the portal last said empty).</summary>
    public static IReadOnlyDictionary<string, DateTime> ConfirmedEmptyMarks(LiveProjectionState s) =>
        s.Sources.Where(kv => kv.Value.ConfirmedEmpty).ToDictionary(kv => kv.Key, kv => kv.Value.WhenUtc);

    public static string SerializeSources(IReadOnlyDictionary<string, FieldSource> sources) => JsonSerializer.Serialize(sources);

    public static IReadOnlyDictionary<string, FieldSource> DeserializeSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, FieldSource>();
        try { return JsonSerializer.Deserialize<Dictionary<string, FieldSource>>(json) ?? []; }
        catch (JsonException) { return new Dictionary<string, FieldSource>(); }
    }

    private static string SourceName(ObservationSource s) => s switch
    {
        ObservationSource.Vendor => "Vendor",
        _ => "Portal"
    };
}
