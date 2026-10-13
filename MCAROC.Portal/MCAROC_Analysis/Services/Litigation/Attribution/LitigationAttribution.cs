using System.Linq.Expressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

/// <summary>
/// The one definition of "a case the request shows" with respect to attribution (#489, decision a): a case that is the company's, or one stored before
/// attribution existed and not assessed yet (shown as before until the attribution worker reaches it). Every list, count, report, chat answer, analysis and
/// court refresh applies it, so a case that is not the company's, or is still being judged, never appears or counts anywhere.
/// </summary>
public static class LitigationAttribution
{
    public static readonly Expression<Func<LitigationCase, bool>> IsShown =
        c => c.AttributionOutcome == null || c.AttributionOutcome == LitigationAttributionOutcome.Attributed;

    public static readonly Expression<Func<LitigationCaseSourceReport, bool>> IsShownLink =
        sr => sr.Case!.AttributionOutcome == null || sr.Case.AttributionOutcome == LitigationAttributionOutcome.Attributed;

    public static bool Shown(LitigationAttributionOutcome? outcome) => outcome is null or LitigationAttributionOutcome.Attributed;

    /// <summary>Cases waiting for attribution: imported since attribution exists and not settled yet. The pipeline's litigation stage waits for them.
    /// (A case stored before attribution existed has no outcome; it stays shown and is assessed in the background without holding the pipeline.)</summary>
    public static readonly Expression<Func<LitigationCase, bool>> IsWaiting =
        c => c.MergedIntoCaseId == null && c.AttributionOutcome == LitigationAttributionOutcome.Pending;

    /// <summary>The cases the attribution worker still has to settle: never assessed, or waiting for a judgement.</summary>
    public static readonly Expression<Func<LitigationCase, bool>> IsOutstanding =
        c => c.MergedIntoCaseId == null && (c.AttributionOutcome == null || c.AttributionOutcome == LitigationAttributionOutcome.Pending);
}
