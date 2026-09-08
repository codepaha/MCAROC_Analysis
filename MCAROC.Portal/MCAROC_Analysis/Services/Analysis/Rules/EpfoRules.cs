using System.Globalization;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>The EPFO "behaviour engine" — headcount trend, payment-delay frequency/trend, missing
/// contributions, payroll contraction, and erratic compliance patterns, evaluated together rather than as a
/// single delay/no-delay check. All wording is deliberately hedged ("may indicate potential...") — EPFO
/// data can support a conclusion of possible payroll/liquidity stress, never a direct claim like "employees
/// are not being paid," which this data cannot prove on its own.</summary>
public static class EpfoRules
{
    public const string WorkforceDeclineCode = "EPFO_WORKFORCE_DECLINE";
    public const string RepeatedPaymentDelayCode = "EPFO_REPEATED_PAYMENT_DELAY";
    public const string DelayWorseningCode = "EPFO_DELAY_WORSENING";
    public const string MissingContributionCode = "EPFO_MISSING_CONTRIBUTION";
    public const string PayrollContractionCode = "EPFO_PAYROLL_CONTRACTION";
    public const string ErraticCompliancePatternCode = "EPFO_ERRATIC_COMPLIANCE_PATTERN";

    private record MonthEntry(DateOnly Month, EpfoContribution Row);

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();

        var entries = ctx.EpfoContributions
            .Select(r => (Row: r, Month: TryParseWageMonth(r.WageMonth)))
            .Where(x => x.Month is not null)
            .Select(x => new MonthEntry(x.Month!.Value, x.Row))
            .OrderBy(x => x.Month)
            .ToList();

