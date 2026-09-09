using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class OwnershipRules
{
    public const string PromoterHoldingDeclineCode = "OWN_PROMOTER_HOLDING_DECLINE";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();
        var promoterRows = ctx.Shareholdings.Where(s => s.IsPromoter).ToList();

        if (promoterRows.Count == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(PromoterHoldingDeclineCode, "No promoter Shareholding records available."));
            return outcomes;
        }

        // Sum by year, deduplicating by normalized shareholder name — Phase 1 merges the "Director
        // Shareholding" and "Shareholding > 5%" sheets, which can produce duplicate rows for the same
        // holder/year (take the max reported percentage per holder rather than summing duplicates).
        var byYear = promoterRows
            .GroupBy(s => s.FinancialYear)
            .Select(g => new
            {
                Year = g.Key,
                TotalPercent = g.GroupBy(s => s.ShareholderNameNormalized).Sum(ng => ng.Max(s => s.HoldingPercentage ?? 0))
            })
            .OrderByDescending(x => x.Year)
            .ToList();

        var tolerance = 100m + thresholds.PromoterHoldingToleranceOverHundredPercent;
        if (byYear.Any(y => y.TotalPercent > tolerance))
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(PromoterHoldingDeclineCode,
                "Computed total promoter holding exceeds 100% plus tolerance in at least one year — likely duplicate/overlapping shareholding rows; trend not evaluated."));
            return outcomes;
        }

        if (byYear.Count < 2)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(PromoterHoldingDeclineCode, "Fewer than two years of promoter holding data available."));
            return outcomes;
        }

        var latest = byYear[0];
        var prior = byYear[1];
        var declinePoints = prior.TotalPercent - latest.TotalPercent;

        outcomes.Add(declinePoints > thresholds.MaterialPromoterHoldingDeclinePoints
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Ownership, FindingSeverity.Review, TemporalStatus.Trend,
                PromoterHoldingDeclineCode, "Promoter Holding Decline",
                $"Promoter holding declined from {prior.TotalPercent:0.##}% (FY{prior.Year}) to {latest.TotalPercent:0.##}% (FY{latest.Year}).",
                MetricsJson: JsonSerializer.Serialize(new
                {
                    priorYear = prior.Year, priorPercent = prior.TotalPercent,
                    latestYear = latest.Year, latestPercent = latest.TotalPercent, declinePoints
                }),
                PeriodLabel: $"FY{prior.Year}-FY{latest.Year}"))
            : RuleEvaluationOutcome.NotTriggered());

        return outcomes;
    }
}
