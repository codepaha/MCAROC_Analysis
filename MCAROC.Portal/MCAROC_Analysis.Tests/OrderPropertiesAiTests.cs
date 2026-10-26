using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.PropertyParticulars;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#554: reading the properties a court order names — the validator's grounding, the page filter and grouping a case's properties.
/// The texts are taken from real orders of the 2026-10-26 trial (Godrej, Coastal), shortened.</summary>
public class OrderPropertiesAiTests
{
    private const string HomebuyerPage =
        "The plaintiff entered into an Agreement to Sell and Construction Agreement in respect of flat no.1804 having super built up area of "
        + "3282 sq. ft. in the project Godrej Alpine at Yeyyadi, Padavu Village, Mangalore. The defendant shall hand over possession of the "
        + "said flat within eight weeks.";

    private static OrderPage Page(int n, string text) => new(n, text);

    private static string Answer(string properties, string unidentified = "[]") => $$"""{"properties":{{properties}},"unidentified":{{unidentified}}}""";

    [Fact]
    public void A_grounded_property_keeps_its_page_role_relation_issue_and_direction()
    {
        var raw = Answer("""
            [{"page":2,"sourceText":"flat no.1804 having super built up area of 3282 sq. ft. in the project Godrej Alpine at Yeyyadi, Padavu Village, Mangalore",
              "role":"Disputed","relation":"SoldByTarget","issue":"DelayedPossession",
              "directions":[{"kind":"Possession","quote":"The defendant shall hand over possession of the said flat within eight weeks."}],
              "kind":"Premises","unitNumber":"1804","project":"Godrej Alpine","areas":[{"value":3282,"unit":"SqFt","basis":"SuperBuiltUp"}],
              "surveyNumbers":[],"localities":["Yeyyadi"],"village":"Padavu Village","city":"Mangalore"}]
            """);

        var v = OrderPropertiesAi.Validate(raw, [Page(2, HomebuyerPage)]);

        Assert.True(v.IsAccepted);
        var p = Assert.Single(v.Result!.Properties);
        Assert.Equal(2, p.Page);
        Assert.Equal(OrderPropertyRole.Disputed, p.Role);
        Assert.Equal(OrderPropertyRelation.SoldByTarget, p.Relation);
        Assert.Equal(OrderPropertyIssue.DelayedPossession, p.Issue);
        Assert.Equal("1804", p.Property.UnitNumber);
        Assert.Equal("Godrej Alpine", p.Property.Project);
        Assert.Equal(3282m, Assert.Single(p.Property.Areas).Value);
        Assert.Equal(OrderPropertyDirectionKind.Possession, Assert.Single(p.Directions).Kind);
    }

    [Fact]
    public void A_value_the_page_does_not_have_is_dropped_and_recorded()
    {
        var raw = Answer("""
            [{"page":2,"sourceText":"flat no.1804 having super built up area of 3282 sq. ft.","role":"Disputed","relation":"SoldByTarget",
              "kind":"Premises","unitNumber":"1804","areas":[{"value":3500,"unit":"SqFt","basis":"SuperBuiltUp"}],
              "surveyNumbers":[{"scheme":"Survey","numbers":["75A"]}],"city":"Pune"}]
            """);

        var v = OrderPropertiesAi.Validate(raw, [Page(2, HomebuyerPage)]);

        var p = Assert.Single(v.Result!.Properties).Property;
        Assert.Equal("1804", p.UnitNumber);
        Assert.Empty(p.Areas);
        Assert.Empty(p.SurveyNumbers);
        Assert.Null(p.City);
        Assert.Equal(3, v.RejectedFields.Count);
    }

    [Fact]
    public void A_quote_from_a_page_that_was_not_sent_is_dropped()
    {
        var raw = Answer("""[{"page":7,"sourceText":"flat no.1804 having super built up area","role":"Disputed","kind":"Premises","unitNumber":"1804"}]""");
        var v = OrderPropertiesAi.Validate(raw, [Page(2, HomebuyerPage)]);

        Assert.False(v.IsAccepted);
        Assert.Contains(v.RejectedFields, r => r.Contains("page 7 was not sent"));
    }

