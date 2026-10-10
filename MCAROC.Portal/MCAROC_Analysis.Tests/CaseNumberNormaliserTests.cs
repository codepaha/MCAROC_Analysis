using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Golden cases for the case-number normaliser (plan section 3). Every row is a shape taken from the two sample files: the Coastal
/// database rows and the individual-borrower tracker export. The expected display is the one fixed format, <c>TYPE/NO/YEAR</c>.</summary>
public class CaseNumberNormaliserTests
{
    private static CaseNumberParts P(string? number, string? type = null, string? year = null, string? filing = null, string? cnr = null) =>
        CaseNumberNormaliser.Parse(new CaseNumberInput(number, type, year, filing, cnr));

    // number, case-type column, year column, expected display
    [Theory]
    // Supreme Court text, several shapes
    [InlineData("SLP(C) No. 006459 - / 2019,(Verified On 23-10-2018)", "", "", "SLP(C)/6459/2019")]
    [InlineData("T.C.(C) No. 000253 - / 2020", "", "", "TC(C)/253/2020")]
    [InlineData("W.P.(C) No. 001343 - / 2020,(Verified On 26-11-2020)", "", "", "WP(C)/1343/2020")]
    [InlineData("SLP(C) No. 011180 / 2026", "", "", "SLP(C)/11180/2026")]
    [InlineData("SLP(Crl) No. 004537 - / 2026", "SLP(Crl) No. 004537 - ", "2026", "SLP(CRL)/4537/2026")]   // the type column holds the number text: ignored
    // High Court, type and year from the separate columns
    [InlineData("292/2016", "CRLRC", "2016", "CRLRC/292/2016")]
    [InlineData("10555", "CIVIL WRIT", "2010", "CW/10555/2010")]
    [InlineData("175/2017", "CMP", "2017", "CMP/175/2017")]
    [InlineData("134", "CMP (Civil Misc Petition)", "2016", "CMP/134/2016")]
    [InlineData("1200", "ARBITRATION PETITION.", "2013", "AP/1200/2013")]
    [InlineData("11151/2025", "CRM-M", "2025", "CRM-M/11151/2025")]
    [InlineData("30659", "CWP", "2018", "CWP/30659/2018")]
    [InlineData("33", "AP - ARBITRATION PETITION", "2019", "AP/33/2019")]
    [InlineData("CW/10555/2010", "CW", "2010", "CW/10555/2010")]
    // District courts
    [InlineData("CMA\\\\/1146\\\\/2022", "", "", "CMA/1146/2022")]                  // doubled backslashes in the source
    [InlineData("OS\\/103\\/2015", "", "", "OS/103/2015")]
    [InlineData("CS - CIVIL SUIT/1411/2019", "CS", "2019", "CS/1411/2019")]
    [InlineData("EXE - EXECUTION/198/2020", "EXE", "2020", "EXE/198/2020")]
    [InlineData("NDPS - NDPS ACT/780/2019", "NDPS", "2019", "NDPS/780/2019")]
    [InlineData("59/2021", "O.S.", "2021", "OS/59/2021")]
    // DRT
    [InlineData("41", "SA-", "2021", "SA/41/2021")]
    [InlineData("413", "Original Application-", "2022", "OA/413/2022")]
    [InlineData("MA/21/2016", "MA", "2016", "MA/21/2016")]
    // Consumer commissions
    [InlineData("CC/2/2022", "consumer case(CC)", "2022", "CC/2/2022")]
    [InlineData("SA/104/2025", "Second Appeal(SA)", "2025", "SA/104/2025")]
    [InlineData("RP/3784/2008", "RP", "2008", "RP/3784/2008")]
    // leading zeros are dropped
    [InlineData("000253/2020", "OA", "", "OA/253/2020")]
    public void Complete_numbers_come_out_in_the_one_format(string number, string type, string year, string expected)
    {
        var parts = P(number, type, year);
        Assert.Equal(expected, parts.Display);
        Assert.Equal(CaseNumberCompleteness.Complete, parts.Completeness);
        Assert.Null(parts.Note);
    }