        if (entries.Count == 0)
        {
            var reason = ctx.EpfoContributions.Count == 0 ? "No EpfoContribution records available." : "WageMonth values could not be parsed.";
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(WorkforceDeclineCode, reason));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(RepeatedPaymentDelayCode, reason));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(DelayWorseningCode, reason));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(MissingContributionCode, reason));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(PayrollContractionCode, reason));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(ErraticCompliancePatternCode, reason));
            return outcomes;
        }

        var workforceOutcome = EvaluateWorkforceDecline(entries, thresholds);
        outcomes.Add(workforceOutcome);
        outcomes.Add(EvaluateRepeatedPaymentDelay(entries, ctx.AnalysisDate, thresholds));
        outcomes.Add(EvaluateDelayWorsening(entries));
        outcomes.Add(EvaluateMissingContribution(entries));
        outcomes.Add(EvaluatePayrollContraction(entries, workforceOutcome, thresholds));
        outcomes.Add(EvaluateErraticCompliancePattern(entries, ctx.AnalysisDate));

        return outcomes;
    }

    private static RuleEvaluationOutcome EvaluateWorkforceDecline(List<MonthEntry> entries, RuleThresholds t)
    {
        if (entries.Count < 6)
            return RuleEvaluationOutcome.NotEvaluated(WorkforceDeclineCode, "Fewer than six months of parseable EPFO data available.");

        var latest3 = entries.TakeLast(3).Select(e => e.Row.EmployeeCount).Where(c => c.HasValue).Select(c => c!.Value).ToList();
        var prior3 = entries.SkipLast(3).TakeLast(3).Select(e => e.Row.EmployeeCount).Where(c => c.HasValue).Select(c => c!.Value).ToList();
        if (latest3.Count == 0 || prior3.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(WorkforceDeclineCode, "Employee count not available for the comparison windows.");

        var latestAvg = (decimal)latest3.Average();
        var priorAvg = (decimal)prior3.Average();
        if (priorAvg <= 0)
            return RuleEvaluationOutcome.NotEvaluated(WorkforceDeclineCode, "Prior-period average employee count is not positive.");

        var declinePercent = (priorAvg - latestAvg) / priorAvg * 100m;
        if (declinePercent < t.WorkforceDeclineWatchPercent)
            return RuleEvaluationOutcome.NotTriggered();

        var severity = declinePercent >= t.WorkforceDeclineReviewPercent ? FindingSeverity.Review : FindingSeverity.Watch;
        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Epfo, severity, TemporalStatus.Trend,
            WorkforceDeclineCode, "Employee Headcount Declining",
            $"Average employee count declined from {priorAvg:0.#} to {latestAvg:0.#} (latest 3 months vs prior 3 months), a {declinePercent:0.#}% decrease.",
            MetricsJson: JsonSerializer.Serialize(new { priorAverage = priorAvg, latestAverage = latestAvg, declinePercent })));
    }

    private static RuleEvaluationOutcome EvaluateRepeatedPaymentDelay(List<MonthEntry> entries, DateTime analysisDate, RuleThresholds t)
    {
        var cutoff = DateOnly.FromDateTime(analysisDate.AddMonths(-t.RepeatedEpfoDelayLookbackMonths));
        var windowed = entries.Where(e => e.Month >= cutoff).ToList();
        var delays = windowed
            .Where(e => e.Row.PaymentDueDate is not null && e.Row.PaymentDate is not null)
            .Select(e => (e.Month, DelayDays: (e.Row.PaymentDate!.Value.ToDateTime(TimeOnly.MinValue) - e.Row.PaymentDueDate!.Value.ToDateTime(TimeOnly.MinValue)).Days))
            .Where(x => x.DelayDays > 0)
            .ToList();

        if (delays.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        if (delays.Count == 1)
        {
            var single = delays[0];
            return RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Epfo,
                single.DelayDays <= t.IsolatedEpfoDelayMaxDays ? FindingSeverity.Watch : FindingSeverity.Review,
                TemporalStatus.Current,
                RepeatedPaymentDelayCode, "EPFO Payment Delay",
                $"One EPFO payment in the last {t.RepeatedEpfoDelayLookbackMonths} months was delayed by {single.DelayDays} day(s).",
                MetricsJson: JsonSerializer.Serialize(new { delayCount = 1, maxDelayDays = single.DelayDays })));
        }

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Epfo, FindingSeverity.Review, TemporalStatus.Trend,
            RepeatedPaymentDelayCode, "Repeated EPFO Payment Delays",
            $"{delays.Count} EPFO payments were delayed within the last {t.RepeatedEpfoDelayLookbackMonths} months.",
            MetricsJson: JsonSerializer.Serialize(new { delayCount = delays.Count, maxDelayDays = delays.Max(d => d.DelayDays) })));
    }

    private static RuleEvaluationOutcome EvaluateDelayWorsening(List<MonthEntry> entries)
    {
        var delaySeries = entries
            .Where(e => e.Row.PaymentDueDate is not null && e.Row.PaymentDate is not null)
            .Select(e => (e.Month, DelayDays: (e.Row.PaymentDate!.Value.ToDateTime(TimeOnly.MinValue) - e.Row.PaymentDueDate!.Value.ToDateTime(TimeOnly.MinValue)).Days))
            .TakeLast(4)
            .ToList();

        if (delaySeries.Count < 3)
            return RuleEvaluationOutcome.NotEvaluated(DelayWorseningCode, "Fewer than three months of payment-date data available to assess a trend.");

        // Simple monotonic-increase check across the trailing window, not a full regression — good enough
        // to flag "delays are getting worse" without over-modeling noisy month-to-month variation.
        var isWorsening = delaySeries.Zip(delaySeries.Skip(1), (a, b) => b.DelayDays >= a.DelayDays).All(x => x)
            && delaySeries[^1].DelayDays > delaySeries[0].DelayDays;

        return isWorsening
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Epfo, FindingSeverity.Watch, TemporalStatus.Trend,
                DelayWorseningCode, "EPFO Delay Days Increasing",
                $"EPFO payment delay has trended upward across the last {delaySeries.Count} recorded periods ({delaySeries[0].DelayDays} → {delaySeries[^1].DelayDays} days).",
                MetricsJson: JsonSerializer.Serialize(new { delaySeries = delaySeries.Select(d => new { month = d.Month, delayDays = d.DelayDays }) })))
            : RuleEvaluationOutcome.NotTriggered();
    }

    private static RuleEvaluationOutcome EvaluateMissingContribution(List<MonthEntry> entries)
    {
        var lastActiveKnown = IsActiveWorkingStatus(entries[^1].Row.WorkingStatus);
        if (!lastActiveKnown)
            return RuleEvaluationOutcome.NotTriggered();

        var months = entries.Select(e => e.Month).ToHashSet();
        var missing = new List<DateOnly>();
        var cursor = entries[0].Month;
        while (cursor < entries[^1].Month)
        {
            if (!months.Contains(cursor)) missing.Add(cursor);
            cursor = cursor.AddMonths(1);
        }

        return missing.Count == 0
            ? RuleEvaluationOutcome.NotTriggered()
            : RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Epfo, FindingSeverity.Review, TemporalStatus.Current,
                MissingContributionCode, "Missing EPFO Contribution Month(s)",
                $"{missing.Count} expected wage month(s) have no EPFO contribution record while the establishment remains active — stronger than a late-but-present payment.",
                MetricsJson: JsonSerializer.Serialize(new { missingMonths = missing.Select(m => m.ToString("yyyy-MM")) })));
    }

    private static RuleEvaluationOutcome EvaluatePayrollContraction(List<MonthEntry> entries, RuleEvaluationOutcome workforceOutcome, RuleThresholds t)
    {
        if (workforceOutcome.Status != RuleEvaluationStatus.Triggered)
            return RuleEvaluationOutcome.NotTriggered();

        if (entries.Count < 6)
            return RuleEvaluationOutcome.NotEvaluated(PayrollContractionCode, "Fewer than six months of parseable EPFO data available.");

        var latest3 = entries.TakeLast(3).Select(e => e.Row.ContributionAmountCrore).Where(c => c.HasValue).Select(c => c!.Value).ToList();
        var prior3 = entries.SkipLast(3).TakeLast(3).Select(e => e.Row.ContributionAmountCrore).Where(c => c.HasValue).Select(c => c!.Value).ToList();
        if (latest3.Count == 0 || prior3.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(PayrollContractionCode, "Contribution amount not available for the comparison windows.");

        var latestAvg = latest3.Average();
        var priorAvg = prior3.Average();
        if (priorAvg <= 0 || latestAvg >= priorAvg)
            return RuleEvaluationOutcome.NotTriggered();

        // Headcount decline (already Triggered per the caller check above) AND contribution-amount decline
        // together — amount alone can fall for benign reasons (salary-mix, threshold effects), so this
        // rule requires both signals, not contribution decline alone.
        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Epfo, FindingSeverity.Review, TemporalStatus.Trend,
            PayrollContractionCode, "Payroll Contraction",
            "Both employee headcount and EPFO contribution amount are declining together (latest 3 months vs prior 3 months).",
            MetricsJson: JsonSerializer.Serialize(new { priorAvgContribution = priorAvg, latestAvgContribution = latestAvg })));
    }

    private static RuleEvaluationOutcome EvaluateErraticCompliancePattern(List<MonthEntry> entries, DateTime analysisDate)
    {
        var cutoff = DateOnly.FromDateTime(analysisDate.AddMonths(-12));
        var windowed = entries.Where(e => e.Month >= cutoff).OrderBy(e => e.Month).ToList();
        if (windowed.Count < 4)
            return RuleEvaluationOutcome.NotEvaluated(ErraticCompliancePatternCode, "Fewer than four months of EPFO data in the last 12 months.");

        var states = windowed.Select(ClassifyMonth).ToList();
        var transitions = states.Zip(states.Skip(1), (a, b) => a != b).Count(x => x);

        return transitions >= 4
            ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.Epfo, FindingSeverity.Watch, TemporalStatus.Trend,
                ErraticCompliancePatternCode, "Erratic EPFO Compliance Pattern",
                $"EPFO payment status shifted between on-time/delayed/missing {transitions} times across the last {windowed.Count} recorded months.",
                MetricsJson: JsonSerializer.Serialize(new { transitions, monthsObserved = windowed.Count })))
            : RuleEvaluationOutcome.NotTriggered();
    }

    private static string ClassifyMonth(MonthEntry e)
    {
        if (e.Row.PaymentDate is null) return "Missing";
        if (e.Row.PaymentDueDate is null) return "OnTime"; // no due date recorded — can't compute delay, don't penalize
        return e.Row.PaymentDate > e.Row.PaymentDueDate ? "Delayed" : "OnTime";
    }

    private static bool IsActiveWorkingStatus(string? status) =>
        status is not null
        && status.Contains("active", StringComparison.OrdinalIgnoreCase)
        && !status.Contains("inactive", StringComparison.OrdinalIgnoreCase)
        && !status.Contains("closed", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] MonthFormats =
    [
        "MMM-yyyy", "MMMM-yyyy", "MMM yyyy", "MMMM yyyy", "MM-yyyy", "yyyy-MM", "MM/yyyy", "yyyy-MM-dd", "dd-MM-yyyy"
    ];

    /// <summary>WageMonth is Phase 1 free-text, not a normalized date — tries several common real-world
    /// formats and normalizes to the 1st of the month. Unparseable values are excluded (not assumed 0/blank),
    /// consistent with data-sufficiency: an unparseable month contributes to "insufficient data", never a
    /// false trend.</summary>
    private static DateOnly? TryParseWageMonth(string? wageMonth)
    {
        if (string.IsNullOrWhiteSpace(wageMonth)) return null;
        var text = wageMonth.Trim();
        foreach (var format in MonthFormats)
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return new DateOnly(parsed.Year, parsed.Month, 1);
        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var general)
            ? new DateOnly(general.Year, general.Month, 1)
            : null;
    }
}
