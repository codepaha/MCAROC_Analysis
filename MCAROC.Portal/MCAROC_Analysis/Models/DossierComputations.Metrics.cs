using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Models;

/// <summary>The derived-metrics layer — every computed BFSI figure the dossier and the portal show,
/// built once here as pure functions over the assembled <see cref="DossierModel"/>. Contract:
/// <c>docs/analytics-catalogue.json</c>. Wave-4 issues fill the sections in:
/// D1 charges (#56) · D2 financial trend/leverage (#57) · D3 GST (#58) · D4 shareholding (#59) ·
/// D5 legal (#60) · D6 directors (#61) · D7 EPFO (#62) · D8 peers (#63) · D9 cost/forex (#64) ·
/// D10 RPT (#65) · D11 credit ratings (#66).
///
/// Guardrails: not a score (never combine metrics into an index); every metric returns a
/// <see cref="MetricResult"/> with its inputs, period and — when it cannot be computed — an
/// insufficiency reason instead of a fake 0; the whole layer renders in ALL dossier variants
/// including SourceRecord (no-AI).</summary>
public static partial class DossierComputations
{
    /// <summary>All metric groups for a dossier, in render order. Empty until the D-wave issues add
    /// sections — the plumbing (this method, the Snapshot "Key Indicators" block, the portal
    /// <c>_KeyIndicators</c> partial) ships first in D0 so every later section is regression-guarded
    /// from the moment it lands.</summary>
    public static IReadOnlyList<MetricGroup> BuildMetricGroups(Dossier.DossierModel model)
    {
        var groups = new List<MetricGroup>();

        // D1..D11 append their MetricGroup here, e.g.:
        groups.Add(GstComplianceMetrics(model));
        // Each builder returns a MetricGroup whose Metrics are MetricResult.Ok / .Insufficient.

        return groups.Where(g => g.HasAny).ToList();
    }

