using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class GstRules
{
    public const string RegistrationCancelledCode = "GST_REGISTRATION_CANCELLED";
    public const string FilingDelaysFrequentCode = "GST_FILING_DELAYS_FREQUENT";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();

        if (ctx.GstRegistrations.Count == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(RegistrationCancelledCode, "No GstRegistration records available."));
        }
        else
        {
            // GST source sheets normally provide Status but no cancellation-date column. Treat a
            // registration as cancelled/inactive whenever it is not affirmatively active, while a
            // date remains authoritative when one is available.
            var cancelled = ctx.GstRegistrations.Where(g => !GstRegistrationStatus.IsActive(g)).ToList();
            if (cancelled.Count == 0)
            {
                outcomes.Add(RuleEvaluationOutcome.NotTriggered());
            }
            else
            {
                // Temporal status (the cancellation event is always a past fact) is separate from severity
                // (whether the company currently has no active GST registration at all, vs. an active
                // replacement exists) — a single cancelled registration among otherwise-active ones is a
                // low-severity historical note, not a current compliance gap.
                var activeExists = ctx.GstRegistrations.Any(GstRegistrationStatus.IsActive);
                outcomes.Add(RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.Gst,
                    activeExists ? FindingSeverity.Watch : FindingSeverity.Review,
                    activeExists ? TemporalStatus.Historical : TemporalStatus.Current,
                    RegistrationCancelledCode,
                    activeExists ? "GST Registration Cancelled (Active Replacement Exists)" : "No Active GST Registration",
                    activeExists
                        ? $"{cancelled.Count} of {ctx.GstRegistrations.Count} GST registration(s) are cancelled; at least one other registration remains active."
                        : $"All {ctx.GstRegistrations.Count} known GST registration(s) are cancelled or inactive, with none currently active.",
                    MetricsJson: JsonSerializer.Serialize(new { cancelledCount = cancelled.Count, totalRegistrations = ctx.GstRegistrations.Count, activeExists }))));
            }
        }

        var filings = ctx.GstRegistrations.SelectMany(g => g.Filings).ToList();
        if (filings.Count == 0)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(FilingDelaysFrequentCode, "No GstFiling records available."));
        }
        else
        {
            var delayed = filings.Where(f => f.DelayDays is > 7).ToList();
            outcomes.Add(delayed.Count >= 2
                ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.Gst, FindingSeverity.Watch, TemporalStatus.Trend,
                    FilingDelaysFrequentCode, "Frequent GST Filing Delays",
                    $"{delayed.Count} of {filings.Count} GST returns were filed more than 7 days late.",
                    MetricsJson: JsonSerializer.Serialize(new { delayedCount = delayed.Count, totalFilings = filings.Count })))
                : RuleEvaluationOutcome.NotTriggered());
        }

        return outcomes;
    }
}
