using System.Globalization;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>What was asked of the portal: the tribunal and the case number looked up.</summary>
public sealed record DrtLookup(int TribunalId, CaseNumberParts Expected);

/// <summary>The two questions of plan section 4.3, answered separately:
/// <b>Identity</b> - did the portal return the case we asked for (tribunal, type, number, year)? Independent of any company; it decides whether the observation is verified.
/// <b>Link</b> - is the request's subject really a party? Decides whether the case is published into that request. A rejected link never blocks another request.</summary>
public sealed record DrtVerification(
    bool IdentityVerified, string IdentityReason, PartyMatchResult? Party, string? MatchedSide, bool RoleConflict, string? RoleNote)
{
    public PartyMatchOutcome LinkOutcome => Party?.Outcome ?? PartyMatchOutcome.Rejected;
}

/// <summary>The observation for the projection plus the verification, ready for <c>LiveProjectionStore.PublishAsync</c> (the caller adds the registry case id).</summary>
public sealed record DrtMappedObservation(ObservationOutcome Outcome, bool Verified, ParsedCaseSnapshot? Snapshot, DrtVerification? Verification, string? Message);

/// <summary>Maps a DRT lookup into a compact parsed snapshot and checks identity (plan sections 4.1, 4.3). The raw response is never carried on: only parsed values are kept.</summary>
public static class DrtObservationMapper
{
    public static DrtMappedObservation Map(
        DrtCaseLookupResult result, DrtLookup lookup, DrtTribunal? tribunal, IEnumerable<string> subjectNames, IEnumerable<string>? formerNames,
        string? storedDirection, DateOnly todayIst)
    {
        switch (result.Status)
        {
            case DrtLookupStatus.NotFoundCandidate:
                return new(ObservationOutcome.NotFound, false, null, null, result.Message);
            case DrtLookupStatus.Unavailable:
            case DrtLookupStatus.Error:
                return new(ObservationOutcome.Error, false, null, null, result.Message);
        }
        if (result.Case is not { } details) return new(ObservationOutcome.Error, false, null, null, "found without a case body");

        var verification = Verify(details, lookup, subjectNames, formerNames, storedDirection);
        var snapshot = ToSnapshot(details, tribunal, todayIst);
        return verification.IdentityVerified
            ? new(ObservationOutcome.Found, true, snapshot, verification, null)
            : new(ObservationOutcome.IdentityRejected, false, snapshot, verification, verification.IdentityReason);
    }

    /// <summary>The returned tribunal, type, number and year must equal the lookup; the portal returns whatever case holds a number, so this is not optional.</summary>
    public static DrtVerification Verify(DrtCaseDetails d, DrtLookup lookup, IEnumerable<string> subjectNames, IEnumerable<string>? formerNames, string? storedDirection)
    {
        var expected = lookup.Expected;
        var got = d.CaseNumber;
        string? identityProblem =
            d.TribunalId != lookup.TribunalId ? $"the portal returned a case of tribunal {d.TribunalId}, not {lookup.TribunalId}"
            : expected.TypeCode is null || got.TypeCode is null ? "the case type could not be compared"
            : expected.TypeCode != got.TypeCode ? $"the portal returned type {got.TypeCode}, not {expected.TypeCode}"
            : expected.No is null || got.No != expected.No ? $"the portal returned number {got.No}, not {expected.No}"
            : expected.Year is null || got.Year != expected.Year ? $"the portal returned year {got.Year}, not {expected.Year}"
            : null;
        if (identityProblem is not null) return new(false, identityProblem, null, null, false, null);

        // Party: whole-name against every party the portal lists, on either side.
        var subjects = subjectNames.ToList();
        var former = formerNames?.ToList();
        var candidates = new List<(string? Name, string Side)>
        {
            (d.PetitionerName, "petitioner"), (d.AdditionalPetitioner, "petitioner"), (d.RespondentName, "respondent"), (d.AdditionalRespondent, "respondent")
        };
        PartyMatchResult? best = null;
        string? bestSide = null;
        foreach (var (name, side) in candidates)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var r = WholeNamePartyMatcher.Match(name, subjects, former);
            if (best is null || Rank(r.Outcome) > Rank(best.Outcome)) { best = r; bestSide = side; }
        }
        best ??= new PartyMatchResult(PartyMatchOutcome.Rejected, "no-match", null, null);

        // Role: a stated direction ("by" / "against" the subject) must agree with the side the party is on. A conflict is recorded, not resolved by guessing.
        var conflict = false;
        string? note = null;
        if (best.Outcome == PartyMatchOutcome.Verified && !string.IsNullOrWhiteSpace(storedDirection))
        {
            var expectedSide = storedDirection.Trim().ToLowerInvariant() switch { "by" => "petitioner", "against" => "respondent", _ => null };
            if (expectedSide is not null && expectedSide != bestSide)
            {
                conflict = true;
                note = $"the stored direction is {storedDirection} but the portal lists the subject as {bestSide}";
            }
        }
        return new(true, "tribunal, type, number and year match the lookup", best, best.Outcome == PartyMatchOutcome.Rejected ? null : bestSide, conflict, note);
    }

    private static int Rank(PartyMatchOutcome o) => o switch { PartyMatchOutcome.Verified => 2, PartyMatchOutcome.Ambiguous => 1, _ => 0 };

    /// <summary>The portal's fields as the projection's compact snapshot. Dates are written day-first (d-M-yyyy), the form the rest of the module reads.</summary>
    public static ParsedCaseSnapshot ToSnapshot(DrtCaseDetails d, DrtTribunal? tribunal, DateOnly todayIst)
    {
        // The last hearing is the latest listing that is not in the future; a listing today or later is the next hearing, which the portal reports itself.
        var lastHearing = d.Hearings.Where(h => h.CauseListDate is { } c && c <= todayIst).Select(h => h.CauseListDate!.Value).DefaultIfEmpty().Max();
        return new ParsedCaseSnapshot(
            Status: d.Status, Stage: null, Court: tribunal?.Name ?? d.CourtName, FilingDate: Format(d.DateOfFiling),
            LastHearingDate: lastHearing == default ? null : Format(lastHearing), NextHearingDate: Format(d.NextListingDate), DecisionDate: Format(d.DateOfDisposal),
            DisposalNature: d.DisposalNature, SuitAmount: d.SuitAmount?.ToString("0.##", CultureInfo.InvariantCulture), HearingCount: d.Hearings.Count);
    }

    private static string? Format(DateOnly? date) => date?.ToString("d-M-yyyy", CultureInfo.InvariantCulture);
}
