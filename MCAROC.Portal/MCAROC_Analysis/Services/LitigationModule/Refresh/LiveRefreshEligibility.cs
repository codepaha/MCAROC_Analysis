using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>What the module already holds about a case, as eligibility needs it (plan section 4.4). All values are as stored; nothing is fetched to build this.</summary>
/// <param name="ConfirmedEmpty">Fields a verified portal call returned empty, with when. Within the confirmed-empty window they count as known, not missing.</param>
public sealed record LiveRefreshCaseState(
    string? CaseStatus, string? CaseStage, string? Court, string? State, string? District, string? FilingDate, string? LastHearingDate, string? NextHearingDate,
    CaseNumberCompleteness NumberCompleteness, bool EverLiveVerified, bool NewlyDiscovered,
    IReadOnlyDictionary<string, DateTime>? ConfirmedEmpty = null, bool OrdersRequested = false, bool FinalOrderOnFile = false);

public enum LiveRefreshAction
{
    /// <summary>No portal call.</summary>
    Skip,
    /// <summary>One status/detail call (which also lists hearings and the orders available).</summary>
    FetchStatus,
    /// <summary>A disposed case whose orders are wanted: the orders path only; status and hearing fields are by-products, never the reason for a call.</summary>
    FetchOrdersOnly
}

public sealed record LiveRefreshDecision(LiveRefreshAction Action, string Reason, IReadOnlyList<string> MissingFields);

/// <summary>Decides, from data already held and before any portal call, whether a case is worth a call (plan section 4.4, owner rules of 2026-10-10):
/// a disposed case is never refreshed; a pending or unknown one only when its hearing has passed, a required field is missing, or it was never verified;
/// a next hearing today or later means nothing new can have happened. Pure and deterministic.</summary>
public static class LiveRefreshEligibility
{
    public const string Status = "status", Court = "court", State = "state", District = "district", FilingDate = "filing date",
        LastHearing = "last hearing date", NextHearing = "next hearing date", CaseNumber = "case number";

    /// <summary>How long a field the portal itself returned empty stays "confirmed empty" before it is asked about again (owner-proposed 7 days).</summary>
    public static readonly TimeSpan ConfirmedEmptyWindow = TimeSpan.FromDays(7);

    /// <param name="todayIst">Today's calendar date in IST.</param>
    /// <param name="nowUtc">Used only to age the confirmed-empty marks.</param>
    public static LiveRefreshDecision Decide(LiveRefreshCaseState c, DateOnly todayIst, DateTime nowUtc)
    {
        var bucket = LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage);

        if (bucket == LitigationCaseStatusBucket.Disposed)
        {
            if (c.OrdersRequested && !c.FinalOrderOnFile)
                return new(LiveRefreshAction.FetchOrdersOnly, "disposed: orders requested and the final order is not on file", []);
            return new(LiveRefreshAction.Skip, "disposed: status and hearings are never refreshed", []);
        }

        // Pending, or status unknown (treated as pending).
        var missing = MissingRequired(c, bucket, nowUtc);

        if (c.NewlyDiscovered && !c.EverLiveVerified)
            return new(LiveRefreshAction.FetchStatus, "newly discovered: one detail call to learn its status", missing);
        if (!c.EverLiveVerified)
            return new(LiveRefreshAction.FetchStatus, "never verified against the portal", missing);
        if (missing.Count > 0)
            return new(LiveRefreshAction.FetchStatus, "a required field is missing", missing);

        var next = CaseDates.ParseDate(c.NextHearingDate);
        if (next is { } n && n < todayIst)
            return new(LiveRefreshAction.FetchStatus, "the next hearing date has passed", missing);
        if (next is { } future)
            return new(LiveRefreshAction.Skip, future == todayIst ? "the next hearing is today: nothing new can have happened yet" : "the next hearing is in the future", missing);

        // No next hearing date, and it is confirmed empty (otherwise it would be listed as missing above): the case is awaiting its next listing.
        return new(LiveRefreshAction.Skip, "no next hearing date, confirmed empty by the portal recently", missing);
    }

    /// <summary>The required fields (plan 4.4) that are missing: status, court, state, district, filing date, last hearing date, next hearing date (pending cases) and a complete case number.
    /// A field the portal confirmed empty within the window is not missing.</summary>
    public static IReadOnlyList<string> MissingRequired(LiveRefreshCaseState c, LitigationCaseStatusBucket bucket, DateTime nowUtc)
    {
        var missing = new List<string>();
        void Check(string name, string? value)
        {
            if (!CaseValue.IsMissing(value)) return;
            if (c.ConfirmedEmpty is { } marks && marks.TryGetValue(name, out var at) && nowUtc - at < ConfirmedEmptyWindow) return;
            missing.Add(name);
        }
        Check(Status, c.CaseStatus);
        Check(Court, c.Court);
        Check(State, c.State);
        Check(District, c.District);
        Check(FilingDate, c.FilingDate);
        Check(LastHearing, c.LastHearingDate);
        if (bucket != LitigationCaseStatusBucket.Disposed) Check(NextHearing, c.NextHearingDate);
        if (c.NumberCompleteness != CaseNumberCompleteness.Complete) missing.Add(CaseNumber);
        // A next hearing date that is present but unreadable is as good as missing.
        if (!CaseValue.IsMissing(c.NextHearingDate) && CaseDates.ParseDate(c.NextHearingDate) is null && !missing.Contains(NextHearing) && bucket != LitigationCaseStatusBucket.Disposed)
            missing.Add(NextHearing);
        return missing;
    }
}