    [Fact]
    public void The_year_comes_from_the_number_when_it_has_one_and_from_the_column_otherwise()
    {
        Assert.Equal(YearSource.Number, P("292/2016", "CRLRC", "2016").YearSource);
        Assert.Equal(YearSource.Column, P("10555", "CIVIL WRIT", "2010").YearSource);
    }

    [Fact]
    public void Several_numbers_in_one_string_keep_the_first_as_primary_and_list_the_rest()
    {
        var p = P("SLP(C) No. 013999 / 2013,S.L.P.(C)...CC No. 006950 / 2013,S.L.P.(C)...CC No. 013999 - 013999 / 2013");
        Assert.Equal("SLP(C)/13999/2013", p.Display);
        Assert.Equal(2, p.ConnectedMatters.Count);
        Assert.Contains("connected:2", p.Notes);

        var ca = P("C.A. No. 007971 - / 2017,SLP(C) No. 009188 / 2015");
        Assert.Equal("CA/7971/2017", ca.Display);
        Assert.Single(ca.ConnectedMatters);
    }

    [Fact]
    public void Tribunal_numbers_with_a_bench_code_and_no_separator_between_matters_are_split()
    {
        var p = P("TPNo.93/CTB/2019CPNo.1317/KB/2018");
        Assert.Equal("TP/93/2019", p.Display);
        Assert.Contains("bench:CTB", p.Notes);
        Assert.Single(p.ConnectedMatters);

        var q = P("C.P.(IB)No.593/KB/2017CA(IB)No.206/KB/2018");
        Assert.Equal("CP(IB)/593/2017", q.Display);
        Assert.Single(q.ConnectedMatters);
    }

    [Fact]
    public void The_portals_full_number_forms_display_as_type_number_year_and_keep_the_full_form_for_lookups()
    {
        var district = P("DC/497/CC/599/2017");
        Assert.Equal("CC/599/2017", district.Display);
        Assert.Equal("DC/497/CC/599/2017", district.PortalNumber);

        var national = P("NC/RP/836/2005");
        Assert.Equal("RP/836/2005", national.Display);
        Assert.Equal("NC/RP/836/2005", national.PortalNumber);

        Assert.Null(P("CC/599/2017").PortalNumber);
    }

    [Fact]
    public void A_section_138_cheque_case_is_recognised_from_the_type_text_as_an_act_hint()
    {
        var p = P("NACT - 138 NIA ACT/2573/2020", "NACT", "2020");
        Assert.Equal("NACT/2573/2020", p.Display);
        Assert.Equal("Negotiable Instruments Act s.138", p.ActHint);
        Assert.Null(P("CS - CIVIL SUIT/1411/2019", "CS", "2019").ActHint);
    }

    // ---- partial and unresolved: show what is known, say what is missing, never invent ----

    [Theory]
    [InlineData("88", "CONTEMPT PETITION", "2014", "88/2014", "type not stated")]
    [InlineData("Temporary Injunction/3/2020", "", "", "3/2020", "type not stated")]
    [InlineData("Company Appeal(AT)(Ins) - 383/2020", "", "", "383/2020", "type not stated")]
    [InlineData("15/2019", "Civil Misc.(execution)", "2019", "15/2019", "type not stated")]
    [InlineData("175/2017", "", "", "175/2017", "type not stated")]
    [InlineData("75643", "SERVICE TAX", "", "75643", "type and year not stated")]            // a category, not a case type
    [InlineData("10555", "", "", "10555", "type and year not stated")]
    public void An_unknown_or_missing_type_or_year_is_left_out_and_noted(string number, string type, string year, string display, string note)
    {
        var p = P(number, type, year);
        Assert.Equal(display, p.Display);
        Assert.Equal(CaseNumberCompleteness.Partial, p.Completeness);
        Assert.Equal(note, p.Note);
    }

    [Fact]
    public void An_unknown_type_is_never_guessed_and_is_recorded()
    {
        var p = P("88", "CONTEMPT PETITION", "2014");
        Assert.Null(p.TypeCode);
        Assert.Contains("type-unknown:CONTEMPT PETITION", p.Notes);
    }

