using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Services.Analysis;

public record RuleEngineResult(
    IReadOnlyList<FindingDraft> Findings,
    ReviewPriority OverallReviewPriority,
    IReadOnlyList<(string Code, string Reason)> DataSufficiencyNotes);

/// <summary>Runs every domain rule set, then consolidation, then deterministic cross-section rules, then
/// the deterministic review-priority calculation — pure function of AnalysisContext, no DB access.</summary>
public static class RuleEngine
{
    public static RuleEngineResult Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var allOutcomes = new List<RuleEvaluationOutcome>();
        allOutcomes.AddRange(CompanyProfileRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(DirectorRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(DirectorNetworkRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(OwnershipRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(FinancialRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(ChargeRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(MsmeRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(GstRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(EpfoRules.Evaluate(ctx, thresholds));
        allOutcomes.AddRange(AuditorRules.Evaluate(ctx));
        allOutcomes.AddRange(LitigationRules.Evaluate(ctx));

        var rawFindings = allOutcomes.Where(o => o.Status == RuleEvaluationStatus.Triggered).Select(o => o.Finding!).ToList();
        var dataSufficiencyNotes = allOutcomes
            .Where(o => o.Status == RuleEvaluationStatus.NotEvaluated)
            .Select(o => (o.Code!, o.NotEvaluatedReason!))
            .ToList();

        var consolidated = FindingConsolidator.Consolidate(rawFindings);
        var crossSection = CrossSectionRules.Evaluate(consolidated);
        var allFindings = consolidated.Concat(crossSection).ToList();

        var priority = ReviewPriorityCalculator.Calculate(allFindings);

        return new RuleEngineResult(allFindings, priority, dataSufficiencyNotes);
    }
}
