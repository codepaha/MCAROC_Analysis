using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class DrtObservationMapperTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly string[] Subject = ["Alpha Industries Limited"];
    private static readonly DrtTribunal Tribunal = new(7, "DRT Mumbai 1", false, "Maharashtra", "Mumbai", "1");

    private static CaseNumberParts P(string number) => CaseNumberNormaliser.Parse(new CaseNumberInput(number));

    private static DrtCaseDetails Details(string caseNumber = "OA/413/2022", int tribunalId = 7, string? petitioner = "STATE BANK OF INDIA", string? respondent = "ALPHA INDUSTRIES LIMITED",
        string? additionalRespondent = null, string status = "PENDING", DateOnly? next = null, DateOnly? disposal = null, IReadOnlyList<DrtHearing>? hearings = null) =>
        new(tribunalId, 1, "Original Application", "OA", "413", 2022, P(caseNumber), null, null, null, new DateOnly(2022, 12, 5), status,
            CaseStatusVocabulary.Map(status), null, disposal, null, next, null, "1", "DRT Mumbai 1", null, 5000000m, petitioner, respondent, null, null, null, null, null, additionalRespondent,
            hearings ?? [], null);

    private static DrtHearing Hearing(int y, int m, int d) => new(new DateOnly(y, m, d), "Hearing", null, null, null, null, null);

    private static DrtMappedObservation Map(DrtCaseLookupResult result, string expected = "OA/413/2022", int tribunalId = 7, string? direction = null) =>
        DrtObservationMapper.Map(result, new DrtLookup(tribunalId, P(expected)), Tribunal, Subject, null, direction, Today);

    [Fact]
    public void A_matching_case_with_the_subject_as_respondent_is_found_verified_and_linked()
    {
        var m = Map(DrtCaseLookupResult.Found(Details()));
        Assert.Equal((ObservationOutcome.Found, true), (m.Outcome, m.Verified));
        Assert.True(m.Verification!.IdentityVerified);
        Assert.Equal(PartyMatchOutcome.Verified, m.Verification.LinkOutcome);
        Assert.Equal("respondent", m.Verification.MatchedSide);
    }

    [Theory]
    [InlineData("OA/414/2022", 7)]      // another number
    [InlineData("OA/413/2021", 7)]      // another year
    [InlineData("MA/413/2022", 7)]      // another type
    public void A_case_with_another_type_number_or_year_than_the_one_asked_for_is_identity_rejected_and_not_verified(string returned, int tribunalId)
    {
        var m = Map(DrtCaseLookupResult.Found(Details(caseNumber: returned, tribunalId: tribunalId)));
        Assert.Equal((ObservationOutcome.IdentityRejected, false), (m.Outcome, m.Verified));
        Assert.False(m.Verification!.IdentityVerified);
        Assert.NotNull(m.Message);
    }

    [Fact]
    public void The_same_number_at_another_tribunal_is_identity_rejected()
    {
        var m = Map(DrtCaseLookupResult.Found(Details(tribunalId: 9)));
        Assert.Equal(ObservationOutcome.IdentityRejected, m.Outcome);
        Assert.Contains("tribunal 9", m.Message);
    }

    [Fact]
    public void A_right_case_where_the_subject_is_not_a_party_is_verified_for_the_registry_but_the_link_is_rejected()
    {
        var m = Map(DrtCaseLookupResult.Found(Details(respondent: "ALPHA INDUSTRIES LIMITED EMPLOYEES COOPERATIVE SOCIETY")));
        Assert.Equal((ObservationOutcome.Found, true), (m.Outcome, m.Verified));          // the case is what we asked for
        Assert.Equal(PartyMatchOutcome.Rejected, m.Verification!.LinkOutcome);            // but this company is not a party
        Assert.Null(m.Verification.MatchedSide);
    }

    [Fact]
    public void A_differently_formed_name_gives_an_ambiguous_link()
    {
        var m = Map(DrtCaseLookupResult.Found(Details(respondent: "ALPHA INDUSTRIES PRIVATE LIMITED")));
        Assert.Equal(PartyMatchOutcome.Ambiguous, m.Verification!.LinkOutcome);
    }

    [Fact]
    public void Additional_parties_count_and_the_best_match_wins()
    {
        var m = Map(DrtCaseLookupResult.Found(Details(respondent: "SOME OTHER COMPANY", additionalRespondent: "ALPHA INDUSTRIES LTD")));
        Assert.Equal(PartyMatchOutcome.Verified, m.Verification!.LinkOutcome);
        Assert.Equal("respondent", m.Verification.MatchedSide);
    }

    [Fact]
    public void A_stated_direction_that_disagrees_with_the_side_is_recorded_as_a_conflict_not_resolved()
    {
        var conflict = Map(DrtCaseLookupResult.Found(Details()), direction: "by");   // the subject is the respondent, the vendor said the case is BY the subject
        Assert.True(conflict.Verification!.RoleConflict);
        Assert.Contains("respondent", conflict.Verification.RoleNote);
        Assert.Equal(PartyMatchOutcome.Verified, conflict.Verification.LinkOutcome);   // the link stands; the conflict is information

        Assert.False(Map(DrtCaseLookupResult.Found(Details()), direction: "against").Verification!.RoleConflict);
        Assert.False(Map(DrtCaseLookupResult.Found(Details()), direction: null).Verification!.RoleConflict);
    }

    [Fact]
    public void Not_found_unavailable_and_error_map_to_observations_that_cannot_change_a_projection()
    {
        var nf = Map(DrtCaseLookupResult.NotFoundCandidate());
        Assert.Equal((ObservationOutcome.NotFound, false, null), (nf.Outcome, nf.Verified, nf.Snapshot));
        Assert.Equal(ObservationOutcome.Error, Map(DrtCaseLookupResult.Unavailable("down")).Outcome);
        Assert.Equal(ObservationOutcome.Error, Map(DrtCaseLookupResult.Error("boom")).Outcome);
    }

    [Fact]
    public void The_snapshot_carries_parsed_values_dates_day_first_and_the_tribunal_name_but_no_raw_body()
    {
        var hearings = new[] { Hearing(2026, 8, 1), Hearing(2026, 9, 15), Hearing(2026, 10, 30) };
        var m = Map(DrtCaseLookupResult.Found(Details(next: new DateOnly(2026, 10, 30), hearings: hearings), raw: "{\"raw\":true}"));
        var s = m.Snapshot!;
        Assert.Equal(("PENDING", "DRT Mumbai 1", "5-12-2022", "15-9-2026", "30-10-2026", "5000000", 3), (s.Status, s.Court, s.FilingDate, s.LastHearingDate, s.NextHearingDate, s.SuitAmount, s.HearingCount));
        Assert.DoesNotContain("raw", System.Text.Json.JsonSerializer.Serialize(m));
    }

    [Fact]
    public void A_disposed_case_carries_its_decision_date_and_the_snapshot_round_trips_into_the_projection_engine()
    {
        var m = Map(DrtCaseLookupResult.Found(Details(status: "DISPOSAL", disposal: new DateOnly(2026, 9, 1), hearings: [Hearing(2026, 9, 1)])));
        Assert.Equal("1-9-2026", m.Snapshot!.DecisionDate);
        var obs = new ProjectionObservation(1, ObservationSource.DrtPortal, m.Outcome, m.Verified, DateTime.UtcNow, m.Snapshot);
        var result = LiveProjectionEngine.Apply(LiveProjectionState.Empty, obs);
        Assert.Equal(ProjectionOutcome.Applied, result.Outcome);
        Assert.Equal("DISPOSAL", result.State.Status);
        Assert.True(result.State.Sources[LiveProjectionEngine.FNextHearing].ConfirmedEmpty);    // complete response with no next listing
    }

    [Fact]
    public void An_identity_rejected_observation_is_not_applied_by_the_engine()
    {
        var m = Map(DrtCaseLookupResult.Found(Details(caseNumber: "OA/999/2022")));
        var obs = new ProjectionObservation(1, ObservationSource.DrtPortal, m.Outcome, m.Verified, DateTime.UtcNow, m.Snapshot!);
        Assert.Equal(ProjectionOutcome.IgnoredNotVerified, LiveProjectionEngine.Apply(LiveProjectionState.Empty, obs).Outcome);
    }

    // ---- a response that omits its own identifiers must not match the lookup by echo ----

    private static async Task<DrtCaseLookupResult> LookupAsync(string json, string no = "413", int year = 2022)
    {
        var http = new HttpClient { BaseAddress = new Uri("https://example.test/drtapi/") };
        var options = new DrtClientOptions { RequestsPerSecond = 1000.0, TimeoutSeconds = 5, MaxRetries = 1 };
        var client = new DrtClient(http, Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Logging.Abstractions.NullLogger<DrtClient>.Instance, null,
            new DrtCircuitBreaker(), (req, ct) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") }));
        return await client.GetCaseByNumberAsync(7, 1, no, year);
    }

    [Theory]
    [InlineData("{\"casetype\":\"OA\",\"caseyear\":\"2022\",\"casestatus\":\"PENDING\",\"applicant\":\"X\",\"respondent\":\"ALPHA INDUSTRIES LIMITED\"}")]                      // no caseno
    [InlineData("{\"caseno\":\"413\",\"casetype\":\"OA\",\"casestatus\":\"PENDING\",\"applicant\":\"X\",\"respondent\":\"ALPHA INDUSTRIES LIMITED\"}")]                      // no caseyear
    [InlineData("{\"caseno\":\"413\",\"caseyear\":\"2022\",\"casestatus\":\"PENDING\",\"applicant\":\"X\",\"respondent\":\"ALPHA INDUSTRIES LIMITED\"}")]                       // no casetype
    [InlineData("{\"casestatus\":\"PENDING\",\"applicant\":\"X\",\"respondent\":\"ALPHA INDUSTRIES LIMITED\"}")]                                                                // none of the three
    public async Task A_response_that_omits_its_case_number_year_or_type_is_not_verified_even_though_the_request_supplied_them(string json)
    {
        var result = await LookupAsync(json);
        Assert.True(result.IsFound);
        var m = DrtObservationMapper.Map(result, new DrtLookup(7, P("OA/413/2022")), Tribunal, Subject, null, null, Today);
        Assert.Equal((ObservationOutcome.IdentityRejected, false), (m.Outcome, m.Verified));
        Assert.False(m.Verification!.IdentityVerified);
    }

    [Fact]
    public async Task A_response_that_returns_all_three_identifiers_and_matches_is_still_verified()
    {
        var result = await LookupAsync("{\"caseno\":\"413\",\"casetype\":\"OA\",\"caseyear\":\"2022\",\"casestatus\":\"PENDING\",\"applicant\":\"X\",\"respondent\":\"ALPHA INDUSTRIES LIMITED\"}");
        var m = DrtObservationMapper.Map(result, new DrtLookup(7, P("OA/413/2022")), Tribunal, Subject, null, null, Today);
        Assert.Equal((ObservationOutcome.Found, true), (m.Outcome, m.Verified));
    }
}
