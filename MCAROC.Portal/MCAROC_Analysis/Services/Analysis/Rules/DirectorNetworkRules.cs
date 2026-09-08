using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class DirectorNetworkRules
{
    public const string LargeNetworkCode = "DIR_NETWORK_LARGE";
    public const string DistressedEntityCurrentCode = "DIR_NETWORK_DISTRESSED_ENTITY";
    public const string DistressedEntityHistoricalCode = "DIR_NETWORK_DISTRESSED_ENTITY_HISTORICAL";

    private static readonly NormalizedCompanyStatus[] DistressedStatuses =
    [
        NormalizedCompanyStatus.StruckOff, NormalizedCompanyStatus.UnderLiquidation,
        NormalizedCompanyStatus.Liquidated, NormalizedCompanyStatus.UnderCirp
    ];

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();
        var associations = ctx.DirectorAssociations;

        if (associations.Count == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(LargeNetworkCode, "No DirectorAssociation records available."));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(DistressedEntityCurrentCode, "No DirectorAssociation records available."));
            return outcomes;
        }

        var byDirector = associations.GroupBy(a => string.IsNullOrEmpty(a.DirectorDin) ? a.DirectorNameRaw : a.DirectorDin).ToList();
        var widest = byDirector.OrderByDescending(g => g.Count()).First();

        // Fan-out alone is Watch/Info only — a director on several boards isn't inherently concerning; it
        // only escalates below when combined with a distressed connected entity.
        outcomes.Add(widest.Count() >= thresholds.DirectorFanOutThreshold
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.DirectorNetwork, FindingSeverity.Watch, TemporalStatus.Current,
                LargeNetworkCode, "Director With Large Company Network",
                $"A common director is associated with {widest.Count()} other companies.",
                MetricsJson: JsonSerializer.Serialize(new { directorName = widest.First().DirectorNameRaw, connectedCompanies = widest.Count() }),
                DisplayPriority: -10))
            : RuleEvaluationOutcome.NotTriggered());

        var today = DateOnly.FromDateTime(ctx.AnalysisDate);
        var distressed = associations.Where(a => DistressedStatuses.Contains(CompanyStatusNormalizer.Normalize(a.CompanyStatus))).ToList();
        var currentDistressed = distressed.Where(a => a.CessationDate is null || a.CessationDate > today).ToList();
        var historicalDistressed = distressed.Where(a => a.CessationDate is { } c && c <= today).ToList();

        outcomes.Add(currentDistressed.Count > 0
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.DirectorNetwork, FindingSeverity.Review, TemporalStatus.Current,
                DistressedEntityCurrentCode, "Director Currently Linked to Distressed Entity",
                $"{currentDistressed.Count} current director association(s) are with a company recorded as struck off, under liquidation, liquidated, or under CIRP.",
                MetricsJson: JsonSerializer.Serialize(new { associations = currentDistressed.Select(a => new { a.DirectorNameRaw, a.ConnectedCompanyNormalized, a.CompanyStatus }) })))
            : RuleEvaluationOutcome.NotTriggered());

        outcomes.Add(historicalDistressed.Count > 0 && currentDistressed.Count == 0
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.DirectorNetwork, FindingSeverity.Watch, TemporalStatus.Historical,
                DistressedEntityHistoricalCode, "Historical Director Association With Distressed Entity",
                $"{historicalDistressed.Count} past director association(s) (now ceased) were with a company recorded as struck off, under liquidation, liquidated, or under CIRP.",
                MetricsJson: JsonSerializer.Serialize(new { associations = historicalDistressed.Select(a => new { a.DirectorNameRaw, a.ConnectedCompanyNormalized, a.CompanyStatus }) }),
                DisplayPriority: -10))
            : RuleEvaluationOutcome.NotTriggered());

        return outcomes;
    }
}
