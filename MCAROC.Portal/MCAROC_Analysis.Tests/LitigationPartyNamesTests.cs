using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

/// <summary>The party and advocate lists of a litigation report. The provider sends each party as a record with a name; reading them as a
/// list of strings silently found no parties on every real report.</summary>
public class LitigationPartyNamesTests
{
    [Fact]
    public void Parties_sent_as_records_are_read_by_name()
    {
        const string json = """
            [{"address":"","petitioner_advocate":"","name":"GURUSWAMY NANDA KUMAR","_id":{"$oid":"62c8dfca44ad686ed8e3a269"},"advocate":"","mobile_number":"","email":""},
             {"address":"S/O. GURUSWAMY NAIDU, AGE: NIL","name":"PRAKASH G","advocate":""}]
            """;

        Assert.Equal(["GURUSWAMY NANDA KUMAR", "PRAKASH G"], LitigationPartyNames.Parse(json));
    }

    [Fact]
    public void Plain_strings_and_a_mix_of_both_are_read_too()
    {
        Assert.Equal(["Karur Vysya Bank Limited"], LitigationPartyNames.Parse("""["Karur Vysya Bank Limited"]"""));
        Assert.Equal(["A", "B"], LitigationPartyNames.Parse("""["A",{"name":"B"}]"""));
        Assert.Equal(["Only One"], LitigationPartyNames.Parse("""{"name":"Only One"}"""));
    }

    [Fact]
    public void Advocates_sent_as_records_are_read_and_blank_names_are_not_parties()
    {
        // As the report sends them: some respondent advocates have a record but no name.
        const string json = """[{"code":"","name":"","_id":{"$oid":"1"}},{"code":"","name":"","_id":{"$oid":"2"}},{"code":"","name":"MS ALLURI KRISHNAM RAJU","_id":{"$oid":"3"}}]""";

        Assert.Equal(["MS ALLURI KRISHNAM RAJU"], LitigationPartyNames.Parse(json));
        Assert.Empty(LitigationPartyNames.Parse("""[{"name":" "},"  ",{"name":null},{"other":"x"},7]"""));
    }

    [Fact]
    public void A_name_is_trimmed_and_listed_once()
    {
        Assert.Equal(["COASTAL PROJECTS LIMITED"], LitigationPartyNames.Parse("""[{"name":" COASTAL PROJECTS LIMITED"},"coastal projects limited "]"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[")]
    public void Nothing_or_unreadable_gives_no_names_and_never_throws(string? json) => Assert.Empty(LitigationPartyNames.Parse(json));
}
