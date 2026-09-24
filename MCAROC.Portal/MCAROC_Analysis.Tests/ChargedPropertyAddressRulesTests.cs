using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>ChargedPropertyAddressRules (issue #191): an open charge whose property particulars match the
/// company's registered office, business address or an EPFO establishment address is reported once, as a
/// Watch finding on the Charges section citing the matching RocCharge rows.</summary>
public class ChargedPropertyAddressRulesTests
{
    private const string CoastalRegistered = "Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar, Orissa, 751012";
    private const string OwnOfficeMortgage =
        "Equitable mortgage on the land and building at Plot No. A-36, Nayapalli, Bhubaneswar - 751012";
    private const string HyderabadMortgage =
        "Equitable mortgage on the land and building at 8-2-293/82/F-B-1/F, filmnagar, Hyderabad 500033";

    private static CompanyProfile Profile(string? registered = CoastalRegistered, string? business = null, string? pin = null) => new()
    {
        CompanyName = "Coastal Projects Limited", RegisteredAddress = registered, BusinessAddress = business,
        RegisteredAddressPinCode = pin
    };

    private static RocCharge Charge(long id, string particulars, string? propertyType = "Immovable property",
        DateOnly? satisfied = null, string holder = "State Bank of India") => new()
    {
        ChargeId = id, RocChargeNumber = $"10126{id:0000}", LatestChargeHolderRaw = holder, SatisfactionDate = satisfied,
        Events =
        [
            new RocChargeEvent
            {
                ChargeEventId = id * 10, EventType = ChargeEventType.Creation, EventDate = new DateOnly(2018, 4, 1),
                PropertyType = propertyType, PropertyParticulars = particulars
            }
        ]
    };

    private static RuleEvaluationOutcome Evaluate(AnalysisContext ctx) => Assert.Single(ChargedPropertyAddressRules.Evaluate(ctx));

    [Fact]
    public void OpenCharge_OnRegisteredOffice_TriggersWatchOnCharges()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, OwnOfficeMortgage)]);

        var outcome = Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        var finding = outcome.Finding!;
        Assert.Equal(ChargedPropertyAddressRules.ChargedPropertyIsOwnPremisesCode, finding.Code);
        Assert.Equal(FindingSection.Charges, finding.Section);
        Assert.Equal(FindingSeverity.Watch, finding.Severity);
        Assert.Contains("registered office address", finding.SummaryText);
        Assert.Contains("A-36", finding.SummaryText);
        Assert.Contains("State Bank of India", finding.SummaryText);

        using var source = JsonDocument.Parse(finding.SourceReferenceJson!);
        Assert.Equal("RocCharge", source.RootElement.GetProperty("entityType").GetString());
        Assert.Equal([1L], source.RootElement.GetProperty("entityIds").EnumerateArray().Select(e => e.GetInt64()).ToArray());

        using var metrics = JsonDocument.Parse(finding.MetricsJson!);
        var address = metrics.RootElement.GetProperty("matches")[0].GetProperty("addresses")[0];
        Assert.Equal(ChargedPropertyAddressRules.RegisteredOfficeSource, address.GetProperty("source").GetString());
        Assert.Equal(nameof(AddressMatchBasis.PinPlotAndLocality), address.GetProperty("matchBasis").GetString());
    }

    [Fact]
    public void ChargeOnUnrelatedProperty_NotTriggered()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, HyderabadMortgage)]);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, Evaluate(ctx).Status);
    }

    [Fact]
    public void SatisfiedCharge_OnRegisteredOffice_NotTriggered()
    {
        var ctx = BuildContext(companyProfile: Profile(),
            charges: [Charge(1, OwnOfficeMortgage, satisfied: new DateOnly(2021, 3, 31))]);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Evaluate(ctx).Status);
    }

    [Fact]
    public void MovableSecurity_KeptAtRegisteredOffice_NotTriggered()
    {
        // Stock hypothecated "at" the office is not a charge on the office.
        var ctx = BuildContext(companyProfile: Profile(), charges:
        [
            Charge(1, "Hypothecation of stocks and book debts lying at Plot No. A-36, Nayapalli, Bhubaneswar 751012",
                propertyType: "Book debts, Movable property (not being pledge)")
        ]);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, Evaluate(ctx).Status);
    }

    [Fact]
    public void MortgageWording_WithoutPropertyType_StillCounts()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, OwnOfficeMortgage, propertyType: null)]);

        Assert.Equal(RuleEvaluationStatus.Triggered, Evaluate(ctx).Status);
    }

    [Fact]
    public void EpfoEstablishmentAddress_Matches_WhenProfileHasNoAddress()
    {
        var ctx = BuildContext(
            companyProfile: Profile(registered: null),
            // EPFO spells the village "BHAUTI", so only "Kanpur" is shared — the PIN supplies the rest.
            charges: [Charge(1, "Equitable mortgage of factory land at Arazi No. 428 and 429, Village Bhaunti, Kanpur - 209305")],
            epfoEstablishments:
            [
                new EpfoEstablishment
                {
                    EpfoEstablishmentId = 5, EstablishmentId = "KNKAN0012345000",
                    Address = "KANPUR ARAZI NO 428 AND 429 BHAUTI, KANPUR, UTTAR PRADESH, 209305"
                }
            ]);

        var outcome = Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Contains("EPFO establishment address", outcome.Finding!.SummaryText);
    }

    [Fact]
    public void BusinessAddress_Matches()
    {
        var ctx = BuildContext(
            companyProfile: Profile(business: "Survey No. 112/4, Gachibowli, Serilingampally, Hyderabad 500032"),
            charges: [Charge(1, "Equitable mortgage of land at Survey No. 112/4, Gachibowli, Hyderabad - 500032")]);

        var outcome = Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Contains("business address", outcome.Finding!.SummaryText);
    }

    [Fact]
    public void ChargeMatchingSeveralOwnAddresses_IsReportedOnce()
    {
        var ctx = BuildContext(
            companyProfile: Profile(business: "A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar 751012"),
            charges: [Charge(1, OwnOfficeMortgage)]);

        var finding = Evaluate(ctx).Finding!;

        using var metrics = JsonDocument.Parse(finding.MetricsJson!);
        Assert.Equal(1, metrics.RootElement.GetProperty("matchCount").GetInt32());
        Assert.Equal(2, metrics.RootElement.GetProperty("matches")[0].GetProperty("addresses").GetArrayLength());
    }

    [Fact]
    public void SeveralMatchingCharges_SummarisedWithCount()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges:
        [
            Charge(1, OwnOfficeMortgage),
            Charge(2, OwnOfficeMortgage, holder: "HDFC Bank Limited"),
            Charge(3, HyderabadMortgage)
        ]);

        var finding = Evaluate(ctx).Finding!;

        Assert.StartsWith("2 open charges", finding.SummaryText);
        using var source = JsonDocument.Parse(finding.SourceReferenceJson!);
        Assert.Equal([1L, 2L], source.RootElement.GetProperty("entityIds").EnumerateArray().Select(e => e.GetInt64()).ToArray());
    }

    [Fact]
    public void StructuredPinCode_IsUsed_WhenAddressTextHasNone()
    {
        // Registered address text without a PIN, but the structured field has one: a charge in the same plot
        // and locality but a different PIN must be ruled out.
        var ctx = BuildContext(
            companyProfile: Profile(registered: "Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar", pin: "751012"),
            charges: [Charge(1, "Equitable mortgage of Plot No. A-36, Nilakantha Nagar, Nayapalli, Bhubaneswar 751015")]);

        Assert.Equal(RuleEvaluationStatus.NotTriggered, Evaluate(ctx).Status);
    }

    [Fact]
    public void NoCharges_NotEvaluated()
    {
        var ctx = BuildContext(companyProfile: Profile());

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Evaluate(ctx).Status);
    }

    [Fact]
    public void NoOwnAddress_NotEvaluated()
    {
        var ctx = BuildContext(companyProfile: null, charges: [Charge(1, OwnOfficeMortgage)]);

        var outcome = Evaluate(ctx);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, outcome.Status);
        Assert.Equal(ChargedPropertyAddressRules.ChargedPropertyIsOwnPremisesCode, outcome.Code);
    }

    [Fact]
    public void NoPropertyParticulars_NotEvaluated()
    {
        var charge = Charge(1, OwnOfficeMortgage);
        charge.Events[0].PropertyParticulars = null;
        var ctx = BuildContext(companyProfile: Profile(), charges: [charge]);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, Evaluate(ctx).Status);
    }

    [Fact]
    public void RuleEngine_IncludesTheFinding()
    {
        var ctx = BuildContext(companyProfile: Profile(), charges: [Charge(1, OwnOfficeMortgage)]);

        var result = RuleEngine.Evaluate(ctx, new RuleThresholds());

        Assert.Contains(result.Findings, f => f.Code == ChargedPropertyAddressRules.ChargedPropertyIsOwnPremisesCode);
    }
}
