using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationModule.Refresh;

namespace MCAROC_Analysis.Services.LitigationModule.Freshness;

/// <summary>The one label a case carries about how current its court data is (plan section 4.8). The case list, the card, the banner counts, the report and the PDF all take it from
/// <see cref="CaseFreshness.Of"/>, so they cannot disagree and no label is ever guessed.</summary>
public enum FreshnessLabel
{
    /// <summary>Not part of the request's view at all (merged into another case). Never shown.</summary>
    Excluded,
    /// <summary>Verified on the court portal. Green.</summary>
    Updated,
    /// <summary>Queued, in progress, or due again and not yet checked. Grey.</summary>
    NotYetUpdated,
    /// <summary>Pending with a next hearing today or later and nothing missing: intentionally not fetched. Neutral.</summary>
    NotDue,
    /// <summary>Disposed, and its final order is on file. Neutral.</summary>
    DisposedFinalOrderOnFile,
    /// <summary>Disposed cases are not refreshed by design. Neutral.</summary>
    DisposedNotRefreshed,
    /// <summary>Not found, identity mismatch, ambiguous, or failed after its retries. Amber.</summary>
    CouldNotVerify
}

public enum LastAttemptKind { None, Verified, NotFound, IdentityRejected, Failed }

/// <summary>What is stored about one case, as the freshness label needs it.</summary>
/// <param name="MergedAway">The case was soft-merged into another.</param>
/// <param name="Eligibility">The refresh decision from <see cref="LiveRefreshEligibility.Decide"/> (disposed: never; pending: when due).</param>
/// <param name="LastVerifiedUtc">When the portal last verified this case (a verified found observation); null if never.</param>
/// <param name="LastAttempt">The outcome of the newest attempt, and when. A later verified observation supersedes an earlier failure.</param>
/// <param name="FailureReason">Why the newest attempt failed or could not verify, in words the reader can use.</param>
/// <param name="QueuedOrInProgress">A work item for this case is waiting or being worked on.</param>
/// <param name="AnalysisOnEarlierData">The case's AI analysis was made on evidence that has since changed (stored hash differs from the current hash).</param>
public sealed record CaseFreshnessInput(
    bool MergedAway, LitigationCaseStatusBucket StatusBucket, bool FinalOrderOnFile, LiveRefreshDecision Eligibility,
    DateTime? LastVerifiedUtc, LastAttemptKind LastAttempt, DateTime? LastAttemptUtc, string? FailureReason, bool QueuedOrInProgress, bool AnalysisOnEarlierData = false);

/// <param name="AsOfUtc">The date to show beside "Updated" (the last verification); null for the other labels.</param>
/// <param name="Reason">A short plain reason: why it could not be verified, or why it is not due.</param>
/// <param name="AnalysisOnEarlierData">Shown beside the data label, not instead of it ("Analysed on earlier data - re-analysis queued").</param>
public sealed record CaseFreshnessResult(FreshnessLabel Label, DateTime? AsOfUtc, string? Reason, bool AnalysisOnEarlierData);

public static class CaseFreshness
{
    public static CaseFreshnessResult Of(CaseFreshnessInput c)
    {
        if (c.MergedAway) return new(FreshnessLabel.Excluded, null, "merged into another case", false);

        var verified = c.LastVerifiedUtc is not null;
        // A failure or a non-match counts only if nothing verified the case after it.
        var verifiedAfterAttempt = verified && (c.LastAttemptUtc is null || c.LastVerifiedUtc >= c.LastAttemptUtc);
        if (!verifiedAfterAttempt && c.LastAttempt is LastAttemptKind.NotFound or LastAttemptKind.IdentityRejected or LastAttemptKind.Failed)
            return new(FreshnessLabel.CouldNotVerify, null, c.FailureReason ?? ReasonFor(c.LastAttempt), c.AnalysisOnEarlierData);

        var dueForStatus = c.Eligibility.Action == LiveRefreshAction.FetchStatus;
        if (verified && !dueForStatus) return new(FreshnessLabel.Updated, c.LastVerifiedUtc, null, c.AnalysisOnEarlierData);
        if (dueForStatus || c.QueuedOrInProgress)
            return new(FreshnessLabel.NotYetUpdated, null, c.QueuedOrInProgress ? "queued" : c.Eligibility.Reason, c.AnalysisOnEarlierData);

        return c.StatusBucket == LitigationCaseStatusBucket.Disposed
            ? new(c.FinalOrderOnFile ? FreshnessLabel.DisposedFinalOrderOnFile : FreshnessLabel.DisposedNotRefreshed, null, c.Eligibility.Reason, c.AnalysisOnEarlierData)
            : new(FreshnessLabel.NotDue, null, c.Eligibility.Reason, c.AnalysisOnEarlierData);
    }

    private static string ReasonFor(LastAttemptKind kind) => kind switch
    {
        LastAttemptKind.NotFound => "the portal has no such case",
        LastAttemptKind.IdentityRejected => "the portal returned a different case",
        LastAttemptKind.Failed => "the portal could not be reached",
        _ => "unknown"
    };

    /// <summary>The label text for the list, card, report and PDF. Times are IST (the caller passes an IST-formatted date).</summary>
    public static string Text(CaseFreshnessResult r, Func<DateTime, string> formatIstDate) => r.Label switch
    {
        FreshnessLabel.Updated => $"Updated {formatIstDate(r.AsOfUtc!.Value)}",
        FreshnessLabel.NotYetUpdated => "Not yet updated",
        FreshnessLabel.NotDue => "Not due",
        FreshnessLabel.DisposedFinalOrderOnFile => "Disposed - final order on file",
        FreshnessLabel.DisposedNotRefreshed => "Disposed - not refreshed",
        FreshnessLabel.CouldNotVerify => $"Could not verify - {r.Reason}",
        _ => string.Empty
    };
}
