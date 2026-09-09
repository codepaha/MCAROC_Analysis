using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using static MCAROC_Analysis.Tests.AnalysisTestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>ChargeRules.Evaluate appends outcomes in a fixed order: [0] LenderConcentration,
/// [1] RegisteredExposure, [2] MaterialEnhancement.</summary>
public class ChargeRulesTests
{
    [Fact]
    public void MaterialEnhancement_NoCreationEvent_IsNotEvaluated()
    {
        // Regression test: must never assume the earliest visible Modification is the original creation
        // when no Creation event is present in the data.
        var charge = new RocCharge
        {
            RocChargeNumber = "C1", ChargeStatus = "Open", CurrentAmount = 50m,
            Events =
            [
                new RocChargeEvent { EventType = ChargeEventType.Modification, EventDate = new DateOnly(2025, 1, 1), ChargeAmount = 50m }
            ]
        };
        var ctx = BuildContext(charges: [charge]);

        var result = ChargeRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, result[2].Status);
    }

    [Fact]
    public void MaterialEnhancement_ZeroOriginalAmount_IsNotEvaluated()
    {
        var charge = new RocCharge
        {
            RocChargeNumber = "C1", ChargeStatus = "Open", CurrentAmount = 50m,
            Events =
            [
                new RocChargeEvent { EventType = ChargeEventType.Creation, EventDate = new DateOnly(2020, 1, 1), ChargeAmount = 0m },
                new RocChargeEvent { EventType = ChargeEventType.Modification, EventDate = new DateOnly(2025, 1, 1), ChargeAmount = 50m }
            ]
        };
        var ctx = BuildContext(charges: [charge]);

        var result = ChargeRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, result[2].Status);
    }

    [Fact]
    public void MaterialEnhancement_LargeIncrease_Triggers()
    {
        var charge = new RocCharge
        {
            RocChargeNumber = "C1", ChargeStatus = "Open", CurrentAmount = 50m,
            Events =
            [
                new RocChargeEvent { EventType = ChargeEventType.Creation, EventDate = new DateOnly(2020, 1, 1), ChargeAmount = 20m },
                new RocChargeEvent { EventType = ChargeEventType.Modification, EventDate = new DateOnly(2025, 1, 1), ChargeAmount = 50m }
            ]
        };
        var ctx = BuildContext(charges: [charge]);

        var result = ChargeRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[2];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(FindingSeverity.Review, outcome.Finding!.Severity);
        // Evidence link so the Charges & Security drawer can show this finding under the exact charge.
        Assert.Contains("\"entityType\":\"RocCharge\"", outcome.Finding!.SourceReferenceJson);
    }

    [Fact]
    public void LenderConcentration_SingleDominantHolder_Triggers()
    {
        var charges = new List<RocCharge>
        {
            new() { RocChargeNumber = "C1", LatestChargeHolderNormalized = "BANK A", CurrentAmount = 90m, SatisfactionDate = null },
            new() { RocChargeNumber = "C2", LatestChargeHolderNormalized = "BANK B", CurrentAmount = 10m, SatisfactionDate = null }
        };
        var ctx = BuildContext(charges: charges);

        var result = ChargeRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[0];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Equal(ChargeRules.LenderConcentrationCode, outcome.Finding!.Code);
    }

    [Fact]
    public void RegisteredExposure_HighRatioToNetWorth_Triggers()
    {
        var charges = new List<RocCharge> { new() { RocChargeNumber = "C1", LatestChargeHolderNormalized = "BANK A", CurrentAmount = 150m, SatisfactionDate = null } };
        var ctx = BuildContext(charges: charges, financialYears: [Fy(2025, netWorth: 100m)]);

        var result = ChargeRules.Evaluate(ctx, RuleThresholds.Default);

        var outcome = result[1];
        Assert.Equal(RuleEvaluationStatus.Triggered, outcome.Status);
        Assert.Contains("exposure", outcome.Finding!.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SatisfiedCharges_ExcludedFromCurrentExposureAndConcentration()
    {
        var charges = new List<RocCharge>
        {
            new() { RocChargeNumber = "C1", LatestChargeHolderNormalized = "BANK A", CurrentAmount = 500m, SatisfactionDate = new DateOnly(2020, 1, 1) }
        };
        var ctx = BuildContext(charges: charges, financialYears: [Fy(2025, netWorth: 10m)]);

        var result = ChargeRules.Evaluate(ctx, RuleThresholds.Default);

        Assert.Equal(RuleEvaluationStatus.NotEvaluated, result[0].Status); // no open charges
        Assert.Equal(RuleEvaluationStatus.NotTriggered, result[1].Status); // no open charges — nothing to expose
    }
}
