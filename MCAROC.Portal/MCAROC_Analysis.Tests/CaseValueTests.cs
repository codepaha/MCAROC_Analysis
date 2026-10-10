using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CaseValueTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData(".")]
    [InlineData("NA")]
    [InlineData("na")]
    [InlineData("N/A")]
    [InlineData("NIL")]
    [InlineData("null")]
    [InlineData("NULL")]
    [InlineData("None")]
    [InlineData("DUMMY")]
    [InlineData("TEST")]
    [InlineData("Not Found")]
    [InlineData("0000-00-00")]
    [InlineData(" NA ")]
    [InlineData("[]")]
    [InlineData("[ ]")]
    [InlineData("{}")]
    public void These_values_mean_no_information(string? value) => Assert.True(CaseValue.IsMissing(value));

    [Theory]
    [InlineData("0")]
    [InlineData("2020")]
    [InlineData("DISPOSED")]
    [InlineData("Kancheepuram")]
    [InlineData("[\"Petitioner One\"]")]
    [InlineData("NAGPUR")]        // starts with NA but is a value
    [InlineData("Testing Co")]    // contains TEST but is a value
    [InlineData("29-01-2020")]
    public void These_values_are_real(string value) => Assert.False(CaseValue.IsMissing(value));

    [Fact]
    public void A_real_incoming_value_replaces_the_held_one_as_before() =>
        Assert.Equal("PENDING", CaseValue.Merge("DISPOSED", "PENDING"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NA")]
    [InlineData("-")]
    [InlineData("[]")]
    public void A_missing_incoming_value_never_erases_a_held_one(string? incoming) =>
        Assert.Equal("Kancheepuram", CaseValue.Merge("Kancheepuram", incoming));

    [Fact]
    public void A_real_incoming_value_fills_an_empty_field()
    {
        Assert.Equal("Tamil Nadu", CaseValue.Merge(null, "Tamil Nadu"));
        Assert.Equal("Tamil Nadu", CaseValue.Merge("NA", "Tamil Nadu"));
        Assert.Equal("Tamil Nadu", CaseValue.Merge("", "Tamil Nadu"));
    }

    [Fact]
    public void Two_missing_values_keep_what_the_source_sent_so_a_placeholder_is_stored_as_received()
    {
        Assert.Equal("NA", CaseValue.Merge(null, "NA"));      // first observation: stored faithfully, not turned into null
        Assert.Equal("NA", CaseValue.Merge("NA", null));      // a later empty observation does not erase it either
        Assert.Null(CaseValue.Merge(null, null));
    }

    [Fact]
    public void An_empty_list_never_replaces_a_non_empty_one_and_a_non_empty_list_replaces_it()
    {
        Assert.Equal("[\"A\"]", CaseValue.Merge("[\"A\"]", "[]"));
        Assert.Equal("[\"B\"]", CaseValue.Merge("[\"A\"]", "[\"B\"]"));
    }
}