    [Fact]
    public void A_year_taken_from_the_filing_date_is_shown_but_is_not_explicit_so_the_number_is_not_complete()
    {
        var p = P("10555", "CIVIL WRIT", "", filing: "29-01-2010");
        Assert.Equal("CW/10555/2010", p.Display);
        Assert.Equal(YearSource.InferredFilingOnly, p.YearSource);
        Assert.Equal(CaseNumberCompleteness.Partial, p.Completeness);
        Assert.Equal("year inferred from the filing date", p.Note);
    }

    [Fact]
    public void A_year_that_the_filing_date_and_the_cnr_agree_on_is_still_inferred_not_explicit()
    {
        var p = P("4141", "CWP", "", filing: "05-12-2025", cnr: "PHHC010123452025");
        Assert.Equal("CWP/4141/2025", p.Display);
        Assert.Equal(YearSource.InferredFilingAndCnr, p.YearSource);
        Assert.Equal(CaseNumberCompleteness.Partial, p.Completeness);
    }

    [Fact]
    public void A_year_from_the_cnr_alone_is_used_when_there_is_nothing_else()
    {
        var p = P("4141", "CWP", "", cnr: "PHHC010123452025");
        Assert.Equal(YearSource.InferredCnrOnly, p.YearSource);
        Assert.Equal("year inferred from the CNR", p.Note);
    }

    [Fact]
    public void Type_and_number_with_no_year_anywhere_are_partial()
    {
        var p = P("75643", "SA", "");
        Assert.Equal("SA/75643", p.Display);
        Assert.Equal(CaseNumberCompleteness.Partial, p.Completeness);
        Assert.Equal("year not stated", p.Note);
        Assert.Contains("year-missing", p.Notes);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("RESTORATION OF SUITS 0-9 R-4")]
    [InlineData("-")]
    public void Text_with_no_readable_number_is_shown_as_filed_and_flagged(string number)
    {
        var p = P(number, "EXA");
        Assert.Equal(CaseNumberCompleteness.Unresolved, p.Completeness);
        Assert.Equal(number, p.Display);
        Assert.Equal("number format not recognised", p.Note);
        Assert.Null(p.No);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_case_number_is_unresolved_and_displays_nothing(string? number)
    {
        var p = P(number, "CWP", "2020");
        Assert.Equal(CaseNumberCompleteness.Unresolved, p.Completeness);
        Assert.Equal(string.Empty, p.Display);
    }

    [Fact]
    public void The_text_as_filed_is_always_kept()
    {
        const string raw = "SLP(C) No. 006459 - / 2019,(Verified On 23-10-2018)";
        Assert.Equal(raw, P(raw).Raw);
    }

    [Fact]
    public void A_word_that_merely_starts_with_No_is_not_stripped_as_a_number_token()
    {
        // "Notice" must not be read as the number token "No" + "tice"
        Assert.Equal("12/2020", P("Notice/12/2020").Display);   // an unknown word is dropped whole, not clipped
    }

    [Theory]
    [InlineData("O.S.", "OS")]
    [InlineData("W.P.(C)", "WP(C)")]
    [InlineData("Com.A.S.", "COMAS")]
    [InlineData("CRA-S", "CRA-S")]
    [InlineData("Original Application-", "OA")]
    [InlineData("SA-", "SA")]
    [InlineData("AP - ARBITRATION PETITION", "AP")]
    [InlineData("CMP (Civil Misc Petition)", "CMP")]
    [InlineData("consumer case(CC)", "CC")]
    [InlineData("Miscellaneous Application(MA)", "MA")]
    [InlineData("Interlocutory Application (I.B.C)", "IA")]
    public void Case_type_spellings_resolve_to_one_code(string raw, string expected) => Assert.Equal(expected, CaseTypeCodes.Resolve(raw));

    [Theory]
    [InlineData("Appeal")]
    [InlineData("Suit")]
    [InlineData("Temporary Injunction")]
    [InlineData("SERVICE TAX")]
    [InlineData("CONTEMPT PETITION")]
    [InlineData("")]
    [InlineData(null)]
    public void Names_that_are_not_known_codes_resolve_to_nothing(string? raw) => Assert.Null(CaseTypeCodes.Resolve(raw));
}
