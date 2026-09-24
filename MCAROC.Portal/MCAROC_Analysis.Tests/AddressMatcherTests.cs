using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Tests;

/// <summary>AddressMatcher compares a company's own address with a charge's free-text property particulars
/// (issue #191). The addresses below are the real registered addresses of Coastal Projects Limited
/// (request 4) and Kanpur Flowercycling (request 8), and the real Hyderabad mortgage description from
/// Coastal's charge register; the charge-side wordings around them are representative.</summary>
public class AddressMatcherTests
{
    private const string CoastalRegistered = "Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, Orissa, 751012";
    private const string KanpurRegistered = "Arazi Number 428,429  Bhaunti, Pratappur, Kalyanpur, Kanpur, Uttar Pradesh, 209305";
    private const string KanpurEpfo = "KANPUR ARAZI NO 428 AND 429 BHAUTI, KANPUR, UTTAR PRADESH, 209305";

    private static AddressMatch? Match(string own, string particulars, string? ownPin = null) =>
        AddressMatcher.Match(AddressMatcher.Fingerprint(own, ownPin), AddressMatcher.Fingerprint(particulars));

    [Fact]
    public void SamePinPlotAndLocality_Matches()
    {
        var m = Match(CoastalRegistered,
            "Equitable mortgage of land and building at Plot No.A-36, Nayapalli, Bhubaneswar, Odisha - 751012 standing in the name of the company");

        Assert.NotNull(m);
        Assert.Equal(AddressMatchBasis.PinPlotAndLocality, m.Basis);
        Assert.Equal(["A-36"], m.SharedPlotNumbers);
        Assert.Equal(["751012"], m.SharedPinCodes);
        Assert.Contains("NAYAPALLI", m.SharedLocalityTerms);
    }

    [Fact]
    public void NoPinOnCharge_PlotAndTwoLocalityWords_Matches()
    {
        // Mortgage descriptions often leave the PIN out; "and 429", "No.", "Village" and the amount, date and
        // area wording around the address must not get in the way.
        var m = Match(KanpurRegistered,
            "Equitable mortgage of factory land at Arazi No. 428 and 429, Village Bhaunti, Pargana Jajmau, Kanpur Nagar (U.P.) " +
            "for Rs. 5,00,00,000/- dated 12.03.2019 measuring 2.5 acres");

        Assert.NotNull(m);
        Assert.Equal(AddressMatchBasis.PlotAndLocality, m.Basis);
        Assert.Equal(["428", "429"], m.SharedPlotNumbers);
        Assert.Equal(["BHAUNTI", "KANPUR"], m.SharedLocalityTerms);
    }

    [Fact]
    public void EpfoAndRegisteredAddress_OfTheSamePlot_Match()
    {
        // The issue's own observation: Kanpur's EPFO address and registered address are the same plot,
        // spelled differently ("BHAUTI" / "Bhaunti").
        var m = Match(KanpurEpfo, KanpurRegistered);

        Assert.NotNull(m);
        Assert.Equal(AddressMatchBasis.PinPlotAndLocality, m.Basis);
    }

    [Fact]
    public void CompoundDoorNumber_WithCity_MatchesWithoutPin()
    {
        var m = Match("8-2-293/82/F-B-1/F, Road No. 7, Film Nagar, Jubilee Hills, Hyderabad 500033",
            "Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad dated 15-06-2018");

        Assert.NotNull(m);
        Assert.Equal(["8-2-293/82/F-B-1/F"], m.SharedPlotNumbers);
    }

    [Fact]
    public void CompoundDoorNumber_IsNotReadAsADate()
    {
        var fp = AddressMatcher.Fingerprint("Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad");

        Assert.Contains("8-2-293/82/F-B-1/F", fp.PlotNumbers);
    }

    [Fact]
    public void DifferentProperty_InAnotherCity_DoesNotMatch()
    {
        Assert.Null(Match(CoastalRegistered,
            "Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad, Telangana 500033"));
    }

    [Fact]
    public void SamePinAndLocality_ButDifferentPlot_DoesNotMatch()
    {
        Assert.Null(Match(CoastalRegistered, "Mortgage of Plot No. 1123, Nayapalli, Bhubaneswar 751012"));
    }

    [Fact]
    public void SamePlotAndLocality_ButDifferentPin_DoesNotMatch()
    {
        Assert.Null(Match(CoastalRegistered, "Mortgage of Plot No. A-36, Nayapalli, Bhubaneswar 751015"));
    }

    [Fact]
    public void SharedCityOnly_DoesNotMatch()
    {
        Assert.Null(Match(CoastalRegistered, "Mortgage of Plot No. 77, Saheed Nagar, Bhubaneswar"));
    }

    [Fact]
    public void SimplePlotNumber_WithOnlyOneSharedLocalityWord_AndNoPin_DoesNotMatch()
    {
        // "18" is a sector number in both, but a charge on stock stored somewhere in Noida is not the office.
        Assert.Null(Match("Plot 50, Sector 18, Noida 201301",
            "Hypothecation for Rs. 50,00,000 of stock at Noida and Sector 18 office, dated 01/04/2020"));
    }

    [Fact]
    public void AmountsDatesAreasAndYears_AreNotPlotNumbers()
    {
        var fp = AddressMatcher.Fingerprint(
            "Mortgage for Rs. 12,50,000/- and INR 3.5 crore dated 12.03.2019, measuring 1200 sq ft, executed in 2019");

        Assert.Empty(fp.PlotNumbers);
    }

    [Fact]
    public void StateNamesAndGenericWords_AreNotLocalityTerms()
    {
        var fp = AddressMatcher.Fingerprint("Equitable mortgage of land and building, Uttar Pradesh, Andhra Pradesh, Industrial Estate");

        Assert.Empty(fp.LocalityTerms);
    }

    [Fact]
    public void LabelledSpacedPin_AndStructuredPin_AreRecognised()
    {
        var charge = AddressMatcher.Fingerprint("Plot 12, Jubilee Hills, Hyderabad PIN 500 033");
        var own = AddressMatcher.Fingerprint("Plot 12, Jubilee Hills, Hyderabad", knownPinCode: "500033");

        Assert.Contains("500033", charge.PinCodes);
        Assert.Contains("500033", own.PinCodes);
        Assert.Equal(AddressMatchBasis.PinPlotAndLocality, AddressMatcher.Match(own, charge)!.Basis);
    }

    [Fact]
    public void TwoUnlabelledNumbers_AreNotJoinedIntoAPin()
    {
        var fp = AddressMatcher.Fingerprint("Arazi Number 428 429 Bhaunti, Kanpur");

        Assert.Empty(fp.PinCodes);
        Assert.Contains("428", fp.PlotNumbers);
        Assert.Contains("429", fp.PlotNumbers);
    }

    [Fact]
    public void EmptyText_GivesEmptyFingerprint_AndNeverMatches()
    {
        var empty = AddressMatcher.Fingerprint(null);

        Assert.Empty(empty.PlotNumbers);
        Assert.Null(AddressMatcher.Match(empty, AddressMatcher.Fingerprint(CoastalRegistered)));
    }
}
