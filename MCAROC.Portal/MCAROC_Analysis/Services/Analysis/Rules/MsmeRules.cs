using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class MsmeRules
{
    public const string MsmeDueMaterialCode = "MSME_DUE_MATERIAL";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        if (ctx.MsmePayments.Count == 0)
            return [RuleEvaluationOutcome.NotEvaluated(MsmeDueMaterialCode, "No MsmePayment records available.")];

        // ReportingPeriod is free source text (not a normalized date) — take the lexicographically latest
        // group as a best-effort "most recent period" (typical formats like "FY2024-25" sort sensibly).
        var latestPeriod = ctx.MsmePayments.Select(m => m.ReportingPeriod).Distinct().OrderDescending(StringComparer.Ordinal).First();
        var latestDue = ctx.MsmePayments.Where(m => m.ReportingPeriod == latestPeriod).Sum(m => m.AmountDueCrore ?? 0);

        if (latestDue <= 0)
            return [RuleEvaluationOutcome.NotTriggered()];

        if (ctx.Materiality.TradePayables is not { } tradePayables || tradePayables <= 0)
            return [RuleEvaluationOutcome.NotEvaluated(MsmeDueMaterialCode, "Trade payables not available or not positive — MSME materiality ratio not evaluated.")];

        var ratioPercent = latestDue / tradePayables * 100m;
        if (ratioPercent < thresholds.MaterialMsmeToPayablesRatioPercent)
            return [RuleEvaluationOutcome.NotTriggered()];

        var caveat = ratioPercent > 100m
            ? " Figures may be drawn from different reporting periods/bases and are not directly comparable."
            : "";

        return [RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Msme, FindingSeverity.Review, TemporalStatus.Current,
            MsmeDueMaterialCode, "Material MSME Dues Outstanding",
            $"MSME dues outstanding (₹{latestDue:0.##} Cr, period {latestPeriod}) are {ratioPercent:0.#}% of latest reported trade payables.{caveat}",
            MetricsJson: JsonSerializer.Serialize(new { period = latestPeriod, msmeDue = latestDue, tradePayables, ratioPercent }),
            PeriodLabel: latestPeriod))];
    }
}
