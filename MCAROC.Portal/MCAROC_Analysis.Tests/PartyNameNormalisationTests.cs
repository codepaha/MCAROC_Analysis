using MCAROC_Analysis.Services.LitigationModule.Refresh;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#489: how vendor party names are written. Before an AI judges any case, the whole-name rule must read the company's own name however the record
/// writes it. Every accepted form below is from Coastal Projects' 626 vendor cases (dev DB, 2026-10-13), where they made 237 cases look unmatched.</summary>
public class PartyNameNormalisationTests
{
    private static readonly string[] Coastal = ["Coastal Projects Limited"];
    private static readonly string[] CoastalFormer = ["COASTAL PROJECTS PRIVATE LIMITED", "EAST COAST CARRIERS PVT LTD"];

    [Theory]
    [InlineData("1 COASTAL PROJECTS LTD", "exact")]
    [InlineData("MS COASTAL PROJECTS LIMITED", "exact")]
    [InlineData("MS.COASTAL PROJECTS LIMITED", "exact")]
    [InlineData("MS. COASTAL PROJECTS LTD.", "exact")]
    [InlineData("MS COASTAL PROJECTS LTD(NOT APPLICABLE)", "exact")]
    [InlineData("COASTAL PROJECTS LTD(NOT APPLICABLE)", "exact")]
    [InlineData("MS. COASTAL PROJECTS LTD. AND ANR.", "exact")]
    [InlineData("MS COASTAL PROJECTS P LTD.", "exact-former-name")]
    [InlineData("MS. COASTAL PROJECTS PVT. LTD.", "exact-former-name")]
    [InlineData("THE CHAIRMAN, COASTAL PROJECTS LTD.", "exact-representative")]
    [InlineData("RAVI SANKAR DEVARKONDA THE LIQUIDATOR FOR COASTAL PROJECTS LIMITED", "exact-representative")]
    [InlineData("THE RESOLUTION PROFESSIONAL OF COASTAL PROJECTS LIMITED", "exact-representative")]
    [InlineData("THE COASTAL PROJECTS LIMITED", "exact")]
    [InlineData("MIS COASTAL PROJECTS LTD", "exact")]
    [InlineData("MS.COASTAL PROJECTS LIMITED, REP.BY ITS OFFICIAL LIQUIDATOR,", "exact")]
    [InlineData("MS COASTAL PROJECTS LIMITED and 6 ORS", "exact")]
    public void The_company_is_read_however_the_record_writes_it(string party, string rule)
    {
        var result = WholeNamePartyMatcher.Match(party, Coastal, CoastalFormer);
        Assert.Equal(PartyMatchOutcome.Verified, result.Outcome);
        Assert.Equal(rule, result.Rule);
    }

    [Theory]
    [InlineData("COASTAL PROJECTS LIMITED EMPLOYEES COOPERATIVE SOCIETY")]   // another body that contains the name
    [InlineData("MSR COASTAL PROJECTS LIMITED")]                             // "MS" is only stripped as a whole word
    [InlineData("COASTAL PROJECTS (INDIA) LIMITED")]                          // a meaningful bracket is never dropped
    [InlineData("12 COASTAL PROJECTS LIMITED WORKERS UNION")]
    [InlineData("THE CHAIRMAN, COASTAL ROADS LIMITED")]                       // an officer of a different company
    [InlineData("SUNIL KUMAR")]
    [InlineData("COASTAL PROJECTS LIMITED ERSTWHILE MANAGING DIRECTOR S SURENDRA")]   // a person, the company's former director
    [InlineData("MISSION COASTAL PROJECTS LIMITED")]                                   // "MIS" only as a whole word
    [InlineData("THEME COASTAL PROJECTS LIMITED")]
    public void Other_parties_are_not_the_company(string party) =>
        Assert.NotEqual(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match(party, Coastal, CoastalFormer).Outcome);

    [Fact]
    public void A_leading_number_is_read_both_as_a_list_number_and_as_part_of_the_name()
    {
        Assert.Equal(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match("3M INDIA LIMITED", ["3M India Limited"]).Outcome);
        Assert.Equal(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match("2 3M INDIA LIMITED", ["3M India Limited"]).Outcome);
        Assert.Equal(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match("20 MICRONS LIMITED", ["20 Microns Limited"]).Outcome);
        // The text cannot tell "party 20, Microns Limited" from "20 Microns Limited", so both readings count: a company named "Microns Limited"
        // matches a party written "20 MICRONS LIMITED". Accepted; such a name pair is rare and the case page shows the party as written.
        Assert.Equal(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match("20 MICRONS LIMITED", ["Microns Limited"]).Outcome);
    }
}
