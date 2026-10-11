using System.Globalization;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>Records whether a request's company is really a party to a case (plan section 4.3).</summary>
public sealed class RequestLinkStore(AppDbContext db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task UpsertAsync(long requestId, long caseId, PartyMatchResult party, string? matchedSide, bool roleConflict, string? roleNote, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        // A change of link serialises with publication and vendor imports of the same case (CaseLock): a publisher either sees the new verdict or has already published before it.
        var legacyId = await db.Set<ModuleCase>().AsNoTracking().Where(c => c.CaseId == caseId).Select(c => c.LegacyLitigationCaseId).FirstOrDefaultAsync(ct);
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (legacyId is { } legacy) await CaseLock.AcquireAsync(db, legacy, ct);

        var link = await db.Set<ModuleRequestLink>().FirstOrDefaultAsync(l => l.RequestId == requestId && l.CaseId == caseId, ct);
        if (link is null)
        {
            link = new ModuleRequestLink { RequestId = requestId, CaseId = caseId };
            db.Set<ModuleRequestLink>().Add(link);
        }
        link.Verdict = (int)party.Outcome;
        link.Rule = party.Rule;
        link.MatchedParty = party.MatchedParty is { Length: > 512 } mp ? mp[..512] : party.MatchedParty;
        link.MatchedSide = matchedSide;
        link.RoleConflict = roleConflict;
        link.RoleNote = roleNote;
        link.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    public async Task<PartyMatchOutcome?> GetVerdictAsync(long requestId, long caseId, CancellationToken ct)
    {
        var v = await db.Set<ModuleRequestLink>().AsNoTracking().Where(l => l.RequestId == requestId && l.CaseId == caseId).Select(l => (int?)l.Verdict).FirstOrDefaultAsync(ct);
        return v is { } value ? (PartyMatchOutcome)value : null;
    }
}

public enum PublishToRequestOutcome { Published, NothingToChange, NotLinked, LinkNotVerified, NoProjection, NoLegacyCase }

public sealed record PublishToRequestResult(PublishToRequestOutcome Outcome, IReadOnlyList<string> ChangedColumns);

/// <summary>Which of a legacy case's columns the portal owns, for the vendor guard (plan section 4.2c).</summary>
public static class LivePortalOwnership
{
    public const string CaseStatus = nameof(LitigationCase.CaseStatus), CaseStage = nameof(LitigationCase.CaseStage), Court = nameof(LitigationCase.Court),
        FilingDate = nameof(LitigationCase.FilingDate), LastHearingDate = nameof(LitigationCase.LastHearingDate), NextHearingDate = nameof(LitigationCase.NextHearingDate),
        DecisionDate = nameof(LitigationCase.DecisionDate);

    private static readonly (string Column, string Field)[] Map =
    [
        (CaseStatus, LiveProjectionEngine.FStatus), (CaseStage, LiveProjectionEngine.FStage), (Court, LiveProjectionEngine.FCourt), (FilingDate, LiveProjectionEngine.FFilingDate),
        (LastHearingDate, LiveProjectionEngine.FLastHearing), (NextHearingDate, LiveProjectionEngine.FNextHearing), (DecisionDate, LiveProjectionEngine.FDecisionDate)
    ];

    /// <summary>The columns of this request's case that the portal owns: the link is Verified and the projection's source for the field is the portal (a value or a confirmed empty).
    /// Empty when the case has no live data or no verified link, so a vendor re-import behaves exactly as before.</summary>
    public static async Task<IReadOnlySet<string>> OwnedColumnsAsync(AppDbContext db, long requestId, long legacyCaseId, CancellationToken ct)
    {
        var row = await (from mc in db.Set<ModuleCase>().AsNoTracking()
            where mc.LegacyLitigationCaseId == legacyCaseId
            join l in db.Set<ModuleRequestLink>().AsNoTracking() on new { mc.CaseId, RequestId = requestId } equals new { l.CaseId, l.RequestId }
            join live in db.Set<ModuleCaseLive>().AsNoTracking() on mc.CaseId equals live.CaseId
            select new { l.Verdict, live.FieldSourcesJson }).FirstOrDefaultAsync(ct);
        if (row is null || row.Verdict != (int)PartyMatchOutcome.Verified) return new HashSet<string>();
        var sources = LiveProjectionEngine.DeserializeSources(row.FieldSourcesJson);
        return Map.Where(m => sources.TryGetValue(m.Field, out var s) && s.Source is "Portal" or "Derived").Select(m => m.Column).ToHashSet();
    }
}

/// <summary>Publishes a case's verified projection into the request's <c>LitigationCase</c> columns, so every consumer that already reads those columns sees the live values (plan 4.2).
/// Only when the request's link to the case is Verified. The columns and the request's live-data version change in one transaction.</summary>
public static class LiveCasePublisher
{
    public static async Task<PublishToRequestResult> PublishAsync(AppDbContext db, long requestId, long legacyCaseId, CancellationToken ct)
    {
        var moduleCaseId = await db.Set<ModuleCase>().AsNoTracking().Where(c => c.LegacyLitigationCaseId == legacyCaseId).Select(c => (long?)c.CaseId).FirstOrDefaultAsync(ct);
        if (moduleCaseId is not { } caseId) return new(PublishToRequestOutcome.NotLinked, []);

        // Everything that decides the write is read AFTER taking the per-case lock, in the same transaction as the write: the link verdict, the projection and the row. A concurrent
        // link change, vendor import or earlier publication has then either finished (and is seen) or waits for this one.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await CaseLock.AcquireAsync(db, legacyCaseId, ct);

        var link = await db.Set<ModuleRequestLink>().FirstOrDefaultAsync(l => l.RequestId == requestId && l.CaseId == caseId, ct);
        if (link is null) return new(PublishToRequestOutcome.NotLinked, []);
        if (link.Verdict != (int)PartyMatchOutcome.Verified) return new(PublishToRequestOutcome.LinkNotVerified, []);

        var live = await db.Set<ModuleCaseLive>().AsNoTracking().FirstOrDefaultAsync(l => l.CaseId == caseId, ct);
        if (live is null) return new(PublishToRequestOutcome.NoProjection, []);
        // A projection older than what this request already received is never published over it.
        if (link.PublishedObservationId is { } published && live.LastObservationId is { } latest && latest < published)
            return new(PublishToRequestOutcome.NothingToChange, []);
        var sources = LiveProjectionEngine.DeserializeSources(live.FieldSourcesJson);

        var legacy = await db.LitigationCases.FirstOrDefaultAsync(c => c.LitigationCaseId == legacyCaseId && c.RequestId == requestId, ct);
        if (legacy is null) return new(PublishToRequestOutcome.NoLegacyCase, []);
        await db.Entry(legacy).ReloadAsync(ct);

        var changed = new List<string>();
        void Set(string column, string field, string? projected, bool isDate, string? current, Action<string?> assign)
        {
            sources.TryGetValue(field, out var source);
            string? target;
            if (!CaseValue.IsMissing(projected)) target = isDate ? DateText(projected) : projected;
            else if (source is { ConfirmedEmpty: true, Source: "Portal" }) target = null;   // the portal said there is nothing: a stale value is replaced, not kept
            else return;                                                                     // missing never erases
            if (string.Equals(current, target, StringComparison.Ordinal)) return;
            assign(target);
            changed.Add(column);
        }

        Set(LivePortalOwnership.CaseStatus, LiveProjectionEngine.FStatus, live.Status, false, legacy.CaseStatus, v => legacy.CaseStatus = v);
        Set(LivePortalOwnership.CaseStage, LiveProjectionEngine.FStage, live.Stage, false, legacy.CaseStage, v => legacy.CaseStage = v);
        Set(LivePortalOwnership.Court, LiveProjectionEngine.FCourt, live.Court, false, legacy.Court, v => legacy.Court = v);
        Set(LivePortalOwnership.FilingDate, LiveProjectionEngine.FFilingDate, live.FilingDate, true, legacy.FilingDate, v => legacy.FilingDate = v);
        Set(LivePortalOwnership.LastHearingDate, LiveProjectionEngine.FLastHearing, live.LastHearingDate, true, legacy.LastHearingDate, v => legacy.LastHearingDate = v);
        Set(LivePortalOwnership.NextHearingDate, LiveProjectionEngine.FNextHearing, live.NextHearingDate, true, legacy.NextHearingDate, v => legacy.NextHearingDate = v);
        Set(LivePortalOwnership.DecisionDate, LiveProjectionEngine.FDecisionDate, live.DecisionDate, true, legacy.DecisionDate, v => legacy.DecisionDate = v);

        link.PublishedObservationId = live.LastObservationId;
        if (changed.Count == 0)
        {
            await db.SaveChangesAsync(ct);   // only the link's published-observation marker
            if (tx is not null) await tx.CommitAsync(ct);
            return new(PublishToRequestOutcome.NothingToChange, []);
        }
        await db.SaveChangesAsync(ct);
        await RequestLiveData.BumpAsync(db, requestId, ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new(PublishToRequestOutcome.Published, changed);
    }

    /// <summary>The vendor's date convention in the stored columns: dd-MM-yyyy. The projection holds day-first d-M-yyyy.</summary>
    private static string? DateText(string? value) =>
        CaseDates.ParseDate(value) is { } d ? d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) : value;
}
