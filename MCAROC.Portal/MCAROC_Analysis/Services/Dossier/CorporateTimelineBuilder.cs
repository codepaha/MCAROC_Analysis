using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Dossier;

/// <summary>Builds the portal's unified corporate event timeline (Issue #97) — deliberately NOT part of
/// <see cref="DossierModel"/>/<see cref="DossierAssembler"/>. <see cref="DossierAssembler.BuildAsync"/>
/// requires a completed <see cref="AnalysisRun"/> matching the latest ingestion run before it loads
/// anything; the timeline needs none of that analysis-derived data (findings, exec summary, review
/// priority) and must stay visible while a fresh re-ingest's analysis is still queued or running — so it
/// is keyed only on <see cref="McaRequest.LatestCompletedIngestionRunId"/>, independent of
/// <see cref="AnalysisRun"/> entirely.
///
/// Scope (v1) — every category below has a real, discrete, dated event already in the schema:
/// incorporation, name changes, director appointments/cessations, charge events, capital raises
/// (security allotments), credit-rating actions, financial-dispute defaults/judgements, GST
/// registration/cancellation, EPFO establishment setup, and material compliance records (BIFR/CDR/CIBIL
/// suit-filed/wilful-defaulter/name removal-restoration).
///
/// Deliberately excluded (not an oversight):
/// <list type="bullet">
/// <item>Litigation — no discrete "filed" date exists, only <see cref="Litigation.LastHearingDate"/>
/// (which moves as a case proceeds, not a single dated event), and volume is typically in the hundreds.</item>
/// <item>Related-party transactions — <see cref="RelatedPartyTransaction.FinancialYearEnding"/>-tagged,
/// not date-specific.</item>
/// <item>Auditor changes — <see cref="AuditorObservation"/> is FY-keyed with no explicit change date;
/// detecting a change means diffing consecutive FYs, a different derivation than "this row has a date."</item>
/// <item>Registered-office change history — only the current address is stored today.</item>
/// <item>Routine GST/EPFO filing rows — already surfaced via their own on-time-rate metrics.</item>
/// </list></summary>
public class CorporateTimelineBuilder(AppDbContext db)
{
    /// <summary>Null only when there is no completed ingestion run yet — mirrors
    /// <see cref="DossierAssembler.BuildAsync"/>'s own "not ready" contract for the one condition the two
    /// share, without requiring a completed <see cref="AnalysisRun"/> the way that method does.</summary>
    public async Task<IReadOnlyList<CorporateTimelineEvent>?> BuildAsync(long requestId, CancellationToken ct = default)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request?.LatestCompletedIngestionRunId is not { } runId) return null;

        var events = new List<CorporateTimelineEvent>();

        var profile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == runId, ct);
        var companyName = profile?.CompanyName ?? request.CompanyName;
        if (profile is not null) events.AddRange(IncorporationEvents(profile));

        var nameHistory = await db.CompanyNameHistories.Where(x => x.IngestionRunId == runId)
            .OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        events.AddRange(NameChangeEvents(nameHistory, companyName));

        var directors = await db.Directors.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        var assignmentHistory = await db.DirectorAssignmentHistories.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(DirectorEvents(directors, assignmentHistory));

        var chargeEvents = await db.RocChargeEvents.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(ChargeEvents(chargeEvents));

        var allotments = await db.SecurityAllotments.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(CapitalEvents(allotments));

        var ratings = await db.CreditRatings.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(CreditRatingEvents(ratings));

        var disputes = await db.FinancialDisputeCases.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(FinancialDisputeEvents(disputes));

        var gstRegs = await db.GstRegistrations.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(GstEvents(gstRegs));

        var epfoEstablishments = await db.EpfoEstablishments.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(EpfoEvents(epfoEstablishments));

        var complianceRecords = await db.ComplianceRecords.Where(x => x.IngestionRunId == runId).ToListAsync(ct);
        events.AddRange(ComplianceEvents(complianceRecords));

        return events
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Category)
            .ThenBy(e => e.Provenance.SourceSheetName, StringComparer.Ordinal)
            .ThenBy(e => e.Provenance.SourceRowNumber)
            .ThenBy(e => e.Provenance.EntityId)
            .ToList();
    }

    private static TimelineEventProvenance Provenance(ExtractedEntityBase entity, string entityType, long entityId) =>
        new(entityType, entityId, entity.SourceSheetName, entity.SourceRowNumber, entity.SourceDocumentId);

    private static IEnumerable<CorporateTimelineEvent> IncorporationEvents(CompanyProfile profile)
    {
        if (profile.IncorporationDate is { } date)
        {
            yield return new CorporateTimelineEvent(date, TimelineEventCategory.Corporate, "Company incorporated", null,
                Provenance(profile, nameof(CompanyProfile), profile.CompanyProfileId));
        }
    }

    /// <summary>Renamed FROM <see cref="CompanyNameHistory.PreviousName"/> TO whichever name took over —
    /// the next row's <see cref="CompanyNameHistory.PreviousName"/> in <see cref="CompanyNameHistory.DisplayOrder"/>
    /// sequence, or <paramref name="currentCompanyName"/> for the most recent row.</summary>
    private static IEnumerable<CorporateTimelineEvent> NameChangeEvents(List<CompanyNameHistory> history, string currentCompanyName)
    {
        for (var i = 0; i < history.Count; i++)
        {
            var row = history[i];
            if (row.TillDate is not { } date) continue;
            var newName = i + 1 < history.Count ? history[i + 1].PreviousName : currentCompanyName;
            yield return new CorporateTimelineEvent(date, TimelineEventCategory.Corporate,
                $"Renamed from \"{row.PreviousName}\" to \"{newName}\"", null,
                Provenance(row, nameof(CompanyNameHistory), row.CompanyNameHistoryId));
        }
    }

    /// <summary>Primary source is <see cref="DirectorAssignmentHistory"/> (per-DIN designation "stints" —
    /// richer, since a director can hold several designations over time). Falls back to
    /// <see cref="Director.OriginalAppointmentDate"/>/<see cref="Director.CessationDate"/> only for a DIN
    /// with ZERO assignment-history rows, so a director already in <see cref="Director"/> is never
    /// silently dropped from the timeline just because that sheet didn't cover them.</summary>
    private static IEnumerable<CorporateTimelineEvent> DirectorEvents(List<Director> directors, List<DirectorAssignmentHistory> history)
    {
        var historyByDin = history.ToLookup(h => h.DirectorDin);

        foreach (var director in directors)
        {
            var stints = historyByDin[director.Din].ToList();
            if (stints.Count > 0)
            {
                foreach (var stint in stints)
                {
                    var who = string.IsNullOrWhiteSpace(director.NameRaw) ? stint.DirectorNameRaw : director.NameRaw;
                    var designation = stint.Designation ?? director.Designation ?? "director";
                    if (stint.AppointmentDate is { } appointed)
                    {
                        yield return new CorporateTimelineEvent(appointed, TimelineEventCategory.Directors,
                            $"{who} appointed as {designation}", null,
                            Provenance(stint, nameof(DirectorAssignmentHistory), stint.DirectorAssignmentHistoryId));
                    }
                    if (stint.CessationDate is { } ceased)
                    {
                        yield return new CorporateTimelineEvent(ceased, TimelineEventCategory.Directors,
                            $"{who} ceased as {designation}", null,
                            Provenance(stint, nameof(DirectorAssignmentHistory), stint.DirectorAssignmentHistoryId));
                    }
                }
            }
            else
            {
                var designation = director.Designation ?? "director";
                if (director.OriginalAppointmentDate is { } appointed)
                {
                    yield return new CorporateTimelineEvent(appointed, TimelineEventCategory.Directors,
                        $"{director.NameRaw} appointed as {designation}", null,
                        Provenance(director, nameof(Director), director.DirectorId));
                }
                if (director.CessationDate is { } ceased)
                {
                    yield return new CorporateTimelineEvent(ceased, TimelineEventCategory.Directors,
                        $"{director.NameRaw} ceased as {designation}", null,
                        Provenance(director, nameof(Director), director.DirectorId));
                }
            }
        }
    }

    private static IEnumerable<CorporateTimelineEvent> ChargeEvents(List<RocChargeEvent> chargeEvents)
    {
        foreach (var e in chargeEvents)
        {
            if (e.EventDate is not { } date) continue;
            var amountText = e.ChargeAmount is { } amt ? $" — ₹{amt:N2} Cr" : "";
            var holder = string.IsNullOrWhiteSpace(e.HolderNameRaw) ? "" : $" ({e.HolderNameRaw})";
            yield return new CorporateTimelineEvent(date, TimelineEventCategory.Charges,
                $"Charge {e.EventType.ToString().ToLowerInvariant()}{holder}", amountText.Length > 0 ? amountText.TrimStart(' ', '—').Trim() : null,
                Provenance(e, nameof(RocChargeEvent), e.ChargeEventId));
        }
    }

    private static IEnumerable<CorporateTimelineEvent> CapitalEvents(List<SecurityAllotment> allotments)
    {
        foreach (var a in allotments)
        {
            if (a.AllotmentDate is not { } date) continue;
            var detail = a.NumberOfSecurities is { } n
                ? $"{n:N0} {a.InstrumentType ?? "securities"} allotted{(a.AmountCrore is { } amt ? $" — ₹{amt:N2} Cr" : "")}"
                : a.AmountCrore is { } amt2 ? $"₹{amt2:N2} Cr" : null;
            yield return new CorporateTimelineEvent(date, TimelineEventCategory.Capital,
                $"Securities allotted{(string.IsNullOrWhiteSpace(a.AllotmentType) ? "" : $" ({a.AllotmentType})")}", detail,
                Provenance(a, nameof(SecurityAllotment), a.SecurityAllotmentId));
        }
    }

    private static IEnumerable<CorporateTimelineEvent> CreditRatingEvents(List<CreditRating> ratings)
    {
        foreach (var r in ratings)
        {
            if (r.RatingDate is not { } date) continue;
            var acceptedSuffix = r.IsAccepted ? "" : " (not accepted)";
            var title = $"{r.Agency} rated {r.Instrument ?? "instrument"}: {r.Rating ?? "n/a"}{acceptedSuffix}";
            yield return new CorporateTimelineEvent(date, TimelineEventCategory.CreditRatings, title, r.Action,
                Provenance(r, nameof(CreditRating), r.CreditRatingId));
        }
    }

    private static IEnumerable<CorporateTimelineEvent> FinancialDisputeEvents(List<FinancialDisputeCase> disputes)
    {
        foreach (var d in disputes)
        {
            var amountText = d.AmountUnderDefault is { } amt ? $"₹{amt:N2} Cr — {d.DisputeType ?? "dispute"}" : d.DisputeType;
            if (d.DateOfDefault is { } defaultDate)
            {
                yield return new CorporateTimelineEvent(defaultDate, TimelineEventCategory.FinancialDisputes,
                    "Financial dispute default", amountText,
                    Provenance(d, nameof(FinancialDisputeCase), d.FinancialDisputeCaseId));
            }
            if (d.DateOfJudgement is { } judgementDate)
            {
                yield return new CorporateTimelineEvent(judgementDate, TimelineEventCategory.FinancialDisputes,
                    "Financial dispute judgement", d.Verdict ?? amountText,
                    Provenance(d, nameof(FinancialDisputeCase), d.FinancialDisputeCaseId));
            }
        }
    }

    private static IEnumerable<CorporateTimelineEvent> GstEvents(List<GstRegistration> registrations)
    {
        foreach (var g in registrations)
        {
            if (g.RegistrationDate is { } regDate)
            {
                yield return new CorporateTimelineEvent(regDate, TimelineEventCategory.Gst,
                    $"GST registered ({g.Gstin})", g.State, Provenance(g, nameof(GstRegistration), g.GstId));
            }
            if (g.CancellationDate is { } cancelDate)
            {
                yield return new CorporateTimelineEvent(cancelDate, TimelineEventCategory.Gst,
                    $"GST registration cancelled ({g.Gstin})", g.State, Provenance(g, nameof(GstRegistration), g.GstId));
            }
        }
    }

    private static IEnumerable<CorporateTimelineEvent> EpfoEvents(List<EpfoEstablishment> establishments)
    {
        foreach (var e in establishments)
        {
            if (e.DateOfSetup is not { } date) continue;
            yield return new CorporateTimelineEvent(date, TimelineEventCategory.Epfo,
                $"EPFO establishment set up ({e.EstablishmentId})", e.Name,
                Provenance(e, nameof(EpfoEstablishment), e.EpfoEstablishmentId));
        }
    }

    private static IEnumerable<CorporateTimelineEvent> ComplianceEvents(List<ComplianceRecord> records)
    {
        foreach (var r in records)
        {
            if (r.RecordDate is not { } date) continue;
            var title = r.RecordType.ToString();
            var detail = r.DefaulterType is not null
                ? $"{r.DefaulterType}{(r.Bank is not null ? $" — {r.Bank}" : "")}{(r.AmountCrore is { } amt ? $" — ₹{amt:N2} Cr" : "")}"
                : r.Description;
            yield return new CorporateTimelineEvent(date, TimelineEventCategory.Compliance, title, detail,
                Provenance(r, nameof(ComplianceRecord), r.ComplianceRecordId));
        }
    }
}
