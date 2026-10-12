using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ConsumerObservationMapperTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly string[] Subject = ["Acme Technologies Limited"];

    private static CaseNumberParts P(string number) => CaseNumberNormaliser.Parse(new CaseNumberInput(number));

    private static ConsumerCaseDetails CreateDetails(
        string caseNumber = "CC/599/2017",
        int? commissionId = 12270497,
        string? complainant = "John Doe",
        string? respondent = "Acme Technologies Limited",
        string stage = "PENDING") =>
        new(
            CaseNumber: caseNumber,
            NormalizedCaseNumber: P(caseNumber),
            FilingReferenceNumber: 998877,
            CaseFilingDate: new DateOnly(2017, 6, 1),
            FiledInCommissionName: "Thane District Commission",
            Complainant: complainant,
            Respondent: respondent,
            CaseStage: stage,
            DateOfNextHearing: new DateOnly(2026, 11, 15),
            ComplainantAdvocate: null,
            RespondentAdvocate: null,
            ApplicationsFiled: [],
            AttachedLowerCourt: [],
            Hearings: [],
            CommissionTypeId: 3,
            CommissionId: commissionId,
            CommissionName: "Thane District Commission",
            StateName: "Maharashtra",
            DistrictName: "Thane",
            IsBench: false,
            StatusParts: CaseStatusVocabulary.Map(stage),
            DerivedDecisionDate: null,
            IsDecisionDateDerived: false,
            IsTerminal: false);

    [Fact]
    public void When_CommissionId_is_null_identity_fails_closed_and_is_rejected()
    {
        // Response returns matching case number but CommissionId is null (cannot be confirmed)
        var details = CreateDetails(commissionId: null);
        var lookup = new ConsumerLookup(12270497, P("CC/599/2017"));

        var mapped = ConsumerObservationMapper.Map(
            ConsumerCaseLookupResult.Found(details),
            lookup,
            Subject,
            formerNames: null,
            storedDirection: null,
            todayIst: Today);

        Assert.Equal(ObservationOutcome.IdentityRejected, mapped.Outcome);
        Assert.False(mapped.Verified);
        Assert.NotNull(mapped.Verification);
        Assert.False(mapped.Verification.IdentityVerified);
        Assert.Contains("commission", mapped.Verification.IdentityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void When_CommissionId_mismatches_lookup_identity_is_rejected()
    {
        // Response returns CommissionId 11270000 (State commission) instead of looked-up 12270497
        var details = CreateDetails(commissionId: 11270000);
        var lookup = new ConsumerLookup(12270497, P("CC/599/2017"));

        var mapped = ConsumerObservationMapper.Map(
            ConsumerCaseLookupResult.Found(details),
            lookup,
            Subject,
            formerNames: null,
            storedDirection: null,
            todayIst: Today);

        Assert.Equal(ObservationOutcome.IdentityRejected, mapped.Outcome);
        Assert.False(mapped.Verified);
        Assert.Contains("11270000, not 12270497", mapped.Verification?.IdentityReason);
    }

    [Fact]
    public void When_CommissionId_and_parties_match_it_is_found_and_verified()
    {
        var details = CreateDetails(commissionId: 12270497);
        var lookup = new ConsumerLookup(12270497, P("CC/599/2017"));

        var mapped = ConsumerObservationMapper.Map(
            ConsumerCaseLookupResult.Found(details),
            lookup,
            Subject,
            formerNames: null,
            storedDirection: null,
            todayIst: Today);

        Assert.Equal(ObservationOutcome.Found, mapped.Outcome);
        Assert.True(mapped.Verified);
        Assert.True(mapped.Verification?.IdentityVerified);
        Assert.Equal(PartyMatchOutcome.Verified, mapped.Verification?.LinkOutcome);
        Assert.Equal("respondent", mapped.Verification?.MatchedSide);
    }

    [Fact]
    public void When_CommissionId_matches_but_parties_mismatch_identity_is_verified_but_link_is_rejected()
    {
        var details = CreateDetails(
            commissionId: 12270497,
            complainant: "Unrelated Person A",
            respondent: "Unrelated Person B");
        var lookup = new ConsumerLookup(12270497, P("CC/599/2017"));

        var mapped = ConsumerObservationMapper.Map(
            ConsumerCaseLookupResult.Found(details),
            lookup,
            Subject,
            formerNames: null,
            storedDirection: null,
            todayIst: Today);

        Assert.Equal(ObservationOutcome.Found, mapped.Outcome);
        Assert.True(mapped.Verified);
        Assert.True(mapped.Verification?.IdentityVerified);
        Assert.Equal(PartyMatchOutcome.Rejected, mapped.Verification?.LinkOutcome);
    }
}

