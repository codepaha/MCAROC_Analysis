using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>The negatives of plan section 3.4: every way a row can look keyable and must not be.</summary>
public class CaseKeyGateTests
{
    private static CaseNumberParts P(string number, string? type = null, string? year = null, string? filing = null, string? cnr = null) =>
        CaseNumberNormaliser.Parse(new CaseNumberInput(number, type, year, filing, cnr));

    private static CaseKeyResult Gate(CaseNumberParts parts, string? forum = "DRT", string? court = "DRT-MUMBAI-1", CaseNumberParts? portal = null, bool fromPortal = false) =>
        CaseKeyGate.Evaluate(new CaseKeyInput(forum, court, parts, portal, fromPortal));

    [Fact]
    public void A_complete_number_with_an_authoritative_court_gets_a_key() =>
        Assert.Equal("DRT|DRT-MUMBAI-1|OA|413|2022", Gate(P("413", "Original Application-", "2022")).Key);

    [Fact]
    public void The_key_is_the_same_whatever_the_source_spelling() =>
        Assert.Equal(Gate(P("413", "Original Application-", "2022")).Key, Gate(P("OA/000413/2022")).Key);

    [Fact]
    public void A_year_from_the_filing_date_alone_gives_no_key() => Assert.Null(Gate(P("413", "OA", "", filing: "05-12-2022")).Key);

    [Fact]
    public void A_year_from_the_cnr_alone_gives_no_key() => Assert.Null(Gate(P("413", "OA", "", cnr: "PHHC010123452022")).Key);

    [Fact]
    public void A_year_the_filing_date_and_the_cnr_agree_on_gives_a_key() =>
        Assert.Equal("DRT|DRT-MUMBAI-1|OA|413|2022", Gate(P("413", "OA", "", filing: "05-12-2022", cnr: "PHHC010123452022")).Key);

    [Fact]
    public void A_filing_date_and_a_cnr_that_disagree_give_no_key() => Assert.Null(Gate(P("413", "OA", "", filing: "05-12-2021", cnr: "PHHC010123452022")).Key);

    [Fact]
    public void A_glued_number_gives_no_key_for_the_whole()
    {
        var r = Gate(P("TPNo.93/CTB/2019CP/1317/2018"));
        Assert.Null(r.Key);
        Assert.Contains("glued", r.Reason);
    }

    [Fact]
    public void An_unknown_type_gives_no_key()
    {
        var r = Gate(P("88", "CONTEMPT PETITION", "2014"));
        Assert.Null(r.Key);
        Assert.Contains("dictionary", r.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_court_known_only_from_free_text_gives_no_key(string? court) => Assert.Null(Gate(P("413", "OA", "2022"), court: court).Key);

    [Fact]
    public void An_unknown_forum_gives_no_key() => Assert.Null(Gate(P("413", "OA", "2022"), forum: null).Key);

    [Fact]
    public void A_number_with_no_year_or_no_number_gives_no_key()
    {
        Assert.Null(Gate(P("413", "OA", "")).Key);
        Assert.Null(Gate(P("RESTORATION OF SUITS 0-9 R-4", "EXA")).Key);
    }

    [Fact]
    public void A_portal_row_is_keyed_from_the_identity_the_portal_returned_when_it_equals_the_lookup()
    {
        var looked = P("OA/413/2022");
        var returned = P("OA/413/2022");
        Assert.Equal("DRT|DRT-MUMBAI-1|OA|413|2022", Gate(looked, portal: returned, fromPortal: true).Key);
    }

    [Fact]
    public void A_portal_row_whose_returned_identity_differs_gives_no_key()
    {
        var r = Gate(P("OA/413/2022"), portal: P("OA/414/2022"), fromPortal: true);
        Assert.Null(r.Key);
        Assert.Contains("differs", r.Reason);
    }

    [Fact]
    public void A_portal_row_with_no_returned_identity_gives_no_key() => Assert.Null(Gate(P("OA/413/2022"), fromPortal: true).Key);

    [Fact]
    public void A_portal_identity_returned_with_a_glued_number_gives_no_key() =>
        Assert.Null(Gate(P("OA/413/2022"), portal: P("OA/413/2022,OA/9/2022"), fromPortal: true).Key);

    [Fact]
    public void A_portal_identity_whose_year_was_inferred_from_one_signal_gives_no_key()
    {
        var returned = P("413", "OA", "", filing: "05-12-2022");   // year from the filing date alone
        var r = Gate(P("OA/413/2022"), portal: returned, fromPortal: true);
        Assert.Null(r.Key);
        Assert.Contains("inferred", r.Reason);
    }
}
