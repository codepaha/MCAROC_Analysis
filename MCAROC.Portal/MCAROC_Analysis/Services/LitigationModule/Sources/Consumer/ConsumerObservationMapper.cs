using System.Globalization;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>What was looked up on the consumer portal: commission ID and expected case number.</summary>
public sealed record ConsumerLookup(int CommissionId, CaseNumberParts Expected);

/// <summary>Verification verdict for a consumer case lookup (plan section 4.3):
/// <b>Identity</b> - did the portal return the case we asked for (commission, type, number, year)?
/// <b>Link</b> - is the request's subject really a complainant or respondent?</summary>
public sealed record ConsumerVerification(
    bool IdentityVerified,
    string IdentityReason,
    PartyMatchResult? Party,
    string? MatchedSide,
    bool RoleConflict,
    string? RoleNote)
{
    public PartyMatchOutcome LinkOutcome => Party?.Outcome ?? PartyMatchOutcome.Rejected;
}

/// <summary>The observation for the projection plus verification, ready for <c>LiveProjectionStore.PublishAsync</c>.</summary>
public sealed record ConsumerMappedObservation(
    ObservationOutcome Outcome,
    bool Verified,
    ParsedCaseSnapshot? Snapshot,
    ConsumerVerification? Verification,
    string? Message);

/// <summary>Maps a consumer case lookup into a compact parsed snapshot and checks identity (plan sections 4.1, 4.3).</summary>
public static class ConsumerObservationMapper
{
    public static ConsumerMappedObservation Map(
        ConsumerCaseLookupResult result,
        ConsumerLookup lookup,
        IEnumerable<string> subjectNames,
        IEnumerable<string>? formerNames,
        string? storedDirection,
        DateOnly todayIst)
    {
        switch (result.Status)
        {
            case ConsumerLookupStatus.NotFoundCandidate:
                return new(ObservationOutcome.NotFound, false, null, null, result.Message);
            case ConsumerLookupStatus.Unavailable:
            case ConsumerLookupStatus.Error:
                return new(ObservationOutcome.Error, false, null, null, result.Message);
        }

        if (result.Case is not { } details)
            return new(ObservationOutcome.Error, false, null, null, "found without a case body");

        var verification = Verify(details, lookup, subjectNames, formerNames, storedDirection);
        var snapshot = ToSnapshot(details, todayIst);
        return verification.IdentityVerified
            ? new(ObservationOutcome.Found, true, snapshot, verification, null)
            : new(ObservationOutcome.IdentityRejected, false, snapshot, verification, verification.IdentityReason);
    }

    /// <summary>The returned commission, type, number, and year must equal the lookup.</summary>
    public static ConsumerVerification Verify(
        ConsumerCaseDetails d,
        ConsumerLookup lookup,
        IEnumerable<string> subjectNames,
        IEnumerable<string>? formerNames,
        string? storedDirection)
    {
        var expected = lookup.Expected;
        var got = d.NormalizedCaseNumber;

        string? identityProblem =
            d.CommissionId.HasValue && d.CommissionId.Value != lookup.CommissionId
                ? $"the portal returned a case of commission {d.CommissionId}, not {lookup.CommissionId}"
                : expected.TypeCode is null || got.TypeCode is null
                    ? "the case type could not be compared"
                    : !string.Equals(expected.TypeCode, got.TypeCode, StringComparison.OrdinalIgnoreCase)
                        ? $"the portal returned type {got.TypeCode}, not {expected.TypeCode}"
                        : expected.No is null || !string.Equals(got.No, expected.No, StringComparison.OrdinalIgnoreCase)
                            ? $"the portal returned number {got.No}, not {expected.No}"
                            : got.YearSource is not (YearSource.Number or YearSource.Column)
                                ? "the portal did not return the case year itself (a year inferred from the filing date is not identity)"
                                : expected.Year is null || got.Year != expected.Year
                                    ? $"the portal returned year {got.Year}, not {expected.Year}"
                                    : null;

        if (identityProblem is not null)
            return new(false, identityProblem, null, null, false, null);

        var subjects = subjectNames.ToList();
        var former = formerNames?.ToList();
        var candidates = new List<(string? Name, string Side)>
        {
            (d.Complainant, "petitioner"),
            (d.Respondent, "respondent")
        };

        PartyMatchResult? best = null;
        string? bestSide = null;
        foreach (var (name, side) in candidates)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var r = WholeNamePartyMatcher.Match(name, subjects, former);
            if (best is null || Rank(r.Outcome) > Rank(best.Outcome))
            {
                best = r;
                bestSide = side;
            }
        }

        best ??= new PartyMatchResult(PartyMatchOutcome.Rejected, "no-match", null, null);

        var conflict = false;
        string? note = null;
        if (best.Outcome == PartyMatchOutcome.Verified && !string.IsNullOrWhiteSpace(storedDirection))
        {
            var expectedSide = storedDirection.Trim().ToLowerInvariant() switch
            {
                "by" => "petitioner",
                "against" => "respondent",
                _ => null
            };
            if (expectedSide is not null && expectedSide != bestSide)
            {
                conflict = true;
                note = $"the stored direction is {storedDirection} but the portal lists the subject as {bestSide}";
            }
        }

        return new(true, "commission, type, number and year match the lookup", best, best.Outcome == PartyMatchOutcome.Rejected ? null : bestSide, conflict, note);
    }

    private static int Rank(PartyMatchOutcome o) => o switch
    {
        PartyMatchOutcome.Verified => 2,
        PartyMatchOutcome.Ambiguous => 1,
        _ => 0
    };

    /// <summary>Builds compact parsed snapshot from consumer case details.</summary>
    public static ParsedCaseSnapshot ToSnapshot(ConsumerCaseDetails d, DateOnly todayIst)
    {
        var lastHearing = d.Hearings
            .Where(h => h.DateOfHearing is { } c && c <= todayIst)
            .Select(h => h.DateOfHearing!.Value)
            .DefaultIfEmpty()
            .Max();

        var status = d.StatusParts is not null
            ? (d.StatusParts.Bucket == LitigationCaseStatusBucket.Disposed ? "DISPOSED" : "PENDING")
            : (d.IsTerminal ? "DISPOSED" : "PENDING");
        var court = d.CommissionName ?? d.FiledInCommissionName;

        return new ParsedCaseSnapshot(
            Status: status,
            Stage: d.CaseStage,
            Court: court,
            FilingDate: Format(d.CaseFilingDate),
            LastHearingDate: lastHearing == default ? null : Format(lastHearing),
            NextHearingDate: Format(d.DateOfNextHearing),
            DecisionDate: Format(d.DerivedDecisionDate),
            DisposalNature: d.StatusParts?.Outcome,
            SuitAmount: null,
            HearingCount: d.Hearings.Count);
    }

    private static string? Format(DateOnly? date) => date?.ToString("d-M-yyyy", CultureInfo.InvariantCulture);
}

