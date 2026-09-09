using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class ChargeRules
{
    public const string LenderConcentrationCode = "CHARGE_LENDER_CONCENTRATION_HIGH";
    public const string RegisteredExposureCode = "CHARGE_REGISTERED_EXPOSURE_HIGH";
    public const string MaterialEnhancementCode = "CHARGE_MATERIAL_ENHANCEMENT";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();

        if (ctx.Charges.Count == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(LenderConcentrationCode, "No RocCharge records available."));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(RegisteredExposureCode, "No RocCharge records available."));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(MaterialEnhancementCode, "No RocCharge records available."));
            return outcomes;
        }

        // "Open" is determined by SatisfactionDate being null — RocCharge.ChargeStatus is free source text,
        // not a normalized enum, so SatisfactionDate is the reliable signal. Only open charges feed current
        // lender concentration/exposure — satisfied/historical charges are a separate concern, never mixed
        // into the current-exposure figure.
        var openCharges = ctx.Charges.Where(c => c.SatisfactionDate is null && c.CurrentAmount is > 0).ToList();

        outcomes.Add(EvaluateLenderConcentration(openCharges, thresholds));
        outcomes.Add(EvaluateRegisteredExposure(openCharges, ctx.Materiality, thresholds));
        outcomes.Add(EvaluateMaterialEnhancement(ctx.Charges, thresholds));

        return outcomes;
    }

    private static RuleEvaluationOutcome EvaluateLenderConcentration(List<RocCharge> openCharges, RuleThresholds t)
    {
        if (openCharges.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(LenderConcentrationCode, "No open charges with a known current amount.");

        var totalOpen = openCharges.Sum(c => c.CurrentAmount!.Value);
        if (totalOpen <= 0)
            return RuleEvaluationOutcome.NotEvaluated(LenderConcentrationCode, "Total open charge amount is not positive.");

        var byHolder = openCharges.GroupBy(c => c.LatestChargeHolderNormalized)
            .Select(g => new { Holder = g.Key, Amount = g.Sum(c => c.CurrentAmount!.Value) })
            .OrderByDescending(x => x.Amount)
            .First();
        var sharePercent = byHolder.Amount / totalOpen * 100m;

        return sharePercent >= t.HighLenderConcentrationPercent
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Charges, FindingSeverity.Review, TemporalStatus.Current,
                LenderConcentrationCode, "High Lender Concentration",
                $"'{byHolder.Holder}' accounts for {sharePercent:0.#}% of open registered charges (₹{byHolder.Amount:0.##} Cr of ₹{totalOpen:0.##} Cr).",
                MetricsJson: JsonSerializer.Serialize(new { holder = byHolder.Holder, holderAmount = byHolder.Amount, totalOpenAmount = totalOpen, sharePercent })))
            : RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluateRegisteredExposure(List<RocCharge> openCharges, MaterialityContext materiality, RuleThresholds t)
    {
        if (materiality.NetWorth is not { } netWorth || netWorth <= 0)
            return RuleEvaluationOutcome.NotEvaluated(RegisteredExposureCode, "Net worth not available or not positive — exposure ratio not meaningful.");
        if (openCharges.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        var totalOpen = openCharges.Sum(c => c.CurrentAmount!.Value);
        var ratio = totalOpen / netWorth;

        // Deliberately named "registered exposure", not "debt" — an open charge is a sanctioned/security
        // amount, not necessarily current outstanding debt.
        return ratio > t.HighRegisteredChargeExposureRatio
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Charges, FindingSeverity.Review, TemporalStatus.Current,
                RegisteredExposureCode, "High Registered Charge Exposure",
                $"Open registered charges (₹{totalOpen:0.##} Cr) represent {ratio:0.##}x net worth.",
                MetricsJson: JsonSerializer.Serialize(new { totalOpenAmount = totalOpen, netWorth, ratio })))
            : RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluateMaterialEnhancement(IReadOnlyList<RocCharge> charges, RuleThresholds t)
    {
        var evaluable = new List<(RocCharge Charge, decimal EnhancementPercent, decimal OriginalAmount, decimal LatestAmount)>();
        var anyEvaluable = false;

        foreach (var charge in charges)
        {
            // Requires an actual Creation event as the baseline — never assumes the earliest visible
            // Modification is the original creation when no Creation event is present in the data.
            var creation = charge.Events.Where(e => e.EventType == ChargeEventType.Creation).OrderBy(e => e.EventDate).FirstOrDefault();
            var latestModification = charge.Events.Where(e => e.EventType == ChargeEventType.Modification).OrderByDescending(e => e.EventDate).FirstOrDefault();
            if (creation?.ChargeAmount is not { } originalAmount || latestModification?.ChargeAmount is not { } latestAmount)
                continue;
            if (originalAmount <= 0)
                continue; // guards divide-by-zero — not evaluable for this charge

            anyEvaluable = true;
            var enhancementPercent = (latestAmount - originalAmount) / originalAmount * 100m;
            if (enhancementPercent > t.MaterialChargeEnhancementPercent)
                evaluable.Add((charge, enhancementPercent, originalAmount, latestAmount));
        }

        if (!anyEvaluable)
            return RuleEvaluationOutcome.NotEvaluated(MaterialEnhancementCode, "No charge has both a Creation event amount and a later Modification event amount available.");

        if (evaluable.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        var top = evaluable.OrderByDescending(x => x.EnhancementPercent).First();
        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Charges, FindingSeverity.Review, TemporalStatus.Current,
            MaterialEnhancementCode, "Material Charge Enhancement",
            $"Charge {top.Charge.RocChargeNumber} increased from ₹{top.OriginalAmount:0.##} Cr to ₹{top.LatestAmount:0.##} Cr ({top.EnhancementPercent:0.#}%).",
            MetricsJson: JsonSerializer.Serialize(new
            {
                chargeNumber = top.Charge.RocChargeNumber, originalAmount = top.OriginalAmount,
                latestAmount = top.LatestAmount, enhancementPercent = top.EnhancementPercent,
                additionalChargesEnhanced = evaluable.Count - 1
            }),
            // Evidence link to the exact charge — the Charges & Security drawer shows this finding under
            // "Related findings" for that one charge only.
            SourceReferenceJson: JsonSerializer.Serialize(new
            {
                entityType = nameof(RocCharge),
                entityIds = new[] { top.Charge.ChargeId },
                chargeNumber = top.Charge.RocChargeNumber
            })));
    }
}
