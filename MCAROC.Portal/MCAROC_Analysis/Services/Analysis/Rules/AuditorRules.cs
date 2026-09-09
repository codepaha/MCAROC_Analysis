using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>Negation-aware before severity-tier keyword matching — "no fraud has been reported" and "no
/// material uncertainty relating to going concern exists" must never hit the adverse tiers just because
/// they contain "fraud"/"going concern" as substrings.</summary>
public static class AuditorRules
{
    public const string AdverseOpinionCode = "AUDITOR_ADVERSE_OPINION";
    public const string QualificationCode = "AUDITOR_QUALIFICATION";
    public const string MinorObservationCode = "AUDITOR_MINOR_OBSERVATION";
    public const string CleanOpinionCode = "AUDITOR_CLEAN_OPINION";

    private static readonly string[] NegationPatterns =
        ["no fraud", "not identified", "no qualification", "no adverse remark", "no material uncertainty", "no going concern"];
    private static readonly string[] CriticalKeywords =
        ["adverse opinion", "disclaimer", "going concern", "fraud", "material default"];
    private static readonly string[] ReviewKeywords =
        ["qualified opinion", "material weakness", "material control weakness", "statutory dues"];

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx)
    {
        var latest = ctx.AuditorObservations.OrderByDescending(a => a.FinancialYear).FirstOrDefault();
        if (latest is null)
            return [RuleEvaluationOutcome.NotEvaluated(AdverseOpinionCode, "No AuditorObservation records available.")];

        var text = (latest.ObservationText ?? "").ToLowerInvariant();
        var hasNegation = NegationPatterns.Any(p => text.Contains(p, StringComparison.Ordinal));

        // Phase 1 already parses HasQualificationOrAdverseRemark as a structured field — trust that over
        // re-deriving it from free text; text tiering below only stratifies severity once it's true, with
        // the negation check as a defensive backstop.
        if (!latest.HasQualificationOrAdverseRemark || hasNegation)
        {
            return [RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Auditor, FindingSeverity.Positive, TemporalStatus.Current,
                CleanOpinionCode, "Clean Audit Opinion",
                $"No qualification or adverse remark recorded for FY{latest.FinancialYear}.",
                MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, auditorName = latest.AuditorName }),
                PeriodLabel: $"FY{latest.FinancialYear}", DisplayPriority: 10))];
        }

        var isCritical = CriticalKeywords.Any(k => text.Contains(k, StringComparison.Ordinal));
        var isReview = !isCritical && ReviewKeywords.Any(k => text.Contains(k, StringComparison.Ordinal));

        var (code, severity, title) = isCritical
            ? (AdverseOpinionCode, FindingSeverity.Critical, "Adverse Auditor Opinion")
            : isReview
                ? (QualificationCode, FindingSeverity.Review, "Auditor Qualification")
                : (MinorObservationCode, FindingSeverity.Watch, "Minor Auditor Observation");

        return [RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Auditor, severity, TemporalStatus.Current,
            code, title,
            latest.ObservationText ?? $"Auditor observation recorded for FY{latest.FinancialYear}.",
            MetricsJson: JsonSerializer.Serialize(new { financialYear = latest.FinancialYear, auditorName = latest.AuditorName }),
            PeriodLabel: $"FY{latest.FinancialYear}"))];
    }
}
