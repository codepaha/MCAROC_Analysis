using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Movable identifiers: what counts as a specific asset, what never does, and how an order is searched for
/// exactly those identifiers. Precision over recall: a category word names no asset.</summary>
public class MovableIdentifierTests
{
    private static string[] Normalized(params string?[] texts) =>
        MovableIdentifierExtractor.Extract(texts).Select(i => i.Normalized).OrderBy(x => x).ToArray();

    // ── extraction ───────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Hypothecation of vehicle bearing registration no. MH12AB1234", "MH12AB1234")]
    [InlineData("Toyota Innova KA-01-AB-1234 financed", "KA01AB1234")]
    [InlineData("Truck DL 3C AB 1234 and trailer", "DL3CAB1234")]
    [InlineData("vehicle mh 12 ab 1234", "MH12AB1234")]
    [InlineData("Bharat series vehicle 22 BH 1234 AA", "22BH1234AA")]
    public void Vehicle_registrations_are_recognised_in_common_spellings(string text, string expected) =>
        Assert.Equal([expected], Normalized(text));

    [Theory]
    [InlineData("Sale agreement dated 12 AB 2021 and 14 CD 2020")]          // no state prefix / not a registration
    [InlineData("ZZ12AB1234")]                                                // ZZ is not an RTO code
    [InlineData("Plant and machinery, stock, book debts and other current assets")]
    [InlineData("Hypothecation of movable fixed assets of the company")]
    [InlineData("Serial No. 12")]                                             // too short to be specific
    [InlineData("Engine number")]                                             // a label with no number
    [InlineData("")]
    [InlineData(null)]
    public void Category_words_and_non_identifiers_are_never_extracted(string? text) =>
        Assert.Empty(MovableIdentifierExtractor.Extract(text));

    [Fact]
    public void Labelled_engine_chassis_and_serial_numbers_are_recognised_with_their_kind()
    {
        var ids = MovableIdentifierExtractor.Extract(
            "Vehicle: Engine No. G4LCFU123456, Chassis No: MA3EWDE1S00123456. Machine serial no. SN-88123-A for the lathe.");

        Assert.Contains(ids, i => i.Kind == MovableIdentifierKind.EngineNumber && i.Normalized == "G4LCFU123456");
        Assert.Contains(ids, i => i.Kind == MovableIdentifierKind.ChassisNumber && i.Normalized == "MA3EWDE1S00123456");
        Assert.Contains(ids, i => i.Kind == MovableIdentifierKind.SerialNumber && i.Normalized == "SN88123A");
    }

    [Fact]
    public void A_survey_number_is_not_mistaken_for_a_serial_number()
    {
        // "S. No." is a survey/serial number of land, not of a machine: only "serial"/"machine" labels count.
        Assert.Empty(MovableIdentifierExtractor.Extract("Land bearing S. No. 123/4B and S.No. 998877 at Pune"));
    }

    [Fact]
    public void The_same_identifier_written_two_ways_is_one_asset()
    {
        var ids = MovableIdentifierExtractor.Extract("Vehicle MH12AB1234", "Also listed as MH 12 AB 1234");
        Assert.Single(ids);
    }

    [Fact]
    public void A_bare_vin_is_recognised_but_a_plain_word_or_number_is_not()
    {
        Assert.Equal(["MA3EWDE1S00123456"], Normalized("Vehicle MA3EWDE1S00123456 financed"));
        Assert.Empty(Normalized("INTERNATIONALISATION99 and 12345678901234567"));
    }

    // ── searching an order page ──────────────────────────────────────────────────────────────────────────
    private static MovableIdentifier Id(string normalized) =>
        MovableIdentifierExtractor.Extract(normalized).Single(i => i.Normalized == normalized);

    [Theory]
    [InlineData("The vehicle bearing No. MH12AB1234 was seized.")]
    [InlineData("The vehicle bearing No. MH 12 AB 1234 was seized.")]
    [InlineData("The vehicle bearing No. MH-12-AB-1234 was seized.")]
    [InlineData("the vehicle bearing no. mh12ab1234 was seized.")]
    public void An_order_naming_the_vehicle_in_any_spelling_is_a_hit(string page)
    {
        var hits = MovableIdentifierExtractor.FindInPage([Id("MH12AB1234")], 3, page);

        var hit = Assert.Single(hits);
        Assert.Equal(3, hit.PageNumber);
        Assert.Contains("seized", hit.Excerpt);
    }

    [Theory]
    [InlineData("The vehicle MH12AB12345 was seized.")]       // digit touching the end: a different number
    [InlineData("The vehicle XMH12AB1234 was seized.")]       // letter touching the start
    [InlineData("Fourteen MH 12 AB 123 4 words apart")]         // gap too wide between characters
    [InlineData("A different vehicle MH12AB9999 was seized.")]
    public void Near_misses_are_not_hits(string page) =>
        Assert.Empty(MovableIdentifierExtractor.FindInPage([Id("MH12AB1234")], 1, page));

