using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Tests;

/// <summary>Addresses below are the real ones quoted in issue #191: Kanpur Flowercycling's registered office
/// and EPFO establishment (request 8 — the same plot, spelled two ways), and Coastal Projects' registered
/// office and a real Coastal mortgage description (request 4).</summary>
public class AddressMatcherTests
{
    private const string KanpurRegistered = "Arazi Number 428,429  Bhaunti, Pratappur, Kalyanpur, Kanpur, Uttar Pradesh, 209305";
    private const string KanpurEpfo = "KANPUR ARAZI NO 428 AND 429 BHAUTI, KANPUR, UTTAR PRADESH, 209305";
    private const string CoastalRegistered = "Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, Orissa, 751012";
    private const string CoastalHyderabadMortgage =
        "Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad";

    [Fact]
    public void SamePlot_TwoRealRenderings_Strong()
    {
        // "BHAUNTI" vs "BHAUTI" is a one-letter transliteration difference on the company's own filings.
        var result = AddressMatcher.Match(KanpurRegistered, KanpurEpfo);

        Assert.Equal(AddressMatchStrength.Strong, result.Strength);
        Assert.Equal("209305", result.MatchedPinCode);
        Assert.Contains("428", result.MatchedPlotNumbers);
        Assert.Contains("429", result.MatchedPlotNumbers);
        Assert.Contains("BHAUNTI", result.MatchedLocalities);
    }

    [Fact]
    public void MortgageDescription_PlotAndLocality_NoPin_Strong()
    {
        var result = AddressMatcher.Match(KanpurRegistered, "Equitable mortgage of land at Arazi No. 428 & 429, Village Bhauti, Kanpur");

        Assert.Equal(AddressMatchStrength.Strong, result.Strength);
        Assert.Null(result.MatchedPinCode);
    }

    [Fact]
    public void PlotNumber_WrittenDifferently_StillMatches()
    {
        // "A-36" / "A/36" / "A36" are the same plot.
        var result = AddressMatcher.Match(CoastalRegistered, "Mortgage of office premises at Plot No A/36, Nayapalli, Bhubaneswar");

        Assert.Equal(AddressMatchStrength.Strong, result.Strength);
        Assert.Contains("A-36", result.MatchedPlotNumbers);
        Assert.Contains("NAYAPALLI", result.MatchedLocalities);
    }

    [Fact]
    public void DifferentCity_NoMatch()
    {
        // Coastal's registered office is in Bhubaneswar; this real Coastal mortgage is a Hyderabad property.
        Assert.Equal(AddressMatchStrength.None, AddressMatcher.Match(CoastalRegistered, CoastalHyderabadMortgage).Strength);
    }

    [Fact]
    public void SameCity_DifferentPlot_NoMatch()
    {
        // Sharing only the city (and state) must never look like the company's own premises.
        var result = AddressMatcher.Match(KanpurRegistered, "Mortgage of land at Plot 77, Panki Industrial Area, Kanpur, Uttar Pradesh");

        Assert.Equal(AddressMatchStrength.None, result.Strength);
    }

    [Fact]
    public void SamePlotNumber_ButNoSecondSignal_NoMatch()
    {
        // A bare shared number ("428") elsewhere in the same city is not enough on its own.
        var result = AddressMatcher.Match(KanpurRegistered, "Mortgage of flat no. 428, Swaroop Nagar, Kanpur");

        Assert.Equal(AddressMatchStrength.None, result.Strength);
    }

    [Fact]
    public void PinAndLocality_NoPlotNumber_Partial()
    {
        var result = AddressMatcher.Match(CoastalRegistered, "Mortgage of land and building at Nayapalli, Bhubaneswar 751012");

        Assert.Equal(AddressMatchStrength.Partial, result.Strength);
        Assert.Empty(result.MatchedPlotNumbers);
    }

    [Fact]
    public void SpacedPinCode_Recognised()
    {
        var result = AddressMatcher.Match(CoastalRegistered, "Plot A-36 at Bhubaneswar - 751 012");

        Assert.Equal(AddressMatchStrength.Strong, result.Strength);
        Assert.Equal("751012", result.MatchedPinCode);
    }

    [Fact]
    public void SplitLocalityName_MatchesJoinedSpelling()
    {
        var result = AddressMatcher.Match(
            "8-2-293/82/F-B-1/F, Film Nagar, Jubilee Hills, Hyderabad, Telangana, 500033", CoastalHyderabadMortgage);

        Assert.Equal(AddressMatchStrength.Strong, result.Strength);
        Assert.Contains("8-2-293/82/F-B-1/F", result.MatchedPlotNumbers);
        Assert.Contains("FILMNAGAR", result.MatchedLocalities);
    }

    [Fact]
    public void ExcludedPlaceName_IsNotLocalityEvidence()
    {
        // With "Nayapalli" supplied as a structured city name, only the plot number is left — not enough.
        var result = AddressMatcher.Match(CoastalRegistered, "Plot No A/36, Nayapalli", ["Nayapalli"]);

        Assert.Equal(AddressMatchStrength.None, result.Strength);
    }

    [Theory]
    [InlineData(null, "anything")]
    [InlineData("Plot 1, Kanpur", null)]
    [InlineData("  ", "  ")]
    public void BlankInput_NoMatch(string? address, string? text) =>
        Assert.Equal(AddressMatchStrength.None, AddressMatcher.Match(address, text).Strength);
}