    [Fact]
    public void A_property_with_nothing_to_identify_it_is_dropped()
    {
        const string page = "The suit property shall not be alienated by either party until the next date of hearing.";
        var raw = Answer("""[{"page":1,"sourceText":"The suit property shall not be alienated","role":"Disputed","kind":"Premises"}]""");

        var v = OrderPropertiesAi.Validate(raw, [Page(1, page)]);

        Assert.False(v.IsAccepted);
        Assert.Contains(v.RejectedFields, r => r.Contains("names no unit"));
    }

    /// <summary>Scanned orders run words together ("Plotno.304-0,roadno.78"); the model writes them apart. Only spacing and punctuation are let go.</summary>
    [Fact]
    public void A_quote_matches_scanned_text_whose_words_run_together()
    {
        const string page = "Corporateoffice at Plotno.304-0,roadno.78,Filmnagar,Jubilee Hills,Hyderabad - 500033.";
        var raw = Answer("""
            [{"page":1,"sourceText":"Corporate office at Plot no.304-0, road no.78, Filmnagar, Jubilee Hills, Hyderabad - 500033.",
              "role":"PartyAddress","relation":"TargetOwned","kind":"Premises","localities":["Jubilee Hills"],"city":"Hyderabad","pin":"500033"}]
            """);

        var p = Assert.Single(OrderPropertiesAi.Validate(raw, [Page(1, page)]).Result!.Properties);

        Assert.Equal(OrderPropertyRole.PartyAddress, p.Role);
        Assert.Equal("500033", p.Property.Pin);
        Assert.StartsWith("Corporateoffice", p.Property.SourceText);   // the page's own wording is kept
    }

    /// <summary>Scanners misread letters ("craim", "Appricant") and the model quotes them corrected: a few letters may differ, the values may not.</summary>
    [Fact]
    public void A_quote_with_a_few_misread_letters_is_found_but_a_changed_number_is_not()
    {
        const string page = "In so far as the craim of the Appricant Bank against the defendant Company, the mortgaged properties as described in "
            + "the schedule to OA shall be sold.";
        Assert.NotNull(OrderPropertiesAi.FindQuote(page, "the claim of the Applicant Bank against the defendant Company, the mortgaged properties"));
        Assert.Null(OrderPropertiesAi.FindQuote(page, "an entirely different sentence about a flat in another tower of the project"));
        Assert.Null(OrderPropertiesAi.FindQuote(page, "short"));
    }

    [Fact]
    public void A_property_acted_on_but_not_described_is_kept_as_unidentified()
    {
        const string page = "The Applicant Bank is entitled to recover the amount and in default for sale of mortgaged properties as described in theschedule to OA.";
        var raw = Answer("[]", """
            [{"page":5,"role":"Security","quote":"in default for sale of mortgaged properties as described in the schedule to OA"},
             {"page":5,"role":"Security","quote":"a quote that is nowhere on the page at all"}]
            """);

        var v = OrderPropertiesAi.Validate(raw, [Page(5, page)]);

        Assert.True(v.IsAccepted);
        var u = Assert.Single(v.Result!.Unidentified!);
        Assert.Equal(OrderPropertyRole.Security, u.Role);
        Assert.Contains("theschedule", u.Quote);
    }

    [Fact]
    public void An_order_that_names_no_property_is_an_accepted_empty_answer()
    {
        var v = OrderPropertiesAi.Validate("""{"properties":[],"unidentified":[]}""", [Page(1, HomebuyerPage)]);
        Assert.True(v.IsAccepted);
        Assert.Empty(v.Result!.Properties);
    }

    [Theory]
    [InlineData("sale of mortgaged properties as described in theschedule to OA", true)]
    [InlineData("the mortgagedproperties of the borrower", true)]
    [InlineData("land bearing Survey No. 75A admeasuring 900 sq. m", true)]
    [InlineData("flat no.1804 in B Wing", true)]
    [InlineData("Respondent Nos. 1 and 2 have already filed counter affidavit. List again on 14.7.2025.", false)]
    [InlineData("the claim against the company and the community at large", false)]   // "gat" in "against", "unit" in "community"
    [InlineData("Godrej Properties Limited, Godrej One, 5th Floor, Pirojshanagar, Vikhroli (East), Mumbai 400079 ... Respondent", false)]  // a cause-title address
    [InlineData("The hearing scheduled to the next date; the office of the registry shall list it.", false)]
    public void Only_pages_that_may_name_a_property_are_sent(string text, bool sent) =>
        Assert.Equal(sent, OrderPropertiesAi.MayNameProperty(text));