    [Fact]
    public void Characters_that_only_line_up_across_word_boundaries_are_not_a_hit()
    {
        // The compact form of this text contains MH12AB1234, but no single token does.
        Assert.Empty(MovableIdentifierExtractor.FindInPage([Id("MH12AB1234")], 1, "Item MH 12 then some words AB 1234 follow."));
    }
}

/// <summary>The movable signal end to end through the linker.</summary>
public class ChargeLitigationMovableTests
{
    private static RocCharge Charge(long id, string number, string particulars, string propertyType = "Movable property (not being pledge)",
        string? securityTypesJson = "[\"Vehicle\"]") => new()
    {
        ChargeId = id, RocChargeNumber = number, LatestChargeHolderRaw = "State Bank of India", LatestChargeHolderNormalized = "STATE BANK OF INDIA",
        ChargeStatus = "Open", LatestSecurityTypesJson = securityTypesJson,
        Events = [new RocChargeEvent { ChargeEventId = id, PropertyType = propertyType, PropertyParticulars = particulars }]
    };

    private static LitigationCase Case(long id, string number, long orderId, string? court = "High Court of Bombay") => new()
    {
        LitigationCaseId = id, CaseNumber = number, Court = court, CaseStatus = "Pending", RespondentsJson = "[\"Coastal Projects Limited\"]",
        Orders = [new LitigationCaseOrder { LitigationCaseOrderId = orderId, LitigationCaseId = id, OrderDate = "02-03-2021", OrderType = "Interim Order" }]
    };

    private static Dictionary<long, LitigationOrderDocument> Docs(long orderId, string text) => new()
    {
        [orderId] = new LitigationOrderDocument { LitigationCaseOrderId = orderId, ExtractedText = text, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted }
    };

    [Fact]
    public void Order_naming_a_vehicle_in_the_charge_links_the_case_with_page_and_excerpt()
    {
        var charge = Charge(1, "CHG-7", "Hypothecation of vehicles: Toyota Innova MH12AB1234 and Tata Prima MH14CD5678");
        const string order = "--- Page 1 (native) ---\nIN THE HIGH COURT\n\n--- Page 2 (native) ---\nThe Bank seized the truck bearing registration MH 14 CD 5678 on 4th March.";

        var summary = ChargeLitigationLinker.Build([charge], [Case(20, "CS 88/2021", 700)], Docs(700, order), []);

        var link = Assert.Single(summary.Links);
        Assert.Equal(ChargeLitigationSignal.MovableIdentifier, link.Signal);
        Assert.Equal(1, link.ChargeId);
        Assert.Equal(2, link.PageNumber);
        Assert.Contains("vehicle registration MH 14 CD 5678".Replace("MH 14 CD 5678", "MH14CD5678"), link.Explanation);
        Assert.Contains("CHG-7", link.Explanation);
        Assert.Contains("seized the truck", link.Excerpt);
        Assert.Equal(1, summary.CasesNamingAssets);
        Assert.Equal(1, summary.ChargesWithIdentifiers);
        Assert.Equal(0, summary.MovableChargesWithoutIdentifiers);
    }

    [Fact]
    public void Two_vehicles_of_one_charge_named_in_one_case_make_one_link_naming_both()
    {
        var charge = Charge(1, "CHG-7", "Hypothecation of MH12AB1234 and MH14CD5678");
        const string order = "--- Page 1 (native) ---\nVehicles MH12AB1234 and MH14CD5678 were attached.";

        var link = Assert.Single(ChargeLitigationLinker.Build([charge], [Case(20, "CS 88/2021", 700)], Docs(700, order), []).Links);

        Assert.Contains("MH12AB1234", link.Explanation);
        Assert.Contains("MH14CD5678", link.Explanation);
    }

    [Fact]
    public void An_order_about_a_different_vehicle_links_nothing()
    {
        var charge = Charge(1, "CHG-7", "Hypothecation of vehicle MH12AB1234");
        const string order = "--- Page 1 (native) ---\nThe vehicle MH12AB9999 was released to its owner.";

        var summary = ChargeLitigationLinker.Build([charge], [Case(20, "CS 88/2021", 700)], Docs(700, order), []);

        Assert.False(summary.HasAny);
        Assert.Equal(1, summary.ChargesWithIdentifiers);
    }

    [Fact]
    public void A_movable_charge_with_only_category_words_is_reported_as_not_comparable_rather_than_clean()
    {
        var generic = Charge(1, "CHG-1", "First pari passu charge on the current assets and movable fixed assets of the company");
        var withVehicle = Charge(2, "CHG-2", "Hypothecation of MH12AB1234");
        var land = Charge(3, "CHG-3", "Mortgage on land at Plot No. 55, Rohini, Delhi, 110085", propertyType: "Immovable property", securityTypesJson: "[\"ImmovableProperty\"]");

        var summary = ChargeLitigationLinker.Build([generic, withVehicle, land], [], new Dictionary<long, LitigationOrderDocument>(), []);

        Assert.Equal(1, summary.ChargesWithIdentifiers);
        Assert.Equal(1, summary.MovableChargesWithoutIdentifiers);   // only CHG-1: CHG-2 has an identifier, CHG-3 is land
    }

