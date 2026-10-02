using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>The deterministic reading of a charge's "Particulars of Property Charged", against verbatim wording from a
/// real charge-report export (public MCA filings) — including its typos, restated areas and broken CTS lists.</summary>
public class PropertyParticularsNormalizerTests
{
    private const string OfficeUnit =
        "All that piece or parcel of premises adm. about 27864.63 sq ft eq. to 2588.68 sq mets, of carpet area, bearing Unit No. 5c. on the 5 Floor, " +
        "in the bldg \"Godrej One\" along with 43 car parking space out of which 22 car parking space bearing Nos. B-43 to B-49, B-51 to B-65 in the " +
        "North core of upper level basement and 21 car parking space bearing Nos. B-82. B- 121 to B-131. B-174 to B-182 in the North core of Lower " +
        "level Basement of the bldg, situated and constructed on all that piece or parcel of land admeasuring about 5.3 acres equivalent to 21448 sq mets, " +
        "forming part of a larger land admeasuring about 34.20 acres i.e. eq. to 138402 sq.mets, comprised in New C.T.S No.51/B and Old C.T.S. Nos, " +
        "51 (P) 52(P), 52/1, 52/2, 52/3, 52/4, 52/5, 52/6, 52/7, 52/8, 52/9, 52/10, 52/11, 52/12, 52/13, 52/14, 52/15, 52/16,52/17, lying, being and " +
        "situated at Pirojshanagar, Vikhroli, Taluka Kurla, Mumbai Suburban District together with undivided interest in land proportionate to Unit No.5c";

    [Fact]
    public void OfficeUnit_WithParkingLandAndCtsList_IsReadField_ByField()
    {
        var p = PropertyParticularsNormalizer.Normalize(OfficeUnit, "Immovable property or any interest therein - Commercial");

        Assert.Equal([PropertyAssetClass.Immovable], p.AssetClasses);
        Assert.Contains(PropertyKind.Premises, p.Kinds);
        Assert.Contains(PropertyKind.Parking, p.Kinds);
        Assert.Equal(new NormalizedUnit("5C", "5th", "Godrej One"), Assert.Single(p.Units));

        // Restated areas are one area each, carrying the stated (not converted) values.
        Assert.Contains(new NormalizedArea(2588.68m, 27864.63m, AreaBasis.Carpet), p.Areas);
        Assert.Contains(p.Areas, a => a.SquareMetres == 21448m && a.Basis == AreaBasis.Land);
        Assert.Contains(p.Areas, a => a.SquareMetres == 138402m && a.Basis == AreaBasis.LargerLand);
        Assert.Equal(3, p.Areas.Count);

        Assert.Equal(43, p.ParkingSpaces); // the stated total, not 43 + 22 + 21
        Assert.Equal(["B-43–B-49", "B-51–B-65", "B-82", "B-121–B-131", "B-174–B-182"], p.ParkingSpaceNumbers);

        Assert.Contains(new SurveyNumberGroup("CTS", "New", ["51/B"]), p.SurveyNumbers, new SurveyGroupComparer());
        Assert.Contains(new SurveyNumberGroup("CTS", "Old", ["51(P)", "52(P)", "52/1–52/17"]), p.SurveyNumbers, new SurveyGroupComparer());

        Assert.Equal(["Pirojshanagar", "Vikhroli"], p.Location.Localities);
        Assert.Equal("Kurla", p.Location.Taluka);
        Assert.Equal("Mumbai Suburban", p.Location.District);
        Assert.Equal("Mumbai", p.Location.City);
    }

    [Theory]
    [InlineData("27864.63 sq. fts.i.e. 2588.68 sq. mtrs. Carpet Area")]
    [InlineData("27864.63Sq.fts i.e.2588.68 Sq.mtrs carpet area")]
    [InlineData("27,864.63 sq.fts. i.e 2588.68 sq mtrs carpet area")]
    public void AreaRestatements_InEveryObservedSpelling_AreOneArea(string text)
    {
        var area = Assert.Single(PropertyParticularsNormalizer.Normalize("premises admeasuring about " + text).Areas);

        Assert.Equal(new NormalizedArea(2588.68m, 27864.63m, AreaBasis.Carpet), area);
    }