    [Fact]
    public void A_pin_written_with_a_space_is_read_as_one()
    {
        const string page = "residing at No.381, Meghasree, 4th Main, RMV Second Stage, Bangalore_560 040 and the respondent";
        var raw = Answer("""[{"page":1,"sourceText":"No.381, Meghasree, 4th Main, RMV Second Stage, Bangalore_560 040","role":"PartyAddress","kind":"Premises","city":"Bangalore","pin":"560 040"}]""");

        Assert.Equal("560040", Assert.Single(OrderPropertiesAi.Validate(raw, [Page(1, page)]).Result!.Properties).Property.Pin);
    }

    [Fact]
    public void A_place_that_already_says_what_it_is_is_not_labelled_twice()
    {
        var p = new AiProperty(PropertyAssetClass.Immovable, PropertyKind.BuildingOrProject, null, null, null, null, "Godrej Alpine", [], null, [], [],
            "Padavu Village", "Taluka Mangalore", "Dakshina Kannada District", "Mangalore", null, null);

        var d = CaseOrderProperties.Describe(p);

        Assert.Contains("Padavu Village", d);
        Assert.DoesNotContain("Village Padavu Village", d);
        Assert.DoesNotContain("District District", d);
        Assert.DoesNotContain("Taluka Taluka", d);
        Assert.DoesNotContain("Immovable", d);
    }

    // ── a case's properties ─────────────────────────────────────────────────────────────────────────────

    private static AiProperty Flat(string unit, string? project, string? source = null, IReadOnlyList<AiArea>? areas = null) =>
        new(PropertyAssetClass.Immovable, PropertyKind.Premises, null, unit, null, null, project, areas ?? [], null, [], [], null, null, null, "Mangalore", null, null, source);

    private static LitigationOrderPropertyReading Reading(long documentId, params OrderProperty[] properties) => new()
    {
        LitigationOrderDocumentId = documentId, Status = LitigationOrderPropertyReadingStatus.Completed,
        ResultJson = OrderPropertiesAi.Serialize(new OrderPropertiesResult(properties))
    };

    private static (LitigationCaseOrder, LitigationOrderDocument) Order(long id, string date) =>
        (new LitigationCaseOrder { LitigationCaseOrderId = id, OrderDate = date, OrderType = "Order" }, new LitigationOrderDocument { LitigationOrderDocumentId = 100 + id });

    [Fact]
    public void The_same_property_in_two_orders_is_one_entry_and_party_addresses_are_left_out()
    {
        var first = new OrderProperty(2, OrderPropertyRole.Disputed, OrderPropertyRelation.SoldByTarget, OrderPropertyIssue.DelayedPossession, [], Flat("1804", "Godrej Alpine", "flat no.1804"));
        var again = new OrderProperty(1, OrderPropertyRole.Attached, OrderPropertyRelation.Unknown, null,
            [new OrderPropertyDirection(OrderPropertyDirectionKind.Attachment, "flat 1804 is attached")],
            Flat("1804", "Godrej Alpine", "flat 1804", [new AiArea(3282, AiAreaUnit.SqFt, AreaBasis.SuperBuiltUp, null, null)]));
        var office = new OrderProperty(1, OrderPropertyRole.PartyAddress, OrderPropertyRelation.TargetOwned, null, [], Flat("5", "Godrej One"));
        var readings = new Dictionary<long, LitigationOrderPropertyReading> { [101] = Reading(101, first, office), [102] = Reading(102, again) };

        var props = CaseOrderProperties.Build([Order(1, "15-04-2025"), Order(2, "10-07-2025"), Order(3, "01-08-2025")], readings);

        var p = Assert.Single(props.Properties);
        Assert.Equal(2, p.Mentions.Count);
        Assert.Equal(OrderPropertyRole.Attached, p.Role);                      // the weightiest role any order gives it
        Assert.Equal(OrderPropertyRelation.SoldByTarget, p.Relation);
        Assert.Equal(OrderPropertyIssue.DelayedPossession, p.Issue);
        Assert.Single(p.Property.Areas);                                       // the fullest description
        Assert.Single(p.Directions);
        Assert.Equal(2, props.OrdersRead);
        Assert.Equal(1, props.OrdersPending);                                  // the third order has no reading yet
    }
}
