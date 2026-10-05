using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Tests;

/// <summary>Tests AiChargesNarrativeService's internal SelectChargesForNarrative/BuildPrompt/Validate
/// methods directly — same boundary as AiCrossSectionAnalysisServiceValidationTests (no live-call testing,
/// since the constructor eagerly loads Google Cloud credentials from disk).</summary>
public class AiChargesNarrativeServiceValidationTests
{
    private static RocCharge Charge(long id, string number, decimal? amount, string status = "Open",
        DateOnly? satisfactionDate = null, List<RocChargeEvent>? events = null) => new()
    {
        ChargeId = id, RocChargeNumber = number, LatestChargeHolderRaw = $"HOLDER {number}",
        LatestChargeHolderNormalized = $"HOLDER {number}", ChargeStatus = status, CurrentAmount = amount,
        SatisfactionDate = satisfactionDate, Events = events ?? []
    };

    private static RocChargeEvent Event(long id, DateOnly? eventDate, string? propertyParticulars = null,
        long chargeEventId = 0) => new()
    {
        ChargeEventId = chargeEventId, RocChargeId = id, EventType = ChargeEventType.Creation,
        EventDate = eventDate, PropertyParticulars = propertyParticulars
    };

    // ── SelectChargesForNarrative ────────────────────────────────────────────

    [Fact]
    public void SelectChargesForNarrative_OnlySelectsOpenCharges_IncludingBlankStatusFailOpen()
    {
        var open1 = Charge(1, "C1", 500m);
        // Blank/nonstandard ChargeStatus with no SatisfactionDate must count as open per
        // DossierComputations.IsOpenCharge's own fail-open semantics — the caller applies
        // OpenChargesByAmount before this method ever sees the list, so this proves the caller's contract
        // is honoured end to end via the same charges.
        var openBlankStatus = Charge(2, "C2", 300m, status: "");
        var satisfied = Charge(3, "C3", 999m, status: "Satisfied", satisfactionDate: new DateOnly(2020, 1, 1));

        var openOnly = MCAROC_Analysis.Models.DossierComputations.OpenChargesByAmount([open1, openBlankStatus, satisfied]);
        var selected = AiChargesNarrativeService.SelectChargesForNarrative(openOnly);

        Assert.Equal(2, selected.Count);
        Assert.DoesNotContain(selected, s => s.Charge.ChargeId == 3);
    }

    [Fact]
    public void SelectChargesForNarrative_OrdersByAmountDescending_CappedAtN()
    {
        var charges = Enumerable.Range(1, AiChargesNarrativeService.MaxChargesConsidered + 5)
            .Select(i => Charge(i, $"C{i}", i)).ToList();

        var openOnly = MCAROC_Analysis.Models.DossierComputations.OpenChargesByAmount(charges);
        var selected = AiChargesNarrativeService.SelectChargesForNarrative(openOnly);

        Assert.Equal(AiChargesNarrativeService.MaxChargesConsidered, selected.Count);
        // Largest amounts first: charge N+5 (amount N+5) down to charge 6 (amount 6, the Nth largest).
        Assert.Equal(AiChargesNarrativeService.MaxChargesConsidered + 5, selected[0].Charge.ChargeId);
        Assert.Equal(6, selected[^1].Charge.ChargeId);
    }

    [Fact]
    public void SelectChargesForNarrative_ResolvesLatestPropertyBearingEvent_ByDateThenId_NotInsertionOrder()
    {
        // Seeded deliberately out of chronological order, with two events sharing the same EventDate — the
        // resolved "latest" must be the one with the later ChargeEventId on that shared date, not whichever
        // happens to be first in the in-memory list (proving explicit ordering, not a bare LastOrDefault
        // against EF's .Include()-populated, order-unguaranteed collection).
        var events = new List<RocChargeEvent>
        {
            Event(1, new DateOnly(2022, 6, 1), "Later chronologically, but seeded first", chargeEventId: 10),
            Event(1, new DateOnly(2020, 1, 1), "Earliest event", chargeEventId: 5),
            Event(1, new DateOnly(2022, 6, 1), "Same date as the first, higher id — this one wins", chargeEventId: 11),
        };
        var charge = Charge(1, "C1", 500m, events: events);

        var selected = AiChargesNarrativeService.SelectChargesForNarrative([charge]);

        var only = Assert.Single(selected);
        Assert.Equal("Same date as the first, higher id — this one wins", only.RepresentativeEvent!.PropertyParticulars);
    }

