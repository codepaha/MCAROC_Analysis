using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;

namespace MCAROC_Analysis.Tests;

public class ChargeSecurityClassifierTests
{
    private static ChargeSecurityClassification Classify(string propertyType, string propertyParticulars,
        string? extent = null, string? instrument = null, bool? joint = null, bool? consortium = null) =>
        ChargeSecurityClassifier.Classify(instrument, propertyType, propertyParticulars, extent, null, joint, consortium);

    [Fact]
    public void PariPassuOverCurrentAndMovableAssets_ProducesTwoComponents()
    {
        var c = Classify(
            "Book debts, Movable property (not being pledge)",
            "First pari passu charge on the current assets and movable fixed assets of the company along with other working capital bankers.",
            instrument: "BG/ILC : Commission @ 0.70% p.a. plus buyer's credit sublimit");

        Assert.Contains(c.SecurityComponents, x => x.SecurityType == SecurityType.CurrentAssets && x.Ranking == ChargeRanking.PariPassu);
        Assert.Contains(c.SecurityComponents, x => x.SecurityType == SecurityType.MovableFixedAssets);
        Assert.Contains(FacilityType.BankGuarantee, c.FacilityTypes);
        Assert.Contains(FacilityType.LetterOfCredit, c.FacilityTypes);
        Assert.Contains(FacilityType.BuyersCredit, c.FacilityTypes);
        Assert.Equal(ChargeArrangement.Consortium, c.Arrangement);   // "other working capital bankers"
        Assert.True(c.IsConsortium);
        Assert.Equal(ChargeClassificationConfidence.High, c.OverallConfidence);
    }

    [Fact]
    public void SeparateRankingsPerClause()
    {
        var c = Classify(
            "Immovable property, Book debts",
            "Pari passu charge over book debts and first charge over immovable property");

        Assert.Contains(c.SecurityComponents, x => x.SecurityType == SecurityType.BookDebts && x.Ranking == ChargeRanking.PariPassu);
        Assert.Contains(c.SecurityComponents, x => x.SecurityType == SecurityType.ImmovableProperty && x.Ranking == ChargeRanking.FirstCharge);
    }

    [Fact]
    public void PrimaryVsCollateral_OnlyFromExplicitWording()
    {
        var explicitPrimary = Classify("Movable property", "Primary Security:- hypothecation of current assets");
        Assert.Contains(explicitPrimary.SecurityComponents, x => x.IsPrimary == true);

        var noStatement = Classify("Book debts", "charge created over the book debts of the company");
        Assert.All(noStatement.SecurityComponents, x => Assert.Null(x.IsPrimary));
    }

    [Fact]
    public void UnrecognisedNarrative_ReturnsNone_NeverGuessed()
    {
        var c = ChargeSecurityClassifier.Classify("-", "-", "-", "-", "-", null, null);
        Assert.Equal(ChargeClassificationConfidence.None, c.OverallConfidence);
        Assert.Empty(c.SecurityComponents);
        Assert.Empty(c.FacilityTypes);
        Assert.Equal("[]", c.MatchedRulesJson);
    }

    [Fact]
    public void JointPlusConsortium_ArrangementIsMultipleLenders()
    {
        var c = ChargeSecurityClassifier.Classify(null, "Book debts", "joint charge with consortium of bankers", null, null, jointHolding: true, consortiumHolding: true);
        Assert.Equal(ChargeArrangement.MultipleLenders, c.Arrangement);
        Assert.True(c.IsConsortium);
        Assert.True(c.IsJointCharge);
    }
}
