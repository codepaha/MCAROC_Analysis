using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>ChargedPropertyAddressRules.Evaluate returns a single outcome ([0]). Fixtures use the real
/// Kanpur Flowercycling (request 8) addresses from issue #191.</summary>
public class ChargedPropertyAddressRulesTests
{
    private const string KanpurRegistered = "Arazi Number 428,429  Bhaunti, Pratappur, Kalyanpur, Kanpur, Uttar Pradesh, 209305";
    private const string OwnPremisesMortgage = "Equitable mortgage of land and building at Arazi No. 428 & 429, Village Bhauti, Kanpur";

    private static CompanyProfile Profile(string? registered = KanpurRegistered, string? business = null) => new()
    {
        CompanyName = "Test Co", RegisteredAddress = registered, RegisteredAddressCity = "Kanpur",
        RegisteredAddressState = "Uttar Pradesh", BusinessAddress = business
    };

    private static RocCharge Charge(long id, string particulars, string? propertyType = "Immovable property", DateOnly? satisfied = null) => new()
    {
        ChargeId = id, RocChargeNumber = $"10000{id}", LatestChargeHolderRaw = "State Bank of India", SatisfactionDate = satisfied,
        Events = [new RocChargeEvent { ChargeEventId = id * 10, RocChargeId = id, PropertyType = propertyType, PropertyParticulars = particulars }]
    };

    [Fact]
    public void MortgageOfRegisteredOffice_TriggersWatch_CitingTheCharge()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, OwnPremisesMortgage)]);

        var result = ChargedPropertyAddressRules.Evaluate(ctx)[0];

        Assert.Equal(RuleEvaluationStatus.Triggered, result.Status);
        var finding = result.Finding!;
        Assert.Equal(FindingSeverity.Watch, finding.Severity);
        Assert.Equal(FindingSection.Charges, finding.Section);
        Assert.Contains("registered office", finding.SummaryText);
        Assert.Contains("appears to", finding.SummaryText);
        Assert.Equal("{\"entityType\":\"RocCharge\",\"entityIds\":[1]}", finding.SourceReferenceJson);
        Assert.Contains("\"matchedPlotNumbers\":[\"428\",\"429\"]", finding.MetricsJson);
    }

    [Fact]
    public void MortgageOfEpfoEstablishment_Triggers()
    {
        var est = new EpfoEstablishment
        {
            EstablishmentId = "KNKNP0012345000", City = "Kanpur",
            Address = "KANPUR ARAZI NO 428 AND 429 BHAUTI, KANPUR, UTTAR PRADESH, 209305"
        };
        var ctx = BuildContext(companyProfile: Profile(registered: null), charges: [Charge(1, OwnPremisesMortgage)], epfoEstablishments: [est]);

        var result = ChargedPropertyAddressRules.Evaluate(ctx)[0];

        Assert.Equal(RuleEvaluationStatus.Triggered, result.Status);
        Assert.Contains("EPFO establishment KNKNP0012345000", result.Finding!.MetricsJson);
    }

    [Fact]
    public void UnrelatedProperty_NotTriggered()
    {
        var ctx = BuildContext(companyProfile: Profile(),
            charges: [Charge(1, "Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad")]);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, ChargedPropertyAddressRules.Evaluate(ctx)[0].Status);
    }

    [Fact]
    public void SatisfiedCharge_NotTriggered()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, OwnPremisesMortgage, satisfied: new DateOnly(2021, 3, 31))]);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, ChargedPropertyAddressRules.Evaluate(ctx)[0].Status);
    }

    [Fact]
    public void MovableOnlyCharge_StockAtPremises_NotTriggered()
    {
        // Hypothecated stock lying at the factory is located there — the premises themselves aren't charged.
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1,
            "Hypothecation of stocks lying at Arazi No. 428 & 429, Village Bhauti, Kanpur", propertyType: "Movable property (not being pledge)")]);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, ChargedPropertyAddressRules.Evaluate(ctx)[0].Status);
    }

    [Fact]
    public void BlankPropertyType_FallsBackToParticularsKeywords()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, OwnPremisesMortgage, propertyType: null)]);

        Assert.Equal(RuleEvaluationStatus.Triggered, ChargedPropertyAddressRules.Evaluate(ctx)[0].Status);
    }

    [Fact]
    public void RepeatedEventsAndIdenticalBusinessAddress_ReportOnce()
    {
        var charge = Charge(1, OwnPremisesMortgage);
        charge.Events.Add(new RocChargeEvent { ChargeEventId = 11, RocChargeId = 1, PropertyType = "Immovable property", PropertyParticulars = OwnPremisesMortgage });
        var ctx = BuildContext(companyProfile: Profile(business: KanpurRegistered), charges: [charge]);

        var finding = ChargedPropertyAddressRules.Evaluate(ctx)[0].Finding!;

        Assert.Contains("\"matchCount\":1", finding.MetricsJson);
    }

    [Fact]
    public void NoCharges_NotEvaluated() =>
        Assert.Equal(RuleEvaluationStatus.NotEvaluated, ChargedPropertyAddressRules.Evaluate(BuildContext(companyProfile: Profile()))[0].Status);

    [Fact]
    public void NoAddressOnFile_NotEvaluated()
    {
        var ctx = BuildContext(companyProfile: Profile(registered: null), charges: [Charge(1, OwnPremisesMortgage)]);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, ChargedPropertyAddressRules.Evaluate(ctx)[0].Status);
    }

    [Fact]
    public void RuleEngine_IncludesTheFinding()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, OwnPremisesMortgage)]);

        var result = RuleEngine.Evaluate(ctx, RuleThresholds.Default);

        Assert.Contains(result.Findings, f => f.Code == ChargedPropertyAddressRules.ChargedPropertyIsCompanyPremisesCode);
    }
}
