using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class FinancialRules
{
    public const string RevenueDecline1YCode = "FIN_REVENUE_DECLINE_1Y";
    public const string RevenueDecline2YCode = "FIN_REVENUE_DECLINE_2Y";
    public const string RevenueGrowthCode = "FIN_REVENUE_GROWTH";
    public const string EbitdaNegativeCode = "FIN_EBITDA_NEGATIVE";
    public const string PatNegativeCode = "FIN_PAT_NEGATIVE";
    public const string NetWorthNegativeCode = "FIN_NET_WORTH_NEGATIVE";
    public const string NetWorthErosionCode = "FIN_NET_WORTH_EROSION";
    public const string LeverageHighCode = "FIN_LEVERAGE_HIGH";
    public const string LeverageLowCode = "FIN_LEVERAGE_LOW";
    public const string CurrentRatioCode = "FIN_CURRENT_RATIO";
    public const string DebtorDaysIncreaseCode = "FIN_DEBTOR_DAYS_INCREASE";
    public const string PayableDaysHighCode = "FIN_PAYABLE_DAYS_HIGH";
    public const string InterestCoverageCriticalCode = "FIN_INTEREST_COVERAGE_CRITICAL";
    public const string CfoNegativeCode = "FIN_CFO_NEGATIVE";
    public const string PatCfoDivergenceCode = "FIN_PAT_CFO_DIVERGENCE";
    public const string CfoHealthyCode = "FIN_CFO_HEALTHY";

    public const string RevenueTrendGroup = "REVENUE_TREND";
    public const string ProfitabilityTrendGroup = "PROFITABILITY_TREND";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();
        var latest = ctx.LatestFinancialYear;
        var prior = ctx.PriorFinancialYear;
        var secondPrior = ctx.SecondPriorFinancialYear;

        if (latest is null)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(RevenueDecline1YCode, "No FinancialYearData available."));
            return outcomes;
        }

        outcomes.Add(EvaluateRevenue1Y(latest, prior, thresholds));
        outcomes.Add(EvaluateRevenue2Y(latest, prior, secondPrior, thresholds));
        outcomes.Add(EvaluateRevenueGrowth(latest, prior, secondPrior));
        outcomes.Add(EvaluateEbitda(latest));
        outcomes.Add(EvaluatePat(latest));
        outcomes.Add(EvaluateNetWorth(latest, prior));
        outcomes.Add(EvaluateLeverage(latest, thresholds));
        outcomes.Add(EvaluateCurrentRatio(latest, thresholds));
        outcomes.Add(EvaluateDebtorDays(latest, prior, thresholds));
        outcomes.Add(EvaluatePayableDays(latest, prior, thresholds));
        outcomes.Add(EvaluateInterestCoverage(latest));
        outcomes.AddRange(EvaluateCashFlow(latest, prior));

        return outcomes;
    }

    private static RuleEvaluationOutcome EvaluateRevenue1Y(FinancialYearData latest, FinancialYearData? prior, RuleThresholds t)
    {
        if (prior?.Revenue is not { } priorRev || latest.Revenue is not { } latestRev || priorRev <= 0)
            return RuleEvaluationOutcome.NotEvaluated(RevenueDecline1YCode, "Prior-year revenue unavailable or non-positive.");

        var changePercent = (latestRev - priorRev) / priorRev * 100m;
        if (changePercent >= -t.MaterialRevenueDeclinePercent)
            return RuleEvaluationOutcome.NotTriggered();

        var isCritical = changePercent <= -t.CriticalRevenueDeclinePercent;
        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
            RevenueDecline1YCode, isCritical ? "Material Revenue Decline" : "Revenue Decline",
            $"Revenue declined from ₹{priorRev:0.##} Cr (FY{prior.FinancialYear}) to ₹{latestRev:0.##} Cr (FY{latest.FinancialYear}), a change of {changePercent:0.#}%.",
            MetricsJson: JsonSerializer.Serialize(new { priorYear = prior.FinancialYear, priorRevenue = priorRev, latestYear = latest.FinancialYear, latestRevenue = latestRev, changePercent }),
            PeriodLabel: $"FY{prior.FinancialYear}-FY{latest.FinancialYear}",
            ConsolidationGroup: RevenueTrendGroup));
    }

    private static RuleEvaluationOutcome EvaluateRevenue2Y(FinancialYearData latest, FinancialYearData? prior, FinancialYearData? secondPrior, RuleThresholds t)
    {
        if (prior?.Revenue is not { } priorRev || secondPrior?.Revenue is not { } secondPriorRev || latest.Revenue is not { } latestRev
            || priorRev <= 0 || secondPriorRev <= 0)
            return RuleEvaluationOutcome.NotEvaluated(RevenueDecline2YCode, "Fewer than three years of revenue data available.");

        var declinedLatestYear = latestRev < priorRev;
        var declinedPriorYear = priorRev < secondPriorRev;
        if (!declinedLatestYear || !declinedPriorYear)
            return RuleEvaluationOutcome.NotTriggered();

        // Two consecutive declines are their own signal even if each year individually is small — this is
        // intentionally a separate code from the 1-year rule, not a duplicate; FindingConsolidator merges
        // both into one "Sustained Revenue Contraction" card when they co-fire.
        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Trend,
            RevenueDecline2YCode, "Sustained Revenue Contraction",
            $"Revenue declined for two consecutive years: ₹{secondPriorRev:0.##} Cr (FY{secondPrior.FinancialYear}) → ₹{priorRev:0.##} Cr (FY{prior.FinancialYear}) → ₹{latestRev:0.##} Cr (FY{latest.FinancialYear}).",
            MetricsJson: JsonSerializer.Serialize(new
            {
                secondPriorYear = secondPrior.FinancialYear, secondPriorRevenue = secondPriorRev,
                priorYear = prior.FinancialYear, priorRevenue = priorRev,
                latestYear = latest.FinancialYear, latestRevenue = latestRev
            }),
            PeriodLabel: $"FY{secondPrior.FinancialYear}-FY{latest.FinancialYear}",
            ConsolidationGroup: RevenueTrendGroup));
    }

    private static RuleEvaluationOutcome EvaluateRevenueGrowth(FinancialYearData latest, FinancialYearData? prior, FinancialYearData? secondPrior)
    {
        if (prior?.Revenue is not { } priorRev || secondPrior?.Revenue is not { } secondPriorRev || latest.Revenue is not { } latestRev
            || priorRev <= 0 || secondPriorRev <= 0)
            return RuleEvaluationOutcome.NotEvaluated(RevenueGrowthCode, "Fewer than three years of revenue data available.");

        if (!(latestRev > priorRev && priorRev > secondPriorRev))
            return RuleEvaluationOutcome.NotTriggered();

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Financial, FindingSeverity.Positive, TemporalStatus.Trend,
            RevenueGrowthCode, "Consistent Revenue Growth",
            $"Revenue grew for two consecutive years: ₹{secondPriorRev:0.##} Cr → ₹{priorRev:0.##} Cr → ₹{latestRev:0.##} Cr.",
            MetricsJson: JsonSerializer.Serialize(new { secondPriorRevenue = secondPriorRev, priorRevenue = priorRev, latestRevenue = latestRev }),
            DisplayPriority: 10));
    }

    private static RuleEvaluationOutcome EvaluateEbitda(FinancialYearData latest)
    {
        if (latest.Ebitda is not { } ebitda)
            return RuleEvaluationOutcome.NotEvaluated(EbitdaNegativeCode, "EBITDA not available for the latest financial year.");

        return ebitda < 0
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                EbitdaNegativeCode, "Negative EBITDA",
                $"EBITDA for FY{latest.FinancialYear} is negative (₹{ebitda:0.##} Cr).",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, ebitda }),
                PeriodLabel: $"FY{latest.FinancialYear}",
                ConsolidationGroup: ProfitabilityTrendGroup))
            : RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluatePat(FinancialYearData latest)
    {
        if (latest.Pat is not { } pat)
            return RuleEvaluationOutcome.NotEvaluated(PatNegativeCode, "PAT not available for the latest financial year.");

        return pat < 0
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                PatNegativeCode, "Net Loss",
                $"PAT for FY{latest.FinancialYear} is negative (₹{pat:0.##} Cr).",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, pat }),
                PeriodLabel: $"FY{latest.FinancialYear}",
                ConsolidationGroup: ProfitabilityTrendGroup))
            : RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluateNetWorth(FinancialYearData latest, FinancialYearData? prior)
    {
        if (latest.NetWorth is not { } netWorth)
            return RuleEvaluationOutcome.NotEvaluated(NetWorthNegativeCode, "Net worth not available for the latest financial year.");

        if (netWorth < 0)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current,
                NetWorthNegativeCode, "Negative Net Worth",
                $"Net worth for FY{latest.FinancialYear} is negative (₹{netWorth:0.##} Cr).",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, netWorth }),
                PeriodLabel: $"FY{latest.FinancialYear}"));

        if (prior?.NetWorth is { } priorNetWorth && priorNetWorth > 0 && netWorth < priorNetWorth)
        {
            var declinePercent = (priorNetWorth - netWorth) / priorNetWorth * 100m;
            if (declinePercent > 10m)
                return RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.Financial, FindingSeverity.Watch, TemporalStatus.Trend,
                    NetWorthErosionCode, "Net Worth Erosion",
                    $"Net worth declined from ₹{priorNetWorth:0.##} Cr (FY{prior.FinancialYear}) to ₹{netWorth:0.##} Cr (FY{latest.FinancialYear}).",
                    MetricsJson: JsonSerializer.Serialize(new { priorNetWorth, latestNetWorth = netWorth, declinePercent }),
                    PeriodLabel: $"FY{prior.FinancialYear}-FY{latest.FinancialYear}"));
        }

        return RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluateLeverage(FinancialYearData latest, RuleThresholds t)
    {
        if (latest.TotalDebt is not { } debt || latest.NetWorth is not { } netWorth)
            return RuleEvaluationOutcome.NotEvaluated(LeverageHighCode, "Total debt or net worth not available for the latest financial year.");
        if (netWorth <= 0)
            return RuleEvaluationOutcome.NotEvaluated(LeverageHighCode, "Net worth is not positive — leverage ratio not meaningful (see negative net worth finding instead).");

        var ratio = debt / netWorth;
        if (ratio > t.HighLeverageRatio)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                LeverageHighCode, "High Leverage",
                $"Total debt to net worth ratio for FY{latest.FinancialYear} is {ratio:0.##}x.",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, totalDebt = debt, netWorth, ratio }),
                PeriodLabel: $"FY{latest.FinancialYear}"));

        if (ratio < 0.5m)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Positive, TemporalStatus.Current,
                LeverageLowCode, "Low Leverage",
                $"Total debt to net worth ratio for FY{latest.FinancialYear} is {ratio:0.##}x.",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, totalDebt = debt, netWorth, ratio }),
                PeriodLabel: $"FY{latest.FinancialYear}",
                DisplayPriority: 10));

        return RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluateCurrentRatio(FinancialYearData latest, RuleThresholds t)
    {
        if (latest.CurrentAssets is not { } assets || latest.CurrentLiabilities is not { } liabilities || liabilities <= 0)
            return RuleEvaluationOutcome.NotEvaluated(CurrentRatioCode, "Current assets or current liabilities not available for the latest financial year.");

        var ratio = assets / liabilities;
        var metrics = JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, currentAssets = assets, currentLiabilities = liabilities, ratio });
        var period = $"FY{latest.FinancialYear}";

        if (ratio < t.MinCurrentRatio)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                CurrentRatioCode, "Weak Liquidity", $"Current ratio for FY{latest.FinancialYear} is {ratio:0.##}, below 1.0.",
                MetricsJson: metrics, PeriodLabel: period));

        if (ratio <= t.WatchCurrentRatioCeiling)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Watch, TemporalStatus.Current,
                CurrentRatioCode, "Modest Liquidity", $"Current ratio for FY{latest.FinancialYear} is {ratio:0.##}.",
                MetricsJson: metrics, PeriodLabel: period));

        if (ratio > t.StrongCurrentRatioFloor)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Positive, TemporalStatus.Current,
                CurrentRatioCode, "Strong Liquidity", $"Current ratio for FY{latest.FinancialYear} is {ratio:0.##}.",
                MetricsJson: metrics, PeriodLabel: period, DisplayPriority: 10));

        return RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluateDebtorDays(FinancialYearData latest, FinancialYearData? prior, RuleThresholds t)
    {
        if (latest.TradeReceivables is not { } receivables || latest.Revenue is not { } revenue || revenue <= 0)
            return RuleEvaluationOutcome.NotEvaluated(DebtorDaysIncreaseCode, "Trade receivables or revenue not available for the latest financial year.");

        var latestDays = receivables / revenue * 365m;

        if (prior?.TradeReceivables is not { } priorReceivables || prior.Revenue is not { } priorRevenue || priorRevenue <= 0)
            return RuleEvaluationOutcome.NotEvaluated(DebtorDaysIncreaseCode, "Prior-year trade receivables/revenue not available — trend not evaluated.");

        var priorDays = priorReceivables / priorRevenue * 365m;
        if (priorDays <= 0)
            return RuleEvaluationOutcome.NotTriggered();

        var changePercent = (latestDays - priorDays) / priorDays * 100m;
        if (changePercent <= t.MaterialDebtorDaysIncreasePercent)
            return RuleEvaluationOutcome.NotTriggered();

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Trend,
            DebtorDaysIncreaseCode, "Debtor Days Increasing",
            $"Debtor days increased from {priorDays:0} (FY{prior.FinancialYear}) to {latestDays:0} (FY{latest.FinancialYear}).",
            MetricsJson: JsonSerializer.Serialize(new { priorDays, latestDays, changePercent }),
            PeriodLabel: $"FY{prior.FinancialYear}-FY{latest.FinancialYear}"));
    }

    private static RuleEvaluationOutcome EvaluatePayableDays(FinancialYearData latest, FinancialYearData? prior, RuleThresholds t)
    {
        if (latest.TradePayables is not { } payables || latest.Revenue is not { } revenue || revenue <= 0)
            return RuleEvaluationOutcome.NotEvaluated(PayableDaysHighCode, "Trade payables or revenue not available for the latest financial year.");

        var latestDays = payables / revenue * 365m;

        if (prior?.TradePayables is not { } priorPayables || prior.Revenue is not { } priorRevenue || priorRevenue <= 0)
            return RuleEvaluationOutcome.NotEvaluated(PayableDaysHighCode, "Prior-year trade payables/revenue not available — trend not evaluated.");

        var priorDays = priorPayables / priorRevenue * 365m;
        if (priorDays <= 0)
            return RuleEvaluationOutcome.NotTriggered();

        var changePercent = (latestDays - priorDays) / priorDays * 100m;
        if (changePercent <= t.MaterialPayableDaysIncreasePercent)
            return RuleEvaluationOutcome.NotTriggered();

        // Feeds CROSS_SUPPLIER_PAYMENT_PRESSURE alongside MSME materiality — this rule exists specifically
        // so that cross-section rule has a real payable-side signal instead of reusing debtor days.
        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Trend,
            PayableDaysHighCode, "Payable Days Increasing",
            $"Payable days increased from {priorDays:0} (FY{prior.FinancialYear}) to {latestDays:0} (FY{latest.FinancialYear}).",
            MetricsJson: JsonSerializer.Serialize(new { priorDays, latestDays, changePercent }),
            PeriodLabel: $"FY{prior.FinancialYear}-FY{latest.FinancialYear}"));
    }

    private static RuleEvaluationOutcome EvaluateInterestCoverage(FinancialYearData latest)
    {
        if (latest.Ebit is not { } ebit || latest.FinanceCost is not { } financeCost)
            return RuleEvaluationOutcome.NotEvaluated(InterestCoverageCriticalCode, "EBIT or finance cost not available for the latest financial year.");

        if (financeCost <= 0)
            return RuleEvaluationOutcome.NotTriggered(); // no interest burden to cover

        // When EBIT is negative, the resulting ratio's magnitude is not meaningful ("-58x" isn't more
        // concerning than "-5x") — classify by sign, not by how negative the ratio is.
        if (ebit < 0)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current,
                InterestCoverageCriticalCode, "Interest Coverage Critical",
                "Operating earnings are insufficient to cover finance costs (EBIT is negative).",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, ebit, financeCost }),
                PeriodLabel: $"FY{latest.FinancialYear}"));

        var ratio = ebit / financeCost;
        if (ratio < 1m)
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Critical, TemporalStatus.Current,
                InterestCoverageCriticalCode, "Interest Coverage Critical",
                $"Operating earnings cover only {ratio:0.##}x finance costs for FY{latest.FinancialYear} — weak but positive coverage.",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, ebit, financeCost, ratio }),
                PeriodLabel: $"FY{latest.FinancialYear}"));

        return RuleEvaluationOutcome.NotTriggered();
    }

    /// <summary>Reports on three distinct codes (CfoNegative, PatCfoDivergence, CfoHealthy — mutually
    /// exclusive in practice, but each needs its own outcome so a NotEvaluated/NotTriggered result for one
    /// isn't silently dropped when another fires).</summary>
    private static List<RuleEvaluationOutcome> EvaluateCashFlow(FinancialYearData latest, FinancialYearData? prior)
    {
        var results = new List<RuleEvaluationOutcome>();

        if (latest.Cfo is not { } cfo)
        {
            var reason = "Cash flow from operations not available for the latest financial year.";
            return [RuleEvaluationOutcome.NotEvaluated(CfoNegativeCode, reason),
                    RuleEvaluationOutcome.NotEvaluated(PatCfoDivergenceCode, reason),
                    RuleEvaluationOutcome.NotEvaluated(CfoHealthyCode, reason)];
        }

        // The source cash-flow section carries no year header — CFO/CFI/CFF were aligned to years by
        // column position and flagged for manual review. Don't draw an automated conclusion from them.
        if (latest.CashFlowYearInferred || (prior?.CashFlowYearInferred ?? false))
        {
            var reason = "Cash-flow figures are excluded from automated analysis: the source has no year " +
                         "header for the cash-flow section, so year alignment is inferred and needs manual verification.";
            return [RuleEvaluationOutcome.NotEvaluated(CfoNegativeCode, reason),
                    RuleEvaluationOutcome.NotEvaluated(PatCfoDivergenceCode, reason),
                    RuleEvaluationOutcome.NotEvaluated(CfoHealthyCode, reason)];
        }

        if (cfo < 0)
        {
            var twoConsecutive = prior?.Cfo is { } priorCfo && priorCfo < 0;
            results.Add(RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial,
                twoConsecutive ? FindingSeverity.Review : FindingSeverity.Watch,
                twoConsecutive ? TemporalStatus.Trend : TemporalStatus.Current,
                CfoNegativeCode, "Negative Operating Cash Flow",
                twoConsecutive
                    ? $"Cash flow from operations was negative for two consecutive years (FY{prior!.FinancialYear} and FY{latest.FinancialYear})."
                    : $"Cash flow from operations for FY{latest.FinancialYear} is negative (₹{cfo:0.##} Cr).",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, cfo, priorCfo = prior?.Cfo }),
                PeriodLabel: $"FY{latest.FinancialYear}")));
        }
        else
        {
            results.Add(RuleEvaluationOutcome.NotTriggered());
        }

        if (latest.Pat is not { } pat)
        {
            var reason = "PAT not available for the latest financial year.";
            results.Add(RuleEvaluationOutcome.NotEvaluated(PatCfoDivergenceCode, reason));
            results.Add(RuleEvaluationOutcome.NotEvaluated(CfoHealthyCode, reason));
        }
        else if (pat > 0 && cfo < 0)
        {
            results.Add(RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Review, TemporalStatus.Current,
                PatCfoDivergenceCode, "PAT/Cash Flow Divergence",
                $"PAT is positive (₹{pat:0.##} Cr) while cash flow from operations is negative (₹{cfo:0.##} Cr) for FY{latest.FinancialYear}.",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, pat, cfo }),
                PeriodLabel: $"FY{latest.FinancialYear}")));
            results.Add(RuleEvaluationOutcome.NotTriggered());
        }
        else if (pat > 0 && cfo > 0)
        {
            results.Add(RuleEvaluationOutcome.NotTriggered());
            results.Add(RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Financial, FindingSeverity.Positive, TemporalStatus.Current,
                CfoHealthyCode, "Cash Flow Aligned With Profit",
                $"PAT and cash flow from operations are both positive for FY{latest.FinancialYear}.",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, pat, cfo }),
                PeriodLabel: $"FY{latest.FinancialYear}", DisplayPriority: 10)));
        }
        else
        {
            results.Add(RuleEvaluationOutcome.NotTriggered());
            results.Add(RuleEvaluationOutcome.NotTriggered());
        }

        return results;
    }
}