    [Fact]
    public void SelectChargesForNarrative_ChargeWithNoPropertyBearingEvent_HasNullRepresentativeEvent()
    {
        var charge = Charge(1, "C1", 500m, events: [Event(1, new DateOnly(2020, 1, 1), propertyParticulars: null)]);

        var selected = AiChargesNarrativeService.SelectChargesForNarrative([charge]);

        Assert.Null(Assert.Single(selected).RepresentativeEvent);
    }

    [Fact]
    public void SelectChargesForNarrative_StopsEarlyOncePromptCharacterBudgetIsExceeded()
    {
        var longText = new string('x', AiChargesNarrativeService.MaxFieldLength);
        var charges = Enumerable.Range(1, AiChargesNarrativeService.MaxChargesConsidered)
            .Select(i => Charge(i, $"C{i}", AiChargesNarrativeService.MaxChargesConsidered - i,
                events: [Event(i, new DateOnly(2020, 1, 1), longText, chargeEventId: i)]))
            .ToList();

        var selected = AiChargesNarrativeService.SelectChargesForNarrative(charges);

        // With every charge carrying a maximal-length PropertyParticulars field, the running budget must
        // cut the list short of the full MaxChargesConsidered cap.
        Assert.True(selected.Count < AiChargesNarrativeService.MaxChargesConsidered);
        Assert.NotEmpty(selected);
    }

    // ── BuildPrompt ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildPrompt_DisclosesLargestNOfMOpenCharges()
    {
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);

        var prompt = AiChargesNarrativeService.BuildPrompt(selected, totalOpenChargeCount: 312);

        Assert.Contains("largest 1 of 312 open charges", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPrompt_DoesNotClaimToMechanicallyEnforcePartyNameFabrication()
    {
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);

        var prompt = AiChargesNarrativeService.BuildPrompt(selected, totalOpenChargeCount: 1);

        // The prompt instructs the model not to invent a party name, but Validate only checks numbers —
        // the prompt text must not overclaim mechanical enforcement of that instruction.
        Assert.Contains("not mechanically verified downstream", prompt);
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_DropsOneUnsupportedNumberPoint_WithoutFailingTheWholeOutcome()
    {
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);
        var response = """
            {
              "summary": "The largest charge is held by HOLDER C1.",
              "notableCollateralPoints": [
                { "text": "This charge covers an unprecedented ₹999 crore facility.", "chargeIds": [1] },
                { "text": "Consortium arrangement worth a second look.", "chargeIds": [1] }
              ]
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.True(outcome.Success);
        var point = Assert.Single(outcome.Narrative!.NotableCollateralPoints);
        Assert.Equal("Consortium arrangement worth a second look.", point.Text);
    }

    [Fact]
    public void Validate_DropsPointReferencingAChargeIdNotAmongThoseSent()
    {
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);
        var response = """
            {
              "summary": "Summary text.",
              "notableCollateralPoints": [{ "text": "References a charge never sent.", "chargeIds": [999] }]
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.True(outcome.Success);
        Assert.Empty(outcome.Narrative!.NotableCollateralPoints);
    }

    [Fact]
    public void Validate_SummaryWithUnsupportedNumber_FailsTheWholeOutcome()
    {
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);
        var response = """
            {
              "summary": "The largest charge secures an unprecedented ₹999 crore facility.",
              "notableCollateralPoints": []
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.False(outcome.Success);
        Assert.Contains("Summary", outcome.FailureReason);
    }

    [Fact]
    public void Validate_SummaryWithSupportedAmount_Preserved()
    {
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);
        var response = """
            {
              "summary": "The largest charge, held by HOLDER C1, is valued at ₹500 crore.",
              "notableCollateralPoints": []
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.True(outcome.Success);
        Assert.Equal("The largest charge, held by HOLDER C1, is valued at ₹500 crore.", outcome.Narrative!.Summary);
    }

    [Fact]
    public void Validate_EmptyResponse_FailsGracefully()
    {
        var outcome = AiChargesNarrativeService.Validate("", [], totalOpenChargeCount: 0);
        Assert.False(outcome.Success);
    }

    [Fact]
    public void Validate_InvalidJson_FailsGracefullyWithReason()
    {
        var outcome = AiChargesNarrativeService.Validate("not json", [], totalOpenChargeCount: 0);
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.FailureReason);
    }

    [Fact]
    public void Validate_MissingSummary_FailsGracefully()
    {
        var outcome = AiChargesNarrativeService.Validate("""{ "notableCollateralPoints": [] }""", [], totalOpenChargeCount: 0);
        Assert.False(outcome.Success);
    }

