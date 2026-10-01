using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.PropertyParticulars;
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

    [Fact]
    public void ClassifyItem_RegisteredOffice_Matches()
    {
        var pool = ChargedPropertyAddressRules.BuildAddressPool(Profile(), null);
        var location = new NormalizedLocation(["Bhaunti"], null, null, null, "Kanpur", "Uttar Pradesh", "209305");
        var surveyGroups = new[] { new SurveyNumberGroup("Arazi", null, ["428", "429"]) };
        var item = new PropertyReadingItem([PropertyAssetClass.Immovable], [PropertyKind.Land], null, [], [], [], null, [], surveyGroups, location, []);

        var result = ChargedPropertyAddressRules.ClassifyItem(item, pool);

        Assert.True(result.IsCompanyPremises);
        Assert.Equal(PremisesCategory.RegisteredOffice, result.Category);
        Assert.Equal("Registered office", result.Label);
        Assert.Contains("428", result.Result.MatchedPlotNumbers);
    }

    [Fact]
    public void ClassifyItem_BusinessAddress_Matches()
    {
        var businessAddress = "Plot 105, Sector 4, IMT Manesar, Gurugram, Haryana, 122050";
        var pool = ChargedPropertyAddressRules.BuildAddressPool(Profile(business: businessAddress), null);
        var location = new NormalizedLocation(["IMT Manesar"], null, null, null, "Gurugram", "Haryana", "122050");
        var surveyGroups = new[] { new SurveyNumberGroup("Plot", null, ["105"]) };
        var item = new PropertyReadingItem([PropertyAssetClass.Immovable], [PropertyKind.Land], null, [], [], [], null, [], surveyGroups, location, []);

        var result = ChargedPropertyAddressRules.ClassifyItem(item, pool);

        Assert.True(result.IsCompanyPremises);
        Assert.Equal(PremisesCategory.BusinessAddress, result.Category);
        Assert.Equal("Business address", result.Label);
    }

    [Fact]
    public void ClassifyItem_EpfoEstablishment_Matches()
    {
        var est = new EpfoEstablishment
        {
            EstablishmentId = "DSNHP0056789000", City = "Baddi",
            Address = "Plot No. 12, Phase 3, Industrial Area Baddi, Solan, Himachal Pradesh, 173205"
        };
        var pool = ChargedPropertyAddressRules.BuildAddressPool(Profile(), [est]);
        var location = new NormalizedLocation(["Industrial Area"], null, null, null, "Baddi", "Himachal Pradesh", "173205");
        var surveyGroups = new[] { new SurveyNumberGroup("Plot", null, ["12"]) };
        var item = new PropertyReadingItem([PropertyAssetClass.Immovable], [PropertyKind.Land], null, [], [], [], null, [], surveyGroups, location, []);

        var result = ChargedPropertyAddressRules.ClassifyItem(item, pool);

        Assert.True(result.IsCompanyPremises);
        Assert.Equal(PremisesCategory.OperatingFacility, result.Category);
        Assert.Equal("EPFO establishment DSNHP0056789000", result.Label);
    }

    [Fact]
    public void ClassifyItem_UnrelatedProperty_ReturnsOtherCollateral()
    {
        var pool = ChargedPropertyAddressRules.BuildAddressPool(Profile(), null);
        var location = new NormalizedLocation(["Film Nagar"], null, null, null, "Hyderabad", "Telangana", "500033");
        var surveyGroups = new[] { new SurveyNumberGroup("Plot", null, ["8-2-293"]) };
        var item = new PropertyReadingItem([PropertyAssetClass.Immovable], [PropertyKind.Land], null, [], [], [], null, [], surveyGroups, location, []);

        var result = ChargedPropertyAddressRules.ClassifyItem(item, pool);

        Assert.False(result.IsCompanyPremises);
        Assert.Equal(PremisesCategory.OtherCollateral, result.Category);
        Assert.Contains("No match to filed company premises", result.Label);
    }

    [Fact]
    public void ClassifyCharge_WithGeminiExtraction_ClassifiesRegisteredOffice()
    {
        var pool = ChargedPropertyAddressRules.BuildAddressPool(Profile(), null);
        var charge = Charge(1, OwnPremisesMortgage);
        var hash = PropertyParticularsAi.HashOf(OwnPremisesMortgage, "Immovable property");
        var extraction = new PropertyParticularsExtraction
        {
            PropertyParticularsExtractionId = 1,
            TextHash = hash,
            Status = PropertyParticularsExtractionStatus.Completed,
            ExtractionJson = """
            {
              "properties": [
                {
                  "assetClass": "Immovable",
                  "kind": "Land",
                  "surveyNumbers": [
                    { "scheme": "Arazi", "numbers": ["428", "429"] }
                  ],
                  "localities": ["Bhaunti"],
                  "city": "Kanpur",
                  "state": "Uttar Pradesh",
                  "pin": "209305",
                  "areas": []
                }
              ]
            }
            """
        };
        var extractions = new Dictionary<string, PropertyParticularsExtraction> { [hash] = extraction };

        var result = ChargedPropertyAddressRules.ClassifyCharge(charge, pool, extractions);

        Assert.True(result.IsCompanyPremises);
        Assert.Equal(PremisesCategory.RegisteredOffice, result.Category);
        Assert.Equal(0, result.MatchedItemIndex);
    }

    [Fact]
    public void Review356_StructuredMovableAddressMustNotBecomeAMortgagedPremisesFinding()
    {
        const string text = "Equitable mortgage of land at Plot No. 999, Remote Village, PIN 500033; hypothecation of current assets at Arazi No. 428 & 429, Village Bhauti, Kanpur, PIN 209305.";
        const string propertyType = "Immovable property and movable assets";
        var charge = Charge(1, text, propertyType);
        var hash = PropertyParticularsAi.HashOf(text, propertyType);
        var extraction = new PropertyParticularsExtraction
        {
            TextHash = hash, Status = PropertyParticularsExtractionStatus.Completed,
            ExtractionJson = """
            {"properties":[
              {"assetClass":"Immovable","kind":"Land","surveyNumbers":[{"scheme":"Plot","numbers":["999"]}],"localities":["Remote Village"],"pin":"500033","areas":[]},
              {"assetClass":"Movable","kind":"CurrentAssets","surveyNumbers":[{"scheme":"Arazi","numbers":["428","429"]}],"localities":["Bhauti"],"pin":"209305","areas":[]}
            ]}
            """
        };
        var extractions = new Dictionary<string, PropertyParticularsExtraction> { [hash] = extraction };
        var reading = PropertyReading.For(text, propertyType, extractions);
        Assert.Equal(PropertyReadingSource.Ai, reading.Source);
        Assert.Equal(2, reading.Items.Count);
        var pool = ChargedPropertyAddressRules.BuildAddressPool(Profile(), null);
        Assert.False(ChargedPropertyAddressRules.ClassifyCharge(charge, pool, extractions).IsCompanyPremises);
        var ctx = BuildContext(companyProfile: Profile(), charges: [charge], propertyExtractions: extractions);
        Assert.Equal(RuleEvaluationStatus.NotTriggered, ChargedPropertyAddressRules.Evaluate(ctx)[0].Status);
    }
}
