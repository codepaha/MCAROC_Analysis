using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class DirectorRules
{
    public const string ZeroActiveDirectorsCode = "DIR_ZERO_ACTIVE_DIRECTORS";
    public const string PromoterExitCode = "DIR_PROMOTER_EXIT";
    public const string DirectorExitCode = "DIR_EXIT";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();
        var directors = ctx.Directors;

        if (directors.Count == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(ZeroActiveDirectorsCode, "No Director records available."));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(DirectorExitCode, "No Director records available."));
            return outcomes;
        }

        var activeCount = directors.Count(d => d.CessationDate is null);
        if (activeCount == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Directors, FindingSeverity.Critical, TemporalStatus.Current,
                ZeroActiveDirectorsCode, "No Currently Active Directors",
                $"All {directors.Count} known director(s) show a cessation date; none are currently active.",
                MetricsJson: JsonSerializer.Serialize(new { totalDirectors = directors.Count }))));
        }
        else
        {
            outcomes.Add(RuleEvaluationOutcome.NotTriggered());
        }

        // Best-effort promoter-director match: Director has no IsPromoter flag of its own (only
        // Shareholding does) — a cessated director is treated as a promoter exit when their normalized
        // name matches a promoter Shareholding row in the latest available year. Same normalization
        // already used across Phase 1's parsers; documented as a heuristic, not a guaranteed identity match.
        var latestPromoterYear = ctx.Shareholdings.Where(s => s.IsPromoter).Select(s => (int?)s.FinancialYear).Max();
        var latestPromoterNames = latestPromoterYear is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : ctx.Shareholdings
                .Where(s => s.IsPromoter && s.FinancialYear == latestPromoterYear)
                .Select(s => s.ShareholderNameNormalized)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cutoff = DateOnly.FromDateTime(ctx.AnalysisDate.AddMonths(-thresholds.DirectorCessationLookbackMonths));
        var recentExits = directors.Where(d => d.CessationDate is { } c && c >= cutoff).ToList();

        if (recentExits.Count == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.NotTriggered());
        }
        else
        {
            var boardExitRatio = (decimal)recentExits.Count / directors.Count;
            var promoterExits = recentExits.Where(d => latestPromoterNames.Contains(d.NameNormalized)).ToList();
            var isPromoterExit = promoterExits.Count > 0;

            outcomes.Add(RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Directors,
                isPromoterExit ? FindingSeverity.Review : FindingSeverity.Watch,
                TemporalStatus.Current,
                isPromoterExit ? PromoterExitCode : DirectorExitCode,
                isPromoterExit ? "Promoter Director Exit" : "Director Exit(s)",
                $"{recentExits.Count} of {directors.Count} known director(s) ceased within the last {thresholds.DirectorCessationLookbackMonths} months"
                    + (isPromoterExit ? $", including {promoterExits.Count} identified as promoter(s) by shareholding records." : "."),
                MetricsJson: JsonSerializer.Serialize(new
                {
                    recentExits = recentExits.Count,
                    totalDirectors = directors.Count,
                    boardExitRatio = Math.Round(boardExitRatio, 2),
                    promoterExits = promoterExits.Count
                }))));
        }

        return outcomes;
    }
}