    [Theory]
    [InlineData("comprised in CTS Nos. 51(P), 52(P),52/1,52/2,52/3,52/4,52/5,52/6.52/7,52/8,52/9,52/10,52/11,52/12,52/13,52/14,52/15,52/16,52/17 lying")]
    [InlineData("comprised in C.T.S Nos51 (P), 52 (P), 52/1, 52/2, 52/3, 52/4, 52/5, 52/6, 52/7, 52/8, 52/9,52/10,52/11,52/12,52/13, 52/14 52/15,52/16,52/17 lying")]
    [InlineData("comprised in CST Nos. 51(P), 52(P),52/1,52/2,52/3,52/4,52/5,52/6,52/7,52/8,52/9,52/10,52/10,52/11,52/12,52/13,52/14,52/15,52/16,52/17 lying")]
    [InlineData("comprised in old CTS-Nos, 51 (P), 52 (P) and 52/1 to 17 and")]
    public void CtsLists_WithTyposBreaksAndRanges_NormaliseToTheSameNumbers(string text)
    {
        var group = Assert.Single(PropertyParticularsNormalizer.Normalize(text).SurveyNumbers);

        Assert.Equal("CTS", group.Scheme);
        Assert.Equal(["51(P)", "52(P)", "52/1–52/17"], group.Numbers);
    }

    [Fact]
    public void CurrentAssetsHypothecation_IsMovableOnly_NotMortgagedPremises()
    {
        // "the Borrower's premises or godowns" is where stock lies, not a mortgaged premises.
        var p = PropertyParticularsNormalizer.Normalize(
            "All of the Borrower's current assets, including but not limited to entire stocks of raw materials, semi-finished and finished goods, " +
            "being and lying in the Borrower's premises or godowns of or rented, consumable stores and spares not relating to plant and machinery and " +
            "such other movables including book- debts, bills receivables", "Book debts, Movable property - Inventory, Movable property - Others");

        Assert.Equal([PropertyAssetClass.Movable], p.AssetClasses);
        Assert.Contains(PropertyKind.CurrentAssets, p.Kinds);
        Assert.Contains(PropertyKind.BookDebtsReceivables, p.Kinds);
        Assert.DoesNotContain(PropertyKind.Premises, p.Kinds);
        Assert.DoesNotContain(PropertyKind.PlantAndMachinery, p.Kinds); // "not relating to plant and machinery"
    }

    [Fact]
    public void PlotAndSurvey_AtJuhu_AreReadWithoutMisreadingCtsAsASurveyNumber()
    {
        var p = PropertyParticularsNormalizer.Normalize(
            "Immovable property viz. Land and Building at sub-divided plot No.5 of Plot No.75A and bearing Old Survey No.75A part admeasuring 900.3 " +
            "square metres (including 362.86 square metres of undeveloped foreshore land) or thereabouts and bearing C.T.S. No.18 of Juhu");

        Assert.Contains(new SurveyNumberGroup("Plot", null, ["5", "75A"]), p.SurveyNumbers, new SurveyGroupComparer());
        Assert.Contains(new SurveyNumberGroup("Survey", "Old", ["75A(P)"]), p.SurveyNumbers, new SurveyGroupComparer());
        Assert.Contains(new SurveyNumberGroup("CTS", null, ["18"]), p.SurveyNumbers, new SurveyGroupComparer());
        Assert.Equal(3, p.SurveyNumbers.Count); // "C.T.S. No.18" is not also "S. No. 18"
        Assert.Contains(p.Areas, a => a.SquareMetres == 900.3m && a.Basis == AreaBasis.Plot);
    }

    [Fact]
    public void Project_PinAndState_AreRead()
    {
        var p = PropertyParticularsNormalizer.Normalize(
            "Land admeasuring approximately 138402 Square meters bearing new CTS No. 51/B, Village Vikhroli, Taluka Kurla, Mumbai Suburban District " +
            "situated at Pirojshanagar, Vikhroli East, Mumbai – 400079. forming part of Protect \"Commercial Project Godrej Two\" Maharashtra, India.");

        Assert.Equal("400079", p.Location.Pin);
        Assert.Equal("Vikhroli", p.Location.Village);
        Assert.Equal("Maharashtra", p.Location.State);
        Assert.Contains("Commercial Project Godrej Two", p.BuildingsOrProjects);
        Assert.DoesNotContain(p.Areas, a => a.SquareMetres == 400079m); // a PIN is never an area
    }