    /// <summary>D3 (#58) — GST compliance analytics (§G). Pure compute over <see cref="DossierCompliance.Gst"/>.</summary>
    public static MetricGroup GstComplianceMetrics(Dossier.DossierModel model)
    {
        var list = new List<MetricResult>();
        var gstRegs = model.Compliance.Gst;
        var asOf = model.Cover.McaDataAsOf ?? new DateTime(model.Cover.ReportDate.Year, model.Cover.ReportDate.Month, model.Cover.ReportDate.Day, 0, 0, 0, DateTimeKind.Utc);
        var asOfStr = $"as of {asOf:d MMM yyyy}";

        // G1: Active GSTIN count
        if (gstRegs.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Active GSTIN count", MetricUnit.Count,
                "No GST registration records on file", "GstRegistration.Status"));
        }
        else
        {
            var activeCount = gstRegs.Count(g => IsActiveGstStatus(g.Status));
            list.Add(MetricResult.Ok("Active GSTIN count", activeCount, MetricUnit.Count,
                asOfStr, "GstRegistration.Status"));
        }

        // G2: States of operation
        if (gstRegs.Count == 0)
        {
            list.Add(MetricResult.Insufficient("States of operation", MetricUnit.Count,
                "No GST registration records on file", "GstRegistration.State"));
        }
        else
        {
            var statesCount = gstRegs
                .Select(g => g.State?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            list.Add(MetricResult.Ok("States of operation", statesCount, MetricUnit.Count,
                asOfStr, "GstRegistration.State"));
        }

        // Collect all filings across registrations
        var filings = gstRegs.SelectMany(g => g.Filings).ToList();

        // G3 & G4: Filing on-time rate and Late filing count
        // An authoritative filing assessment requires either DelayDays or both FilingDate and DueDate.
        // Filings with bare status and no dates are INDETERMINATE: excluded from denominator, counted separately.
        var onTimeCount = 0;
        var lateCount = 0;
        var indeterminateCount = 0;

        foreach (var f in filings)
        {
            if (f.DelayDays is { } delay)
            {
                if (delay <= 0) onTimeCount++;
                else lateCount++;
            }
            else if (f.FilingDate is { } filed && f.DueDate is { } due)
            {
                var calcDelay = filed.DayNumber - due.DayNumber;
                if (calcDelay <= 0) onTimeCount++;
                else lateCount++;
            }
            else
            {
                indeterminateCount++;
            }
        }

        var assessedCount = onTimeCount + lateCount;

        // G3: GST filing on-time rate
        if (assessedCount == 0)
        {
            list.Add(MetricResult.Insufficient("GST filing on-time rate", MetricUnit.Percent,
                "No GST filings with deterministic filing and due dates",
                "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.FilingStatus", "GstFiling.ReturnType"));
        }
        else
        {
            var rate = Math.Round((decimal)onTimeCount / assessedCount * 100m, 1);
            var periodStr = indeterminateCount > 0
                ? $"{assessedCount} assessed ({indeterminateCount} indeterminate)"
                : $"{assessedCount} assessed";
            list.Add(MetricResult.Ok("GST filing on-time rate", rate, MetricUnit.Percent,
                periodStr,
                "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.FilingStatus", "GstFiling.ReturnType"));
        }

        // G4: Late filing count + periods
        if (assessedCount == 0)
        {
            list.Add(MetricResult.Insufficient("Late filing count", MetricUnit.Count,
                "No GST filings with deterministic filing and due dates",
                "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.TaxPeriod", "GstFiling.ReturnType"));
        }
        else
        {
            var periodStr = $"{lateCount} late of {assessedCount} assessed";
            list.Add(MetricResult.Ok("Late filing count", lateCount, MetricUnit.Count,
                periodStr,
                "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.TaxPeriod", "GstFiling.ReturnType"));
        }

        // G5: GSTR-1 vs GSTR-3B filing lag
        // Mean absolute day difference between GSTR-3B and GSTR-1 filings for the same tax period and GSTIN.
        static string NormReturn(string r) => r.Replace("-", "").Trim().ToUpperInvariant();
        var gstr1 = filings.Where(f => NormReturn(f.ReturnType) == "GSTR1" && f.FilingDate is not null && !string.IsNullOrWhiteSpace(f.TaxPeriod)).ToList();
        var gstr3b = filings.Where(f => NormReturn(f.ReturnType) == "GSTR3B" && f.FilingDate is not null && !string.IsNullOrWhiteSpace(f.TaxPeriod)).ToList();

        var gstr1Lookup = gstr1
            .GroupBy(f => (f.Gstin, f.TaxPeriod!.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.First());

        var lags = new List<int>();
        foreach (var f3 in gstr3b)
        {
            var key = (f3.Gstin, f3.TaxPeriod!.Trim().ToUpperInvariant());
            if (gstr1Lookup.TryGetValue(key, out var f1))
            {
                lags.Add(Math.Abs(f3.FilingDate!.Value.DayNumber - f1.FilingDate!.Value.DayNumber));
            }
        }

        if (lags.Count == 0)
        {
            list.Add(MetricResult.Insufficient("GSTR-1 vs GSTR-3B filing lag", MetricUnit.Days,
                "GSTR-1 and GSTR-3B do not co-occur with filing dates for any tax period",
                "GstFiling.ReturnType", "GstFiling.TaxPeriod", "GstFiling.FilingDate"));
        }
        else
        {
            var meanLag = Math.Round((decimal)lags.Average(), 1);
            list.Add(MetricResult.Ok("GSTR-1 vs GSTR-3B filing lag", meanLag, MetricUnit.Days,
                $"{lags.Count} matched periods",
                "GstFiling.ReturnType", "GstFiling.TaxPeriod", "GstFiling.FilingDate"));
        }

        // G6: GST registration flags present
        if (gstRegs.Count == 0)
        {
            list.Add(MetricResult.Insufficient("GST registration flags present", MetricUnit.Count,
                "No GST registration records on file", "GstRegistration.Flags"));
        }
        else
        {
            var flagsPresent = gstRegs.Any(r => !string.IsNullOrWhiteSpace(r.Flags) && r.Flags.Trim() != "-");
            list.Add(MetricResult.Ok("GST registration flags present", flagsPresent ? 1m : 0m, MetricUnit.Count,
                asOfStr, "GstRegistration.Flags"));
        }

        return new MetricGroup("GST compliance", list);
    }

    private static bool IsActiveGstStatus(string? status) =>
        status is not null
        && status.Contains("active", StringComparison.OrdinalIgnoreCase)
        && !status.Contains("inactive", StringComparison.OrdinalIgnoreCase);
}