    [Fact]
    public void Nclt_orders_and_orders_without_text_are_not_searched_for_movables()
    {
        var charge = Charge(1, "CHG-7", "Hypothecation of vehicle MH12AB1234");
        const string order = "--- Page 1 (native) ---\nVehicle MH12AB1234 is attached.";
        var nclt = Case(20, "CP(IB) 5/2021", 700, court: "NCLT Mumbai Bench");
        var noText = Case(21, "CS 9/2021", 701);
        var docs = new Dictionary<long, LitigationOrderDocument>(Docs(700, order))
        {
            [701] = new() { LitigationCaseOrderId = 701, ExtractedText = null, TextExtractionStatus = FilingDocumentProcessingStatus.Discovered }
        };

        var summary = ChargeLitigationLinker.Build([charge], [nclt, noText], docs, []);

        Assert.False(summary.HasAny);
        Assert.True(summary.NcltOrdersSkipped);
    }

    [Fact]
    public void All_three_signals_can_coexist_on_one_charge()
    {
        var charge = Charge(1, "CHG-7", "Hypothecation of vehicle MH12AB1234; also equitable mortgage on land at Plot No. A-36, Nayapalli, Bhubaneswar, 751012",
            propertyType: "Immovable property, Movable property (not being pledge)");
        var propertyCase = Case(20, "WP 1/2020", 700);
        var vehicleCase = Case(21, "CS 2/2021", 701);
        var recoveryCase = new LitigationCase
        {
            LitigationCaseId = 22, CaseNumber = "SA 3/2021", Court = "Debts Recovery Tribunal", Act = "SARFAESI", PetitionersJson = "[\"State Bank of India\"]",
            Orders = []
        };
        var docs = new Dictionary<long, LitigationOrderDocument>(Docs(700, "--- Page 1 (native) ---\nThe land at Plot No. A-36, Nayapalli, Bhubaneswar is attached."))
        {
            [701] = new() { LitigationCaseOrderId = 701, ExtractedText = "--- Page 1 (native) ---\nThe vehicle MH12AB1234 is released.", TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted }
        };

        var summary = ChargeLitigationLinker.Build([charge], [propertyCase, vehicleCase, recoveryCase], docs, []);

        Assert.Equal(3, summary.Links.Count);
        Assert.Equal(1, summary.CasesFor(ChargeLitigationSignal.ImmovableAddress));
        Assert.Equal(1, summary.CasesFor(ChargeLitigationSignal.MovableIdentifier));
        Assert.Equal(1, summary.CasesFor(ChargeLitigationSignal.LenderRecoveryCase));
        Assert.Equal(2, summary.CasesNamingAssets);
    }

    [Fact]
    public void A_vehicle_number_in_the_same_clause_as_a_plot_is_not_read_as_a_plot_number()
    {
        // One clause names a vehicle and a plot. The address matcher treats any token with a digit as a plot number,
        // so without masking, an order that merely mentions the vehicle (and the word "vehicle") looks like a match
        // for the property. It must produce the vehicle link only.
        var charge = Charge(1, "CHG-7", "Hypothecation of vehicle MH12AB1234; also equitable mortgage on land at Plot No. A-36, Nayapalli, Bhubaneswar, 751012",
            propertyType: "Immovable property, Movable property (not being pledge)");
        var vehicleOnly = Case(20, "CS 2/2021", 701);
        var docs = new Dictionary<long, LitigationOrderDocument>
        {
            [701] = new() { LitigationCaseOrderId = 701, ExtractedText = "--- Page 1 (native) ---\nThe vehicle MH12AB1234 is released to its owner.", TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted }
        };

        var summary = ChargeLitigationLinker.Build([charge], [vehicleOnly], docs, []);

        var link = Assert.Single(summary.Links);
        Assert.Equal(ChargeLitigationSignal.MovableIdentifier, link.Signal);
    }

    [Fact]
    public void Wording_helpers_cover_every_signal_and_only_the_indirect_one_is_a_watch()
    {
        foreach (var signal in Enum.GetValues<ChargeLitigationSignal>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ChargeLitigationLabels.Badge(signal)));
            Assert.False(string.IsNullOrWhiteSpace(ChargeLitigationLabels.Heading(signal)));
            Assert.False(string.IsNullOrWhiteSpace(ChargeLitigationLabels.Phrase(signal)));
        }
        Assert.Equal("sev-critical", ChargeLitigationLabels.Severity(ChargeLitigationSignal.ImmovableAddress));
        Assert.Equal("sev-critical", ChargeLitigationLabels.Severity(ChargeLitigationSignal.MovableIdentifier));
        Assert.Equal("sev-watch", ChargeLitigationLabels.Severity(ChargeLitigationSignal.LenderRecoveryCase));
    }
}