    [Fact]
    public void OtherCompanies_AreNamed_WithoutLeadingNoise()
    {
        var p = PropertyParticularsNormalizer.Normalize(
            "First charge on entire current Asset Godrej Projects Development Pvt. Ltd., including book debts. Hypothecation of current Assets of " +
            "Godrej Real Estate Pvt Ltd (Subsidiary of GPL)");

        Assert.Equal(["Godrej Projects Development Private Limited", "Godrej Real Estate Private Limited"], p.NamedEntities);
    }

    [Theory]
    [InlineData("Part A of Schedule of the hypothecation agreement")]
    [InlineData("As per Annexure - I")]
    public void ReferenceOnlyWording_IsFlagged(string text)
    {
        Assert.True(PropertyParticularsNormalizer.Normalize(text, "Movable property - Others").DetailsOnlyInReferencedDocument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    public void Missing_IsEmpty(string? text)
    {
        Assert.False(PropertyParticularsNormalizer.Normalize(text).HasContent);
    }

    private sealed class SurveyGroupComparer : IEqualityComparer<SurveyNumberGroup>
    {
        public bool Equals(SurveyNumberGroup? x, SurveyNumberGroup? y) =>
            x is not null && y is not null && x.Scheme == y.Scheme && x.Qualifier == y.Qualifier && x.Numbers.SequenceEqual(y.Numbers);
        public int GetHashCode(SurveyNumberGroup obj) => HashCode.Combine(obj.Scheme, obj.Qualifier);
    }

    // ── #366: misses found on a 41-company sample of real charge reports ──

    [Fact]
    public void PlotList_JoinedWithPlusSigns_ReadsEveryPlot()
    {
        var p = PropertyParticularsNormalizer.Normalize(
            "Exclusive charge by way of equitable mortgage on immovable property bearing Plot Nos: 728 + 729 + 730 admeasuring 52707.19 sq. mtrs. " +
            "together with lease hold rights at GIDC Halol-2", "Immovable property");

        Assert.Contains(new SurveyNumberGroup("Plot", null, ["728", "729", "730"]), p.SurveyNumbers, new SurveyGroupComparer());
    }

    [Fact]
    public void PlotList_NeverSwallowsTheHeadOfAnAmount()
    {
        var p = PropertyParticularsNormalizer.Normalize("Plot No. 5, 1,000 sq ft situated at Sector 63, Noida", "Immovable property");

        Assert.Contains(new SurveyNumberGroup("Plot", null, ["5"]), p.SurveyNumbers, new SurveyGroupComparer());
    }

    [Fact]
    public void LyingAt_NameBeforeVillage_StateWithoutSpaces_AndBarePin_AreRead()
    {
        var p = PropertyParticularsNormalizer.Normalize(
            "Hypothecation of a sterilizer lying at 149/1 Samudrapalli Village, Post - Pengaragunta Palamner Mandal, Chittoor 517408 Andhrapradesh",
            "Movable property");

        Assert.Equal("Samudrapalli", p.Location.Village);
        Assert.Equal("Andhra Pradesh", p.Location.State);
        Assert.Equal("517408", p.Location.Pin);
        Assert.Contains("Post - Pengaragunta Palamner Mandal", p.Location.Localities);
    }

    [Fact]
    public void SixDigits_NotFollowedByAState_AreNeverAPin()
    {
        var p = PropertyParticularsNormalizer.Normalize("Term loan of Rs. 250000 secured by plant and machinery at Unit 2", "Movable property");

        Assert.Null(p.Location.Pin);
    }

    [Theory]
    [InlineData("As mentioned in CAL")]
    [InlineData("As per Hypothecation agreement dated May 30, 2026 read with third schedule of the Twelfth Supplemental Joint Deed of hypothecation dated January 13, 2025 (Both attached herewith)")]
    public void MoreReferenceOnlyPhrasings_AreFlagged(string text)
    {
        Assert.True(PropertyParticularsNormalizer.Normalize(text, "Movable property").DetailsOnlyInReferencedDocument);
    }
}