    [Fact]
    public void Validate_CapsNotableCollateralPointsAtFive()
    {
        // Letters, not digits, in each point's text — this test is about the cap, kept independent of the
        // numeric-fabrication check exercised separately below.
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);
        var letters = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
        var points = string.Join(",", letters.Select(l => $$"""{ "text": "Point {{l}}", "chargeIds": [1] }"""));
        var response = $$"""{ "summary": "Summary.", "notableCollateralPoints": [{{points}}] }""";

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.Equal(5, outcome.Narrative!.NotableCollateralPoints.Count);
    }

    [Fact]
    public void Validate_SummaryWithBareUnsupportedNumber_NoCurrencyMarker_FailsTheWholeOutcome()
    {
        // #218 review: a fabricated figure with no ₹/Rs/crore marker at all — e.g. "999" bare — must still
        // be caught, not just the currency-prefixed/suffixed forms.
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);
        var response = """
            {
              "summary": "The largest charge secures an unprecedented 999 in facilities.",
              "notableCollateralPoints": []
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.False(outcome.Success);
        Assert.Contains("Summary", outcome.FailureReason);
    }

    [Fact]
    public void Validate_SummaryClaimingAFabricatedChargeCount_FailsTheWholeOutcome()
    {
        // #218 review: "5 material charges" when only 1 charge was ever sent — the blanket 1-10 allow-list
        // previously let any small bare count through regardless of what was actually shown to the model.
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m)]);
        var response = """
            {
              "summary": "There are 5 material charges totalling 999 secured against the company.",
              "notableCollateralPoints": []
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.False(outcome.Success);
    }

    [Fact]
    public void Validate_SummaryReferencingTheRealCoveredAndTotalChargeCounts_IsPreserved()
    {
        // The fix must not over-correct: a genuine "the largest of the N shown, out of M total" reference —
        // using the real, server-computed counts — is a legitimate, verifiable claim and must survive.
        var selected = AiChargesNarrativeService.SelectChargesForNarrative(
            [Charge(1, "C1", 500m), Charge(2, "C2", 400m), Charge(3, "C3", 300m)]);
        var response = """
            {
              "summary": "This narrative covers the largest 3 of 10 open charges by amount.",
              "notableCollateralPoints": []
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 10);

        Assert.True(outcome.Success);
        Assert.Equal("This narrative covers the largest 3 of 10 open charges by amount.", outcome.Narrative!.Summary);
    }

    [Fact]
    public void Validate_SummaryReferencingARealChargeIdOrNumberFromThePrompt_IsPreserved()
    {
        // BuildPrompt shows the model both ChargeId and RocChargeNumber for every selected charge — a prose
        // reference to either (not just the structured chargeIds field checked separately) must not be
        // rejected as invented, since it's a real value the model was actually shown.
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(42, "101265730", 500m)]);
        var response = """
            {
              "summary": "Charge 42 (source charge number 101265730) is the largest on record.",
              "notableCollateralPoints": []
            }
            """;

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 1);

        Assert.True(outcome.Success);
    }

    [Fact]
    public void Validate_OverwritesCoverageCountsWithServerComputedValues_RegardlessOfModelClaims()
    {
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 500m), Charge(2, "C2", 400m)]);
        // Model's JSON has no coverage fields at all (Validate never reads them from the DTO) — the
        // service-computed selected.Count/totalOpenChargeCount must be what lands on the result regardless.
        var response = """{ "summary": "Summary.", "notableCollateralPoints": [] }""";

        var outcome = AiChargesNarrativeService.Validate(response, selected, totalOpenChargeCount: 312);

        Assert.Equal(2, outcome.Narrative!.CoveredChargeCount);
        Assert.Equal(312, outcome.Narrative.TotalOpenChargeCount);
    }

    // ── numbers quoted from the deed wording the model was shown ─────────────

    private static string NarrativeJson(string summary) => System.Text.Json.JsonSerializer.Serialize(new { summary, notableCollateralPoints = Array.Empty<object>() });

    [Fact]
    public void Validate_AcceptsANumberThatAppearsInTheChargeWordingTheModelWasShown()
    {
        var ev = new RocChargeEvent
        {
            ChargeEventId = 1, RocChargeId = 1, EventType = ChargeEventType.Creation, EventDate = new DateOnly(2026, 2, 25),
            InstrumentDescription = "Deed of Hypothecation dated 25.02.2026 for 56 new centres, limit Rs 700 Lakh at 9.5%",
            PropertyParticulars = "Stock at Plot No. 14, Sector 9"
        };
        var charge = Charge(1, "C1", 160m, events: [ev]);
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([charge]);

        var outcome = AiChargesNarrativeService.Validate(
            NarrativeJson("The single open charge, 160.00, secures 56 new centres under a deed dated 25.02.2026 with a Rs 700 Lakh limit at 9.5%."), selected, 1);

        Assert.True(outcome.Success, outcome.FailureReason);
    }

    [Fact]
    public void Validate_DoesNotLetARoundedFormOfAWordingFigureThrough()
    {
        // Source says 9.5%: the narrative may say 9.5%, never 10% (a rounded form of a figure that is not in the data).
        var ev = new RocChargeEvent { ChargeEventId = 1, RocChargeId = 1, EventType = ChargeEventType.Creation, EventDate = new DateOnly(2026, 2, 25), InstrumentDescription = "Hypothecation of stock at 9.5% interest", PropertyParticulars = "Stock at Plot No. 14, Sector 9" };
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 160m, events: [ev])]);

        var faithful = AiChargesNarrativeService.Validate(NarrativeJson("The charge carries interest at 9.5%."), selected, 1);
        Assert.True(faithful.Success, faithful.FailureReason);
        var rounded = AiChargesNarrativeService.Validate(NarrativeJson("The charge carries interest at 10%."), selected, 1);
        Assert.False(rounded.Success);
        Assert.Contains("number not present", rounded.FailureReason);

        // A nearby invented value is not the source figure either: 9.54 rounds to 9.5 but is not written anywhere.
        Assert.False(AiChargesNarrativeService.Validate(NarrativeJson("The charge carries interest at 9.54%."), selected, 1).Success);
    }

    [Fact]
    public void Validate_ComparesFiguresWithTheirUnits()
    {
        static IReadOnlyList<AiChargesNarrativeService.SelectedCharge> Shown(string wording) => AiChargesNarrativeService.SelectChargesForNarrative([Charge(1, "C1", 160m, events: [new RocChargeEvent
            { ChargeEventId = 1, RocChargeId = 1, EventType = ChargeEventType.Creation, EventDate = new DateOnly(2026, 2, 25), InstrumentDescription = wording, PropertyParticulars = "Stock at Plot 14" }])]);
        static bool Ok(string wording, string claim) => AiChargesNarrativeService.Validate(NarrativeJson(claim), Shown(wording), 1).Success;

        // A bare 9.5 in the source does not support a claim of 9.5% (nor 9.5x, nor a rupee amount).
        Assert.False(Ok("Version 9.5 of the deed", "The charge carries interest at 9.5%."));
        Assert.False(Ok("Version 9.5 of the deed", "The charge is for Rs 9.5 crore."));
        // A percentage in the source supports a percentage, however it is written, but not a bare number or a multiple.
        Assert.True(Ok("Interest at 9.5% p.a.", "The charge carries interest at 9.5%."));
        Assert.True(Ok("Interest at 9.5% p.a.", "The charge carries interest at 9.5 percent."));
        Assert.False(Ok("Interest at 9.5% p.a.", "The charge covers 9.5 branches."));
        Assert.False(Ok("Interest at 9.5% p.a.", "Cover of 9.5x."));
        // Scale matters for amounts quoted in the wording.
        Assert.True(Ok("limit Rs 700 Lakh", "A limit of Rs 700 lakh applies."));
        Assert.False(Ok("limit Rs 700 Lakh", "A limit of Rs 700 crore applies."));
    }

    [Fact]
    public void Validate_StillRejectsADerivedTotalOrAnInventedFigure()
    {
        var a = Charge(1, "C1", 20m, events: [new RocChargeEvent { ChargeEventId = 1, RocChargeId = 1, EventType = ChargeEventType.Creation, InstrumentDescription = "Hypothecation of stock" }]);
        var b = Charge(2, "C2", 15m);
        var selected = AiChargesNarrativeService.SelectChargesForNarrative([a, b]);

        var total = AiChargesNarrativeService.Validate(NarrativeJson("Two open charges totalling 35.00."), selected, 2);       // a sum: in nobody's data
        var invented = AiChargesNarrativeService.Validate(NarrativeJson("Charge C1 secures 999 branches."), selected, 2);

        Assert.False(total.Success);
        Assert.Contains("number not present", total.FailureReason);
        Assert.False(invented.Success);
    }
}
