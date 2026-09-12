using System.Globalization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Excel;

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
        var groups = new List<MetricGroup>
        {
            ChargeRegisterMetrics(model),
            GstComplianceMetrics(model),
            LitigationMetrics(model),
            DirectorsMetrics(model),
            FinancialTrendMetrics(model),
            ShareholdingMetrics(model),
            EpfoMetrics(model),
            PeerComparisonMetrics(model),
            CapitalReconciliationMetrics(model),
            RelatedPartyTransactionMetrics(model)
        };

        return groups.Where(g => g.HasAny).ToList();
    }

    /// <summary>Section B — Charge register analytics (Issue #56 / D1, docs/analytics-catalogue.json §B).
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup ChargeRegisterMetrics(Dossier.DossierModel model)
    {
        var charges = model.Charges;
        var open = charges.Open;
        var satisfied = charges.Satisfied;
        var all = charges.All;

        var asOfDate = model.Cover.McaDataAsOf is { } dt
            ? DateOnly.FromDateTime(dt)
            : (all.SelectMany(c => c.Events).Select(e => e.EventDate).Where(d => d is not null).Max()
               ?? DateOnly.FromDateTime(model.Cover.ReportDate));
        var asOfStr = $"as at {asOfDate:d MMM yyyy}";

        var list = new List<MetricResult>();
        var anyOpenMissingAmount = open.Any(c => c.CurrentAmount is null);
        var totalOpen = anyOpenMissingAmount ? (decimal?)null : charges.TotalOpenAmount;

        // B1: Total open charge amount
        if (all.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Total open charge amount", MetricUnit.Crore,
                "No charge records on file", "RocCharge.CurrentAmount"));
        }
        else if (anyOpenMissingAmount)
        {
            var missingCount = open.Count(c => c.CurrentAmount is null);
            list.Add(MetricResult.Insufficient("Total open charge amount", MetricUnit.Crore,
                $"Reconciliation incomplete: {missingCount} of {open.Count} open charges missing CurrentAmount",
                "RocCharge.CurrentAmount"));
        }
        else
        {
            list.Add(MetricResult.Ok("Total open charge amount", totalOpen!.Value,
                MetricUnit.Crore, asOfStr, "RocCharge.CurrentAmount"));
        }

        // B2: Total satisfied charge amount
        if (all.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Total satisfied charge amount", MetricUnit.Crore,
                "No charge records on file", "RocCharge.CurrentAmount"));
        }
        else if (satisfied.Count == 0)
        {
            list.Add(MetricResult.Ok("Total satisfied charge amount", 0m,
                MetricUnit.Crore, $"{asOfStr} (0 satisfied charges)", "RocCharge.CurrentAmount"));
        }
        else if (satisfied.Any(c => c.CurrentAmount is null))
        {
            var missingCount = satisfied.Count(c => c.CurrentAmount is null);
            list.Add(MetricResult.Insufficient("Total satisfied charge amount", MetricUnit.Crore,
                $"Reconciliation incomplete: {missingCount} of {satisfied.Count} satisfied charges missing CurrentAmount",
                "RocCharge.CurrentAmount"));
        }
        else
        {
            var totalSatAmount = satisfied.Sum(c => c.CurrentAmount!.Value);
            list.Add(MetricResult.Ok("Total satisfied charge amount", totalSatAmount,
                MetricUnit.Crore, asOfStr, "RocCharge.CurrentAmount"));
        }

        // B3: Charge satisfaction rate
        if (all.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Charge satisfaction rate", MetricUnit.Percent,
                "No charge records on file", "RocCharge.ChargeStatus", "RocCharge.SatisfactionDate"));
        }
        else
        {
            var rate = Math.Round((decimal)satisfied.Count / all.Count * 100m, 1);
            list.Add(MetricResult.Ok("Charge satisfaction rate", rate,
                MetricUnit.Percent, asOfStr, "RocCharge.ChargeStatus", "RocCharge.SatisfactionDate"));
        }

        // B4: Top-3 lender concentration
        if (anyOpenMissingAmount)
        {
            list.Add(MetricResult.Insufficient("Top-3 lender concentration", MetricUnit.Percent,
                "Open charge amounts incomplete; lender share undefined", "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }
        else if (totalOpen == 0m)
        {
            list.Add(MetricResult.Insufficient("Top-3 lender concentration", MetricUnit.Percent,
                "Total open charge amount is zero", "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }
        else
        {
            var lconc = charges.LenderConcentration;
            var top3Amt = lconc.Take(3).Sum(c => c.RegisteredAmount);
            var top3Pct = Math.Round(top3Amt / totalOpen!.Value * 100m, 1);
            var periodNote = lconc.Count < 3 ? $"{asOfStr} ({lconc.Count} holders)" : asOfStr;
            list.Add(MetricResult.Ok("Top-3 lender concentration", top3Pct,
                MetricUnit.Percent, periodNote, "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }

        // B4b: Lender concentration (HHI)
        if (anyOpenMissingAmount)
        {
            list.Add(MetricResult.Insufficient("Lender concentration (HHI)", MetricUnit.Ratio,
                "Open charge amounts incomplete; lender share undefined", "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }
        else if (totalOpen == 0m)
        {
            list.Add(MetricResult.Insufficient("Lender concentration (HHI)", MetricUnit.Ratio,
                "Total open charge amount is zero", "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }
        else
        {
            decimal hhi = 0m;
            foreach (var r in charges.LenderConcentration)
            {
                var share = r.RegisteredAmount / totalOpen!.Value;
                hhi += share * share;
            }
            list.Add(MetricResult.Ok("Lender concentration (HHI)", Math.Round(hhi, 4),
                MetricUnit.Ratio, asOfStr, "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }

        // B5: Asset-type breakdown of open charges (unclassified bucket, not dropped)
        if (open.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Unclassified open charge amount", MetricUnit.Crore,
                "No open charges on record", "DossierComputations.SecurityTypeLabels", "RocCharge.CurrentAmount"));
        }
        else
        {
            var unclassified = open.Where(c => SecurityTypeLabels(c).Count == 0).ToList();
            if (unclassified.Any(c => c.CurrentAmount is null))
            {
                var missingCount = unclassified.Count(c => c.CurrentAmount is null);
                list.Add(MetricResult.Insufficient("Unclassified open charge amount", MetricUnit.Crore,
                    $"Reconciliation incomplete: {missingCount} of {unclassified.Count} unclassified open charges missing CurrentAmount",
                    "DossierComputations.SecurityTypeLabels", "RocCharge.CurrentAmount"));
            }
            else
            {
                var unclassifiedAmt = unclassified.Sum(c => c.CurrentAmount!.Value);
                list.Add(MetricResult.Ok("Unclassified open charge amount", unclassifiedAmt,
                    MetricUnit.Crore, asOfStr, "DossierComputations.SecurityTypeLabels", "RocCharge.CurrentAmount"));
            }
        }

        // B6: Oldest open charge age
        var datedOpen = open.Where(c => c.CreationDate is not null).ToList();
        if (datedOpen.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Oldest open charge age", MetricUnit.Days,
                "No open charges with dated creation event", "RocCharge.CreationDate", "DossierCover.McaDataAsOf"));
        }
        else
        {
            var minCreation = datedOpen.Min(c => c.CreationDate!.Value);
            var ageDays = Math.Max(0, asOfDate.DayNumber - minCreation.DayNumber);
            list.Add(MetricResult.Ok("Oldest open charge age", (decimal)ageDays,
                MetricUnit.Days, asOfStr, "RocCharge.CreationDate", "DossierCover.McaDataAsOf"));
        }

        // B7: Charges created in last 12 / 24 months (count + amount)
        if (all.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Charges created in last 12 months", MetricUnit.Count,
                "No charge records on file", "RocChargeEvent.EventDate"));
            list.Add(MetricResult.Insufficient("Amount created in last 12 months", MetricUnit.Crore,
                "No charge records on file", "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
            list.Add(MetricResult.Insufficient("Charges created in last 24 months", MetricUnit.Count,
                "No charge records on file", "RocChargeEvent.EventDate"));
            list.Add(MetricResult.Insufficient("Amount created in last 24 months", MetricUnit.Crore,
                "No charge records on file", "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
        }
        else
        {
            var cutoff12 = asOfDate.AddYears(-1);
            var cutoff24 = asOfDate.AddYears(-2);

            static DateOnly? GetCreationDate(RocCharge c) =>
                c.CreationDate ?? c.Events.FirstOrDefault(e => e.EventType == ChargeEventType.Creation && e.EventDate is not null)?.EventDate;

            var c12 = all.Where(c =>
            {
                var d = GetCreationDate(c);
                return d is not null && d.Value >= cutoff12 && d.Value <= asOfDate;
            }).ToList();

            var c24 = all.Where(c =>
            {
                var d = GetCreationDate(c);
                return d is not null && d.Value >= cutoff24 && d.Value <= asOfDate;
            }).ToList();

            if (c12.Count == 0)
            {
                var periodNote = $"trailing 12 months to {asOfDate:d MMM yyyy} (0 dated creation events)";
                list.Add(MetricResult.Ok("Charges created in last 12 months", 0m,
                    MetricUnit.Count, periodNote, "RocChargeEvent.EventDate"));
                list.Add(MetricResult.Ok("Amount created in last 12 months", 0m,
                    MetricUnit.Crore, periodNote, "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
            }
            else
            {
                list.Add(MetricResult.Ok("Charges created in last 12 months", c12.Count,
                    MetricUnit.Count, $"trailing 12 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate"));

                if (c12.Any(c => c.CurrentAmount is null))
                {
                    var missingCount = c12.Count(c => c.CurrentAmount is null);
                    list.Add(MetricResult.Insufficient("Amount created in last 12 months", MetricUnit.Crore,
                        $"Reconciliation incomplete: {missingCount} of {c12.Count} created charges missing CurrentAmount",
                        "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
                }
                else
                {
                    var amt12 = c12.Sum(c => c.CurrentAmount!.Value);
                    list.Add(MetricResult.Ok("Amount created in last 12 months", amt12,
                        MetricUnit.Crore, $"trailing 12 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
                }
            }

            if (c24.Count == 0)
            {
                var periodNote = $"trailing 24 months to {asOfDate:d MMM yyyy} (0 dated creation events)";
                list.Add(MetricResult.Ok("Charges created in last 24 months", 0m,
                    MetricUnit.Count, periodNote, "RocChargeEvent.EventDate"));
                list.Add(MetricResult.Ok("Amount created in last 24 months", 0m,
                    MetricUnit.Crore, periodNote, "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
            }
            else
            {
                list.Add(MetricResult.Ok("Charges created in last 24 months", c24.Count,
                    MetricUnit.Count, $"trailing 24 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate"));

                if (c24.Any(c => c.CurrentAmount is null))
                {
                    var missingCount = c24.Count(c => c.CurrentAmount is null);
                    list.Add(MetricResult.Insufficient("Amount created in last 24 months", MetricUnit.Crore,
                        $"Reconciliation incomplete: {missingCount} of {c24.Count} created charges missing CurrentAmount",
                        "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
                }
                else
                {
                    var amt24 = c24.Sum(c => c.CurrentAmount!.Value);
                    list.Add(MetricResult.Ok("Amount created in last 24 months", amt24,
                        MetricUnit.Crore, $"trailing 24 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate", "RocCharge.CurrentAmount"));
                }
            }
        }

        // B8: Joint / consortium charge count
        var jointOrConsortium = open.Count(c => c.Events.Any(e => e.JointHolding == true || e.ConsortiumHolding == true));
        list.Add(MetricResult.Ok("Joint / consortium charge count", jointOrConsortium,
            MetricUnit.Count, asOfStr, "RocChargeEvent.JointHolding", "RocChargeEvent.ConsortiumHolding"));

        // B9: Charge-to-paid-up-capital ratio
        if (anyOpenMissingAmount)
        {
            list.Add(MetricResult.Insufficient("Charge-to-paid-up-capital ratio", MetricUnit.Times,
                "Open charge amounts incomplete; ratio undefined", "RocCharge.CurrentAmount", "CompanyProfile.PaidUpCapital"));
        }
        else if (model.Corporate.PaidUpCapital is { } puc && puc > 0m)
        {
            var ratio = Math.Round(totalOpen!.Value / puc, 2);
            list.Add(MetricResult.Ok("Charge-to-paid-up-capital ratio", ratio,
                MetricUnit.Times, asOfStr, "RocCharge.CurrentAmount", "CompanyProfile.PaidUpCapital"));
        }
        else
        {
            list.Add(MetricResult.Insufficient("Charge-to-paid-up-capital ratio", MetricUnit.Times,
                "Paid-up capital not reported or zero", "RocCharge.CurrentAmount", "CompanyProfile.PaidUpCapital"));
        }

        // B10: Open charges vs balance-sheet borrowings
        if (anyOpenMissingAmount)
        {
            list.Add(MetricResult.Insufficient("Open charges vs balance-sheet borrowings", MetricUnit.Times,
                "Open charge amounts incomplete; ratio undefined",
                "RocCharge.CurrentAmount", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings"));
        }
        else
        {
            var latestFy = model.Financials.Latest;
            if (latestFy is null || latestFy.LongTermBorrowings is null || latestFy.ShortTermBorrowings is null)
            {
                list.Add(MetricResult.Insufficient("Open charges vs balance-sheet borrowings", MetricUnit.Times,
                    "Balance-sheet borrowings not available (or missing long/short-term components)",
                    "RocCharge.CurrentAmount", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings"));
            }
            else
            {
                var debt = latestFy.LongTermBorrowings.Value + latestFy.ShortTermBorrowings.Value;
                if (debt == 0m)
                {
                    list.Add(MetricResult.Insufficient("Open charges vs balance-sheet borrowings", MetricUnit.Times,
                        "Balance-sheet borrowings is zero; ratio undefined",
                        "RocCharge.CurrentAmount", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings"));
                }
                else
                {
                    var ratio = Math.Round(totalOpen!.Value / debt, 2);
                    list.Add(MetricResult.Ok("Open charges vs balance-sheet borrowings", ratio,
                        MetricUnit.Times, $"FY{latestFy.FinancialYear}",
                        "RocCharge.CurrentAmount", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings"));
                }
            }
        }

        // B11: Charge filing-lag median + buckets + negative-lag anomalies
        // Catalogue: report count + median + buckets 0-7 / 8-30 / 31-90 / >90 (MCA statutory = 30 days)
        // A negative lag (filing precedes creation) is isolated as a data-quality anomaly.
        var datedCreations = all.SelectMany(c => c.Events)
            .Where(e => e.EventType == ChargeEventType.Creation && e.EventDate is not null && e.FilingDate is not null)
            .ToList();
        var negLagEvents = datedCreations.Where(e => e.FilingDate!.Value.DayNumber < e.EventDate!.Value.DayNumber).ToList();
        var nonNegLags = datedCreations
            .Where(e => e.FilingDate!.Value.DayNumber >= e.EventDate!.Value.DayNumber)
            .Select(e => e.FilingDate!.Value.DayNumber - e.EventDate!.Value.DayNumber)
            .OrderBy(d => d)
            .ToList();

        if (nonNegLags.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Median charge filing lag", MetricUnit.Days,
                "No Creation events with valid non-negative filing dates",
                "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
            list.Add(MetricResult.Insufficient("Charge filing lag 0-7 days", MetricUnit.Count,
                "No Creation events with valid non-negative filing dates",
                "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
            list.Add(MetricResult.Insufficient("Charge filing lag 8-30 days", MetricUnit.Count,
                "No Creation events with valid non-negative filing dates",
                "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
            list.Add(MetricResult.Insufficient("Charge filing lag 31-90 days", MetricUnit.Count,
                "No Creation events with valid non-negative filing dates",
                "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
            list.Add(MetricResult.Insufficient("Charge filing lag >90 days", MetricUnit.Count,
                "No Creation events with valid non-negative filing dates",
                "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
        }
        else
        {
            // Median: middle element for odd counts, average of the two middle elements for even counts.
            decimal median = nonNegLags.Count % 2 == 1
                ? nonNegLags[nonNegLags.Count / 2]
                : (nonNegLags[nonNegLags.Count / 2 - 1] + nonNegLags[nonNegLags.Count / 2]) / 2m;
            list.Add(MetricResult.Ok("Median charge filing lag", median,
                MetricUnit.Days, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));

            var b0_7 = nonNegLags.Count(d => d <= 7);
            var b8_30 = nonNegLags.Count(d => d >= 8 && d <= 30);
            var b31_90 = nonNegLags.Count(d => d >= 31 && d <= 90);
            var bGt90 = nonNegLags.Count(d => d > 90);

            list.Add(MetricResult.Ok("Charge filing lag 0-7 days", b0_7,
                MetricUnit.Count, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
            list.Add(MetricResult.Ok("Charge filing lag 8-30 days", b8_30,
                MetricUnit.Count, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
            list.Add(MetricResult.Ok("Charge filing lag 31-90 days", b31_90,
                MetricUnit.Count, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
            list.Add(MetricResult.Ok("Charge filing lag >90 days", bGt90,
                MetricUnit.Count, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
        }

        list.Add(MetricResult.Ok("Negative filing-lag anomalies", negLagEvents.Count,
            MetricUnit.Count, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));

        return new MetricGroup("Charge register", list);
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

        // G3: GST filing on-time rate — broken down by ReturnType per catalogue §G
        {
            // Bucket each assessed filing by its normalised ReturnType
            var byType = new Dictionary<string, (int OnTime, int Late, int Indet)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in filings)
            {
                var rt = string.IsNullOrWhiteSpace(f.ReturnType) ? "UNKNOWN" : f.ReturnType.Trim().ToUpperInvariant();
                byType.TryGetValue(rt, out var counts);
                if (f.DelayDays is { } d)
                {
                    if (d <= 0) byType[rt] = (counts.OnTime + 1, counts.Late, counts.Indet);
                    else        byType[rt] = (counts.OnTime, counts.Late + 1, counts.Indet);
                }
                else if (f.FilingDate is { } filed2 && f.DueDate is { } due2)
                {
                    var calcDelay = filed2.DayNumber - due2.DayNumber;
                    if (calcDelay <= 0) byType[rt] = (counts.OnTime + 1, counts.Late, counts.Indet);
                    else                byType[rt] = (counts.OnTime, counts.Late + 1, counts.Indet);
                }
                else
                {
                    byType[rt] = (counts.OnTime, counts.Late, counts.Indet + 1);
                }
            }

            if (byType.Count == 0)
            {
                list.Add(MetricResult.Insufficient("GST filing on-time rate", MetricUnit.Percent,
                    "No GST filings with deterministic filing and due dates",
                    "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.FilingStatus", "GstFiling.ReturnType"));
            }
            else
            {
                foreach (var (rt, c) in byType.OrderBy(kv => kv.Key))
                {
                    var typeAssessed = c.OnTime + c.Late;
                    if (typeAssessed > 0)
                    {
                        var rate = Math.Round((decimal)c.OnTime / typeAssessed * 100m, 1);
                        var periodStr = c.Indet > 0
                            ? $"{typeAssessed} assessed ({c.Indet} indeterminate)"
                            : $"{typeAssessed} assessed";
                        list.Add(MetricResult.Ok($"GST filing on-time rate ({rt})", rate, MetricUnit.Percent,
                            periodStr,
                            "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.FilingStatus", "GstFiling.ReturnType"));
                    }
                    else
                    {
                        list.Add(MetricResult.Insufficient($"GST filing on-time rate ({rt})", MetricUnit.Percent,
                            $"All {c.Indet} filings indeterminate (missing filing or due dates)",
                            "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.FilingStatus", "GstFiling.ReturnType"));
                    }
                }
            }
        }

        // G4: Late filing count + evidence — catalogue requires listing late (TaxPeriod, ReturnType) pairs
        if (assessedCount == 0)
        {
            list.Add(MetricResult.Insufficient("Late filing count", MetricUnit.Count,
                "No GST filings with deterministic filing and due dates",
                "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.TaxPeriod", "GstFiling.ReturnType"));
        }
        else
        {
            // Collect late evidence: (TaxPeriod, ReturnType) for every late filing, ordered for reproducibility
            var lateFilings = filings.Where(f =>
            {
                if (f.DelayDays is { } d) return d > 0;
                if (f.FilingDate is { } fd && f.DueDate is { } dd) return fd.DayNumber - dd.DayNumber > 0;
                return false;
            })
            .OrderBy(f => f.TaxPeriod, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.ReturnType, StringComparer.OrdinalIgnoreCase)
            .ToList();

            var evidencePairs = lateFilings
                .Select(f => $"{f.TaxPeriod}/{f.ReturnType?.Trim()}");
            var evidenceStr = string.Join(", ", evidencePairs);

            var periodStr = $"{lateCount} late of {assessedCount} assessed — periods: {evidenceStr}";
            list.Add(MetricResult.Ok("Late filing count", lateCount, MetricUnit.Count,
                periodStr,
                "GstFiling.DelayDays", "GstFiling.FilingDate", "GstFiling.DueDate", "GstFiling.TaxPeriod", "GstFiling.ReturnType"));
        }

        // G5: GSTR-1 vs GSTR-3B filing lag
        // Mean day difference (FilingDate(GSTR3B) - FilingDate(GSTR1)) over non-negative lags.
        // Safety gate: A date-difference metric is never abs()-ed; negative lags (GSTR-3B filed before GSTR-1)
        // are isolated as data-quality anomalies and reported separately.
        // When multiple rows share the same (Gstin, TaxPeriod) key for either GSTR-1 or GSTR-3B, we pick the one
        // with the latest FilingDate so the result is deterministic regardless of source workbook row ordering.
        static string NormReturn(string r) => r.Replace("-", "").Trim().ToUpperInvariant();
        var gstr1 = filings.Where(f => NormReturn(f.ReturnType) == "GSTR1" && f.FilingDate is not null && !string.IsNullOrWhiteSpace(f.TaxPeriod)).ToList();
        var gstr3b = filings.Where(f => NormReturn(f.ReturnType) == "GSTR3B" && f.FilingDate is not null && !string.IsNullOrWhiteSpace(f.TaxPeriod)).ToList();

        var gstr1Groups = gstr1
            .GroupBy(f => (f.Gstin, f.TaxPeriod!.Trim().ToUpperInvariant()))
            .ToList();
        var gstr1Lookup = gstr1Groups
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.FilingDate!.Value).First());

        var duplicateGstr1GroupsCount = gstr1Groups.Count(g => g.Count() > 1);
        var duplicateGstr1RowsCount = gstr1.Count - gstr1Groups.Count;

        var gstr3bGroups = gstr3b
            .GroupBy(f => (f.Gstin, f.TaxPeriod!.Trim().ToUpperInvariant()))
            .ToList();
        var gstr3bLookup = gstr3bGroups
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.FilingDate!.Value).First());

        var duplicateGstr3bGroupsCount = gstr3bGroups.Count(g => g.Count() > 1);
        var duplicateGstr3bRowsCount = gstr3b.Count - gstr3bGroups.Count;

        var nonNegLags = new List<int>();
        var negLagCount = 0;
        foreach (var (key, f3) in gstr3bLookup)
        {
            if (gstr1Lookup.TryGetValue(key, out var f1))
            {
                var lag = f3.FilingDate!.Value.DayNumber - f1.FilingDate!.Value.DayNumber;
                if (lag >= 0)
                {
                    nonNegLags.Add(lag);
                }
                else
                {
                    negLagCount++;
                }
            }
        }

        if (nonNegLags.Count == 0)
        {
            list.Add(MetricResult.Insufficient("GSTR-1 vs GSTR-3B filing lag", MetricUnit.Days,
                "No non-negative filing lags between GSTR-1 and GSTR-3B for any matched tax period",
                "GstFiling.ReturnType", "GstFiling.TaxPeriod", "GstFiling.FilingDate"));
        }
        else
        {
            var meanLag = Math.Round((decimal)nonNegLags.Average(), 1);
            var dupe1Str = duplicateGstr1GroupsCount > 0
                ? $"{duplicateGstr1GroupsCount} duplicate GSTR-1 {(duplicateGstr1GroupsCount == 1 ? "group" : "groups")}, {duplicateGstr1RowsCount} duplicate {(duplicateGstr1RowsCount == 1 ? "row" : "rows")}"
                : "0 duplicate GSTR-1 groups";
            var dupe3bStr = duplicateGstr3bGroupsCount > 0
                ? $"{duplicateGstr3bGroupsCount} duplicate GSTR-3B {(duplicateGstr3bGroupsCount == 1 ? "group" : "groups")}, {duplicateGstr3bRowsCount} duplicate {(duplicateGstr3bRowsCount == 1 ? "row" : "rows")}"
                : "0 duplicate GSTR-3B groups";

            var dupeDisclosure = $" ({dupe1Str}, {dupe3bStr}; selected latest FilingDate)";
            var periodStr = $"{nonNegLags.Count} matched periods{dupeDisclosure}";
            list.Add(MetricResult.Ok("GSTR-1 vs GSTR-3B filing lag", meanLag, MetricUnit.Days,
                periodStr,
                "GstFiling.ReturnType", "GstFiling.TaxPeriod", "GstFiling.FilingDate"));
        }

        if (gstRegs.Count == 0)
        {
            list.Add(MetricResult.Insufficient("GSTR-3B filed before GSTR-1 anomalies", MetricUnit.Count,
                "No GST registration records on file",
                "GstFiling.ReturnType", "GstFiling.TaxPeriod", "GstFiling.FilingDate"));
        }
        else
        {
            list.Add(MetricResult.Ok("GSTR-3B filed before GSTR-1 anomalies", negLagCount, MetricUnit.Count,
                asOfStr,
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

    /// <summary>Court type normalisation according to precedence rules:
    /// 1. NCLT: "NCLT" or "NATIONAL COMPANY LAW"
    /// 2. DRT: "DEBT RECOVERY", "DEBTS RECOVERY", "DRT", or "DRAT"
    /// 3. High Court: "HIGH COURT"
    /// 4. Consumer Court: "CONSUMER" (evaluated before District Court to prevent "District Consumer Forum" false matches)
    /// 5. District Court: "DISTRICT", "CITY CIVIL", or "SESSIONS"
    /// 6. Fallback: "Other"
    /// </summary>
    public static string NormalizeCourtType(string? court)
    {
        if (string.IsNullOrWhiteSpace(court)) return "Other";
        var c = court.Trim();

        if (c.Contains("NCLT", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("NATIONAL COMPANY LAW", StringComparison.OrdinalIgnoreCase))
            return "NCLT";

        if (c.Contains("DEBT RECOVERY", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("DEBTS RECOVERY", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("DRT", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("DRAT", StringComparison.OrdinalIgnoreCase))
            return "DRT";

        if (c.Contains("HIGH COURT", StringComparison.OrdinalIgnoreCase))
            return "High Court";

        if (c.Contains("CONSUMER", StringComparison.OrdinalIgnoreCase))
            return "Consumer Court";

        if (c.Contains("DISTRICT", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("CITY CIVIL", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("SESSIONS", StringComparison.OrdinalIgnoreCase))
            return "District Court";

        return "Other";
    }

    /// <summary>D5 formula: CaseCategory ~ 'Insolvency' OR Court ~ NCLT (over Confirmed + Probable cases).</summary>
    public static bool IsNcltOrInsolvency(Litigation l) =>
        NormalizeCourtType(l.Court) == "NCLT" ||
        (l.CaseCategory ?? "").Contains("insolv", StringComparison.OrdinalIgnoreCase);

    /// <summary>D6 formula: Court ~ 'DEBT RECOVERY TRIBUNAL' (over Confirmed + Probable cases).</summary>
    public static bool IsDrt(Litigation l)
    {
        if (l.Court is null) return false;
        var c = l.Court;
        return c.Contains("DEBT RECOVERY", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("DEBTS RECOVERY", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("DRT", StringComparison.OrdinalIgnoreCase) ||
               c.Contains("DRAT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Section D — Legal history analytics (Issue #60 / D5, docs/analytics-catalogue.json §D).
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup LitigationMetrics(Dossier.DossierModel model)
    {
        var list = new List<MetricResult>();
        var lit = model.Litigation;
        var all = lit.All;

        var asOfDate = model.Cover.McaDataAsOf is { } dt
            ? DateOnly.FromDateTime(dt)
            : DateOnly.FromDateTime(model.Cover.ReportDate);
        var asOfStr = $"as at {asOfDate:d MMM yyyy}";

        var confirmed = all.Where(l => l.MatchStatus == LitigationMatchStatus.Confirmed).ToList();
        var relevant = all.Where(l => l.MatchStatus is LitigationMatchStatus.Confirmed or LitigationMatchStatus.Probable).ToList();

        // ── D1: Confirmed pending case count ──
        // Catalogue: degenerate is "0 with note"
        if (all.Count == 0 || confirmed.Count == 0)
        {
            list.Add(MetricResult.Ok("Confirmed pending case count", 0m, MetricUnit.Count,
                $"{asOfStr} (0 confirmed cases on record)", "Litigation.MatchStatus", "Litigation.CaseStatus"));
        }
        else
        {
            var pendingConfirmed = confirmed.Count(IsPendingLitigation);
            var periodStr = pendingConfirmed == 0
                ? $"{asOfStr} (0 pending of {confirmed.Count} confirmed cases)"
                : $"{asOfStr} ({pendingConfirmed} pending of {confirmed.Count} confirmed cases)";
            list.Add(MetricResult.Ok("Confirmed pending case count", (decimal)pendingConfirmed, MetricUnit.Count,
                periodStr, "Litigation.MatchStatus", "Litigation.CaseStatus"));
        }

        // ── D2: Cases filed against vs by the company ──
        // Only from DossierComputations.LitigationRoles (read from DossierLitigation properties), never inferred.
        var against = lit.FiledAgainstCount;
        var by = lit.FiledByCount;
        var notDet = lit.NotDeterminedCount;
        var d2Period = $"{against} filed against, {by} filed by, {notDet} role not determined";
        list.Add(MetricResult.Ok("Cases filed against vs by the company", (decimal)against, MetricUnit.Count,
            d2Period, "DossierComputations.LitigationRoles"));

        // ── D3: Cases by category ──
        // Confirmed + Probable only. Emits bucket metrics only when relevant buckets exist.
        if (relevant.Count > 0)
        {
            var byCategory = relevant
                .GroupBy(l => string.IsNullOrWhiteSpace(l.CaseCategory) ? "Uncategorised" : l.CaseCategory.Trim())
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var g in byCategory)
            {
                var cat = g.Key;
                var count = g.Count();
                var periodStr = $"{count} of {relevant.Count} confirmed/probable cases";
                list.Add(MetricResult.Ok($"Cases by category ({cat})", (decimal)count, MetricUnit.Count,
                    periodStr, "Litigation.CaseCategory", "Litigation.MatchStatus"));
            }
        }

        // ── D4: Cases by court type ──
        // Confirmed + Probable only. Emits bucket metrics only when relevant buckets exist.
        if (relevant.Count > 0)
        {
            var courtTypeOrder = new[] { "NCLT", "High Court", "DRT", "District Court", "Consumer Court", "Other" };
            var byCourt = relevant
                .GroupBy(l => NormalizeCourtType(l.Court))
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var ct in courtTypeOrder)
            {
                if (byCourt.TryGetValue(ct, out var count) && count > 0)
                {
                    var periodStr = $"{count} of {relevant.Count} confirmed/probable cases";
                    list.Add(MetricResult.Ok($"Cases by court type ({ct})", (decimal)count, MetricUnit.Count,
                        periodStr, "Litigation.Court"));
                }
            }
        }

        // ── D5: NCLT / insolvency case count ──
        // Catalogue: degenerate is "0 with note"
        if (all.Count == 0)
        {
            list.Add(MetricResult.Ok("NCLT / insolvency case count", 0m, MetricUnit.Count,
                $"{asOfStr} (0 NCLT/insolvency cases on file)", "Litigation.CaseCategory", "Litigation.Court"));
        }
        else if (relevant.Count == 0)
        {
            list.Add(MetricResult.Ok("NCLT / insolvency case count", 0m, MetricUnit.Count,
                $"{asOfStr} (0 Confirmed/Probable cases on file)", "Litigation.CaseCategory", "Litigation.Court"));
        }
        else
        {
            var ncltCount = relevant.Count(IsNcltOrInsolvency);
            var periodStr = ncltCount == 0
                ? $"{asOfStr} (0 NCLT/insolvency cases of {relevant.Count} confirmed/probable cases)"
                : $"{asOfStr} ({ncltCount} of {relevant.Count} confirmed/probable cases)";
            list.Add(MetricResult.Ok("NCLT / insolvency case count", (decimal)ncltCount, MetricUnit.Count,
                periodStr, "Litigation.CaseCategory", "Litigation.Court"));
        }

        // ── D6: DRT case count ──
        // Catalogue: degenerate is "0 with note"
        if (all.Count == 0)
        {
            list.Add(MetricResult.Ok("DRT case count", 0m, MetricUnit.Count,
                $"{asOfStr} (0 DRT cases on file)", "Litigation.Court"));
        }
        else if (relevant.Count == 0)
        {
            list.Add(MetricResult.Ok("DRT case count", 0m, MetricUnit.Count,
                $"{asOfStr} (0 Confirmed/Probable cases on file)", "Litigation.Court"));
        }
        else
        {
            var drtCount = relevant.Count(IsDrt);
            var periodStr = drtCount == 0
                ? $"{asOfStr} (0 DRT cases of {relevant.Count} confirmed/probable cases)"
                : $"{asOfStr} ({drtCount} of {relevant.Count} confirmed/probable cases)";
            list.Add(MetricResult.Ok("DRT case count", (decimal)drtCount, MetricUnit.Count,
                periodStr, "Litigation.Court"));
        }

        // ── D7: Pending vs disposed ratio ──
        // Over Confirmed cases only. Single source of truth IsPendingLitigation for Pending.
        // Carve out missing CaseStatus as Indeterminate.
        if (confirmed.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Pending vs disposed ratio", MetricUnit.Percent,
                "0 confirmed litigation cases on record", "Litigation.CaseStatus"));
        }
        else
        {
            var pending = confirmed.Count(IsPendingLitigation);
            var indeterminate = confirmed.Count(l => string.IsNullOrWhiteSpace(l.CaseStatus));
            var disposed = confirmed.Count - pending - indeterminate;
            var assessed = pending + disposed;

            if (assessed == 0)
            {
                list.Add(MetricResult.Insufficient("Pending vs disposed ratio", MetricUnit.Percent,
                    $"{indeterminate} confirmed cases indeterminate (missing CaseStatus)", "Litigation.CaseStatus"));
            }
            else
            {
                var ratio = Math.Round((decimal)pending / assessed * 100m, 1);
                var periodStr = indeterminate > 0
                    ? $"{pending} pending, {disposed} disposed of {assessed} assessed ({indeterminate} indeterminate)"
                    : $"{pending} pending, {disposed} disposed of {assessed} assessed";
                list.Add(MetricResult.Ok("Pending vs disposed ratio", ratio, MetricUnit.Percent,
                    periodStr, "Litigation.CaseStatus"));
            }
        }

        // ── D8: Probable + Uncertain exposure count ──
        // Catalogue: degenerate is "0", caveat: standard disclaimer
        var probableCount = all.Count(l => l.MatchStatus == LitigationMatchStatus.Probable);
        var uncertainCount = all.Count(l => l.MatchStatus == LitigationMatchStatus.Uncertain);
        var exposureCount = probableCount + uncertainCount;
        var d8Period = $"{probableCount} probable, {uncertainCount} unverified (name-match only / court not reached)";
        list.Add(MetricResult.Ok("Probable + Uncertain exposure count", (decimal)exposureCount, MetricUnit.Count,
            d8Period, "Litigation.MatchStatus"));

        return new MetricGroup("Legal history", list);
    }

    /// <summary>Section I — Directors analytics (Issue #61 / D6, docs/analytics-catalogue.json §I).
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup DirectorsMetrics(Dossier.DossierModel model)
    {
        var directors = model.Corporate.Directors;
        var active = directors.Where(d => d.CessationDate is null).ToList();

        // Time anchor: strictly workbook-derived SourceSnapshotDate.
        // Fail-closed: if SourceSnapshotDate is null, I2/I3/I6 emit Insufficient (no fallback to McaDataAsOf or ReportDate).
        DateOnly? asOfDate = model.Cover.SourceSnapshotDate is { } snap
            ? DateOnly.FromDateTime(snap)
            : null;

        static string Fmt(DateOnly d) => d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

        var list = new List<MetricResult>();

        // ── I1: Active director count ──
        // Degenerate: 0 with note.
        var i1Period = directors.Count == 0
            ? "0 directors on record"
            : asOfDate.HasValue
                ? $"as at {Fmt(asOfDate.Value)} ({active.Count} active of {directors.Count} directors on record)"
                : $"({active.Count} active of {directors.Count} directors on record)";

        list.Add(MetricResult.Ok("Active director count", (decimal)active.Count, MetricUnit.Count,
            i1Period, "Director.CessationDate"));

        // ── I2: Average board tenure ──
        // Mean over active directors with valid appointment dates <= asOfDate.
        // Future appointment dates excluded; fail closed if asOfDate is null.
        if (asOfDate is null)
        {
            list.Add(MetricResult.Insufficient("Average board tenure", MetricUnit.Years,
                "No trustworthy source snapshot date on file", "DossierCover.SourceSnapshotDate", "Director.OriginalAppointmentDate"));
        }
        else if (active.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Average board tenure", MetricUnit.Years,
                "0 active directors on record", "Director.CessationDate", "Director.OriginalAppointmentDate"));
        }
        else
        {
            var withDates = active.Where(d => d.OriginalAppointmentDate.HasValue).ToList();
            var futureAppointments = withDates.Where(d => d.OriginalAppointmentDate!.Value > asOfDate.Value).ToList();
            var eligible = withDates.Where(d => d.OriginalAppointmentDate!.Value <= asOfDate.Value).ToList();

            if (eligible.Count == 0)
            {
                if (futureAppointments.Count > 0)
                {
                    list.Add(MetricResult.Insufficient("Average board tenure", MetricUnit.Years,
                        $"All {futureAppointments.Count} active director appointment date(s) are in the future relative to source snapshot {Fmt(asOfDate.Value)}",
                        "Director.OriginalAppointmentDate", "DossierCover.SourceSnapshotDate"));
                }
                else
                {
                    list.Add(MetricResult.Insufficient("Average board tenure", MetricUnit.Years,
                        "No active directors have an appointment date on file", "Director.OriginalAppointmentDate"));
                }
            }
            else
            {
                var tenures = eligible.Select(d => (decimal)(asOfDate.Value.DayNumber - d.OriginalAppointmentDate!.Value.DayNumber) / 365.25m).ToList();
                var meanTenure = Math.Round(tenures.Average(), 1);
                var periodStr = $"mean over {eligible.Count} active directors as at {Fmt(asOfDate.Value)}";
                if (futureAppointments.Count > 0)
                    periodStr += $" ({futureAppointments.Count} future appointment date(s) excluded)";

                list.Add(MetricResult.Ok("Average board tenure", meanTenure, MetricUnit.Years,
                    periodStr, "Director.OriginalAppointmentDate", "DossierCover.SourceSnapshotDate"));
            }
        }

        // ── I3: Directors ceased in the trailing 3 years ──
        // Count ceased directors within 3 years before asOfDate.
        // Degenerate: 0. Fail closed if asOfDate is null.
        if (asOfDate is null)
        {
            list.Add(MetricResult.Insufficient("Directors ceased in the trailing 3 years", MetricUnit.Count,
                "No trustworthy source snapshot date on file", "DossierCover.SourceSnapshotDate", "Director.CessationDate"));
        }
        else
        {
            var from = asOfDate.Value.AddYears(-3);
            var to = asOfDate.Value;
            var ceasedCount = directors.Count(d => d.CessationDate.HasValue && d.CessationDate.Value >= from && d.CessationDate.Value <= to);
            var periodStr = $"{ceasedCount} ceased between {Fmt(from)} and {Fmt(to)}";
            list.Add(MetricResult.Ok("Directors ceased in the trailing 3 years", (decimal)ceasedCount, MetricUnit.Count,
                periodStr, "Director.CessationDate", "DossierCover.SourceSnapshotDate"));
        }

        // ── I4: Board composition by designation ──
        // Group active directors by normalised designation. Buckets are omitted when no directors are active.
        if (active.Count > 0)
        {
            var buckets = active
                .GroupBy(d => NormalizeDirectorDesignation(d.Designation))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key);

            foreach (var bucket in buckets)
            {
                var name = $"Board composition by designation ({bucket.Key})";
                var period = $"{bucket.Count()} of {active.Count} active directors";
                list.Add(MetricResult.Ok(name, (decimal)bucket.Count(), MetricUnit.Count,
                    period, "Director.Designation"));
            }
        }

        // ── I5: Flagged director count ──
        // Over all directors on record. Hyphens and whitespace treated as unflagged.
        // Degenerate: 0.
        var flaggedCount = directors.Count(d => !string.IsNullOrWhiteSpace(d.Flags) && d.Flags.Trim() != "-" && d.Flags.Trim() != "--");
        var i5Period = directors.Count == 0
            ? "0 directors on record"
            : $"{flaggedCount} of {directors.Count} directors on record flagged";

        list.Add(MetricResult.Ok("Flagged director count", (decimal)flaggedCount, MetricUnit.Count,
            i5Period, "Director.Flags"));

        // ── I6: Longest-serving director ──
        // Max tenure over active directors with OriginalAppointmentDate <= asOfDate.
        // Fail closed if asOfDate is null.
        if (asOfDate is null)
        {
            list.Add(MetricResult.Insufficient("Longest-serving director", MetricUnit.Years,
                "No trustworthy source snapshot date on file", "DossierCover.SourceSnapshotDate", "Director.OriginalAppointmentDate"));
        }
        else if (active.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Longest-serving director", MetricUnit.Years,
                "0 active directors on record", "Director.CessationDate", "Director.OriginalAppointmentDate"));
        }
        else
        {
            var withDates = active.Where(d => d.OriginalAppointmentDate.HasValue).ToList();
            var futureAppointments = withDates.Where(d => d.OriginalAppointmentDate!.Value > asOfDate.Value).ToList();
            var eligible = withDates.Where(d => d.OriginalAppointmentDate!.Value <= asOfDate.Value).ToList();

            if (eligible.Count == 0)
            {
                if (futureAppointments.Count > 0)
                {
                    list.Add(MetricResult.Insufficient("Longest-serving director", MetricUnit.Years,
                        $"All {futureAppointments.Count} active director appointment date(s) are in the future relative to source snapshot {Fmt(asOfDate.Value)}",
                        "Director.OriginalAppointmentDate", "DossierCover.SourceSnapshotDate"));
                }
                else
                {
                    list.Add(MetricResult.Insufficient("Longest-serving director", MetricUnit.Years,
                        "No active directors have an appointment date on file", "Director.OriginalAppointmentDate"));
                }
            }
            else
            {
                var longest = eligible.OrderBy(d => d.OriginalAppointmentDate!.Value).ThenBy(d => d.NameRaw).First();
                var tenure = Math.Round((decimal)(asOfDate.Value.DayNumber - longest.OriginalAppointmentDate!.Value.DayNumber) / 365.25m, 1);
                var dirName = string.IsNullOrWhiteSpace(longest.NameRaw) ? "Director" : longest.NameRaw.Trim();
                var periodStr = $"{dirName} (appointed {Fmt(longest.OriginalAppointmentDate!.Value)})";
                if (futureAppointments.Count > 0)
                    periodStr += $" ({futureAppointments.Count} future appointment date(s) excluded)";

                list.Add(MetricResult.Ok("Longest-serving director", tenure, MetricUnit.Years,
                    periodStr, "Director.OriginalAppointmentDate", "DossierCover.SourceSnapshotDate"));
            }
        }

        return new MetricGroup("Directors", list);
    }

    /// <summary>Word-aware normalisation for director designations (catalogue §I, metric I4).
    /// Precedence is critical: Independent must be evaluated before Executive / Whole-time
    /// so 'Non-Executive Independent Director' normalises to 'Independent Director', and
    /// Non-Executive must be evaluated before Executive so 'Non-Executive Director' does not
    /// collapse to 'Whole-time Director'.</summary>
    public static string NormalizeDirectorDesignation(string? designation)
    {
        if (string.IsNullOrWhiteSpace(designation)) return "Other";
        var d = designation.Trim().ToUpperInvariant();

        if (d.Contains("MANAGING")) return "Managing Director";
        if (d.Contains("INDEPENDENT")) return "Independent Director";
        if (d.Contains("NOMINEE")) return "Nominee Director";
        if (d.Contains("ALTERNATE")) return "Alternate Director";
        if (d.Contains("ADDITIONAL")) return "Additional Director";
        if (d.Contains("NON-EXECUTIVE") || d.Contains("NON EXECUTIVE") || d.Contains("NONEXECUTIVE")) return "Non-Executive Director";
        if (d.Contains("WHOLE-TIME") || d.Contains("WHOLE TIME") || d.Contains("WHOLETIME") || d.Contains("EXECUTIVE")) return "Whole-time Director";
        if (d.Contains("DIRECTOR")) return "Director";

        return "Other";
    }

    /// <summary>Section A — Financial trend &amp; leverage analytics (Issues #57 / D2 and #64 / D9,
    /// docs/analytics-catalogue.json §A, A2–A5). A1.x (the 16 source-reported ratios) is already
    /// surfaced verbatim by B1/#39's Ratios sub-tab — not re-modelled here, since <c>MetricResult</c>
    /// is for DERIVED values and the catalogue is explicit that those 16 are never recomputed.
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup FinancialTrendMetrics(Dossier.DossierModel model)
    {
        var list = new List<MetricResult>();
        var years = model.Financials.Standalone.OrderBy(f => f.FinancialYear).ToList();
        var latest = years.Count > 0 ? years[^1] : null;
        var prior = years.Count > 1 ? years[^2] : null;

        // A2.1 / A2.2 / A2.3: CAGR over the widest available span up to 3 years.
        var revenueCagr = AddCagr(list, years, f => f.Revenue, "Revenue CAGR", allowNegativeBaseFallback: false,
            "FinancialYearData.Revenue");
        AddCagr(list, years, f => f.Pat, "PAT CAGR", allowNegativeBaseFallback: true, "FinancialYearData.Pat");
        AddCagr(list, years, f => f.Ebitda, "EBITDA CAGR", allowNegativeBaseFallback: true, "FinancialYearData.Ebitda");

        // A2.4: total debt growth YoY. Debt = reported TotalDebt, else LTB+STB only when both present.
        var debtGrowth = AddYoY(list, "Total debt growth (YoY)", latest, prior, Debt,
            "FinancialYearData.TotalDebt", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings");

        // A2.5: net worth growth YoY, flagged if the latest net worth is negative.
        var negativeNetWorthSuffix = latest?.NetWorth is < 0m ? " (net worth is negative)" : "";
        AddYoY(list, "Net worth growth (YoY)", latest, prior, f => f.NetWorth,
            "FinancialYearData.NetWorth", negativeNetWorthSuffix);

        // A2.6: operating leverage = EBITDA growth % / Revenue growth % over the same (latest YoY) span.
        // Uses the RAW (unrounded) growth fractions for the division — dividing the already-rounded
        // A2.4/A2.5-style 1dp percentages would compound rounding error into the ratio; only the final
        // displayed value is rounded, matching this file's other ratio metrics (e.g. the HHI in
        // ChargeRegisterMetrics).
        {
            var ebitdaGrowthRaw = RawGrowthFraction(latest?.Ebitda, prior?.Ebitda);
            var revenueGrowthRaw = RawGrowthFraction(latest?.Revenue, prior?.Revenue);
            if (latest is null || prior is null)
            {
                list.Add(MetricResult.Insufficient("Operating leverage", MetricUnit.Ratio,
                    "Fewer than 2 reported years", "FinancialYearData.Ebitda", "FinancialYearData.Revenue"));
            }
            else if (ebitdaGrowthRaw is null || revenueGrowthRaw is null)
            {
                list.Add(MetricResult.Insufficient("Operating leverage", MetricUnit.Ratio,
                    "EBITDA or revenue growth could not be computed for the latest year", "FinancialYearData.Ebitda", "FinancialYearData.Revenue"));
            }
            else if (revenueGrowthRaw == 0m)
            {
                list.Add(MetricResult.Insufficient("Operating leverage", MetricUnit.Ratio,
                    "Revenue growth is ~0% — ratio is not meaningful (n/m)", "FinancialYearData.Ebitda", "FinancialYearData.Revenue"));
            }
            else
            {
                var leverage = Math.Round(ebitdaGrowthRaw.Value / revenueGrowthRaw.Value, 2);
                list.Add(MetricResult.Ok("Operating leverage", leverage, MetricUnit.Ratio,
                    $"FY{prior.FinancialYear}–FY{latest.FinancialYear}", "FinancialYearData.Ebitda", "FinancialYearData.Revenue"));
            }
        }

        // A3.1 / A3.2: net debt and net debt / EBITDA, latest FY only.
        decimal? netDebt = null;
        if (latest is null)
        {
            list.Add(MetricResult.Insufficient("Net debt", MetricUnit.Crore,
                "No financial year data on file", "FinancialYearData.TotalDebt", "FinancialYearData.CashAndBank"));
        }
        else
        {
            var debt = Debt(latest);
            if (debt is null || latest.CashAndBank is null)
            {
                list.Add(MetricResult.Insufficient("Net debt", MetricUnit.Crore,
                    $"FY{latest.FinancialYear}: total debt or cash-and-bank not available",
                    "FinancialYearData.TotalDebt", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings", "FinancialYearData.CashAndBank"));
            }
            else
            {
                netDebt = Math.Round(debt.Value - latest.CashAndBank.Value, 2);
                list.Add(MetricResult.Ok("Net debt", netDebt.Value, MetricUnit.Crore, $"FY{latest.FinancialYear}",
                    "FinancialYearData.TotalDebt", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings", "FinancialYearData.CashAndBank"));
            }
        }

        if (latest is null)
        {
            list.Add(MetricResult.Insufficient("Net debt / EBITDA", MetricUnit.Times,
                "No financial year data on file", "FinancialYearData.TotalDebt", "FinancialYearData.Ebitda"));
        }
        else if (netDebt is null)
        {
            list.Add(MetricResult.Insufficient("Net debt / EBITDA", MetricUnit.Times,
                $"FY{latest.FinancialYear}: net debt could not be computed",
                "FinancialYearData.TotalDebt", "FinancialYearData.CashAndBank", "FinancialYearData.Ebitda"));
        }
        else if (latest.Ebitda is null || latest.Ebitda <= 0m)
        {
            list.Add(MetricResult.Insufficient("Net debt / EBITDA", MetricUnit.Times,
                $"FY{latest.FinancialYear}: EBITDA is zero, negative, or not reported — ratio not meaningful",
                "FinancialYearData.TotalDebt", "FinancialYearData.CashAndBank", "FinancialYearData.Ebitda"));
        }
        else
        {
            list.Add(MetricResult.Ok("Net debt / EBITDA", Math.Round(netDebt.Value / latest.Ebitda.Value, 2), MetricUnit.Times,
                $"FY{latest.FinancialYear}", "FinancialYearData.TotalDebt", "FinancialYearData.CashAndBank", "FinancialYearData.Ebitda"));
        }

        // A3.3: FCF proxy = CFO + CFI. A3.4: CFO / PAT. A3.6: cash-to-accrual divergence.
        // All three are null whenever the latest FY's cash-flow figures were column-inferred (the
        // source sheet carried no year header for that section) — inferred CFO/CFI are excluded from
        // every automated conclusion, per the CashFlowYearInferred contract.
        if (latest is null)
        {
            list.Add(MetricResult.Insufficient("Free cash flow (proxy)", MetricUnit.Crore, "No financial year data on file", "FinancialYearData.Cfo", "FinancialYearData.Cfi"));
            list.Add(MetricResult.Insufficient("CFO / PAT", MetricUnit.Ratio, "No financial year data on file", "FinancialYearData.Cfo", "FinancialYearData.Pat"));
            list.Add(MetricResult.Insufficient("Cash-to-accrual divergence", MetricUnit.Percent, "No financial year data on file", "FinancialYearData.Pat", "FinancialYearData.Cfo"));
        }
        else if (latest.CashFlowYearInferred)
        {
            var reason = $"FY{latest.FinancialYear}'s cash-flow figures are column-inferred, not year-labelled in the source — excluded from automated conclusions";
            list.Add(MetricResult.Insufficient("Free cash flow (proxy)", MetricUnit.Crore, reason, "FinancialYearData.Cfo", "FinancialYearData.Cfi", "FinancialYearData.CashFlowYearInferred"));
            list.Add(MetricResult.Insufficient("CFO / PAT", MetricUnit.Ratio, reason, "FinancialYearData.Cfo", "FinancialYearData.Pat", "FinancialYearData.CashFlowYearInferred"));
            list.Add(MetricResult.Insufficient("Cash-to-accrual divergence", MetricUnit.Percent, reason, "FinancialYearData.Pat", "FinancialYearData.Cfo", "FinancialYearData.CashFlowYearInferred"));
        }
        else
        {
            if (latest.Cfo is null)
            {
                list.Add(MetricResult.Insufficient("Free cash flow (proxy)", MetricUnit.Crore,
                    $"FY{latest.FinancialYear}: CFO not reported", "FinancialYearData.Cfo", "FinancialYearData.Cfi"));
            }
            else if (latest.Cfi is null)
            {
                list.Add(MetricResult.Insufficient("Free cash flow (proxy)", MetricUnit.Crore,
                    $"FY{latest.FinancialYear}: CFI not reported", "FinancialYearData.Cfo", "FinancialYearData.Cfi"));
            }
            else
            {
                list.Add(MetricResult.Ok("Free cash flow (proxy)", Math.Round(latest.Cfo.Value + latest.Cfi.Value, 2), MetricUnit.Crore,
                    $"FY{latest.FinancialYear} — CFO less net investing, not true FCF", "FinancialYearData.Cfo", "FinancialYearData.Cfi"));
            }

            if (latest.Cfo is null || latest.Pat is null || latest.Pat <= 0m)
            {
                list.Add(MetricResult.Insufficient("CFO / PAT", MetricUnit.Ratio,
                    $"FY{latest.FinancialYear}: CFO not reported, or PAT is zero/negative", "FinancialYearData.Cfo", "FinancialYearData.Pat"));
            }
            else
            {
                list.Add(MetricResult.Ok("CFO / PAT", Math.Round(latest.Cfo.Value / latest.Pat.Value, 2), MetricUnit.Ratio,
                    $"FY{latest.FinancialYear}", "FinancialYearData.Cfo", "FinancialYearData.Pat"));
            }

            if (latest.Pat is null or 0m || latest.Cfo is null)
            {
                list.Add(MetricResult.Insufficient("Cash-to-accrual divergence", MetricUnit.Percent,
                    $"FY{latest.FinancialYear}: PAT is zero/not reported, or CFO not reported", "FinancialYearData.Pat", "FinancialYearData.Cfo"));
            }
            else
            {
                var divergence = Math.Round((latest.Pat.Value - latest.Cfo.Value) / Math.Abs(latest.Pat.Value) * 100m, 1);
                list.Add(MetricResult.Ok("Cash-to-accrual divergence", divergence, MetricUnit.Percent,
                    $"FY{latest.FinancialYear}", "FinancialYearData.Pat", "FinancialYearData.Cfo"));
            }
        }

        // A3.5: debt-funded-growth flag — only assertable when both A2.1 (Revenue CAGR) and A2.4
        // (debt growth YoY) were themselves computable; never inferred when either is insufficient.
        if (revenueCagr is null || debtGrowth is null)
        {
            list.Add(MetricResult.Insufficient("Debt-funded-growth flag", MetricUnit.Count,
                "Revenue CAGR or total debt growth could not be computed",
                "FinancialYearData.Revenue", "FinancialYearData.TotalDebt"));
        }
        else
        {
            var flagged = debtGrowth.Value > revenueCagr.Value && debtGrowth.Value > 20m;
            list.Add(MetricResult.Ok("Debt-funded-growth flag", flagged ? 1m : 0m, MetricUnit.Count,
                $"debt growth {debtGrowth.Value:0.0}% vs revenue CAGR {revenueCagr.Value:0.0}%",
                "FinancialYearData.Revenue", "FinancialYearData.TotalDebt", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings"));
        }

        // ── Section A4: Cost structure analytics (Issue #64 / D9, docs/analytics-catalogue.json §A4) ──
        // ── Section A5: Forex analytics (Issue #64 / D9, docs/analytics-catalogue.json §A5) ──
        if (latest is null)
        {
            list.Add(MetricResult.Insufficient("Employee cost % of revenue", MetricUnit.Percent, "No financial year data on file", "FinancialParameter['Employee benefits expense']", "FinancialYearData.Revenue"));
            list.Add(MetricResult.Insufficient("Material cost % of revenue", MetricUnit.Percent, "No financial year data on file", "FinancialFact['Cost of materials consumed']", "FinancialYearData.Revenue"));
            list.Add(MetricResult.Insufficient("Other expenses % of revenue", MetricUnit.Percent, "No financial year data on file", "FinancialFact['Other expenses']", "FinancialYearData.Revenue"));
            list.Add(MetricResult.Insufficient("Auditor fee % of revenue", MetricUnit.Percent, "No financial year data on file", "FinancialFact['Payment to auditors']", "FinancialYearData.Revenue"));
            list.Add(MetricResult.Insufficient("Export income % of revenue", MetricUnit.Percent, "No financial year data on file", "FinancialParameter['Income in foreign currency']", "FinancialYearData.Revenue"));
            list.Add(MetricResult.Insufficient("Net forex exposure", MetricUnit.Crore, "No financial year data on file", "FinancialParameter['Income in foreign currency']", "FinancialParameter['Expense in foreign currency']"));
        }
        else
        {
            var fy = latest.FinancialYear;
            var fyPeriod = $"FY{fy}";
            var revenueOk = latest.Revenue is not null && latest.Revenue.Value > 0m;
            var revInputs = "FinancialYearData.Revenue";
            var nonPositiveRevReason = $"FY{fy}: revenue is zero, negative, or not reported";

            // A4.1: Employee cost % of revenue = EmployeeBenefitsExpense / Revenue
            var (empVal, empInsuff) = LookupParameter(model, "Employee cost % of revenue", "Employee benefits expense", MetricUnit.Percent, fy, "FinancialParameter['Employee benefits expense']", revInputs);
            if (!revenueOk)
                list.Add(MetricResult.Insufficient("Employee cost % of revenue", MetricUnit.Percent, nonPositiveRevReason, "FinancialParameter['Employee benefits expense']", revInputs));
            else if (empInsuff is not null)
                list.Add(empInsuff);
            else
                list.Add(MetricResult.Ok("Employee cost % of revenue", (empVal!.Value / latest.Revenue!.Value) * 100m, MetricUnit.Percent, fyPeriod, "FinancialParameter['Employee benefits expense']", revInputs));

            // A4.2: Material cost % of revenue = CostOfMaterialsConsumed / Revenue (P&L section)
            var (matVal, matInsuff) = LookupFact(model, "Material cost % of revenue", "Cost of Materials Consumed", FinancialStatementSection.ProfitAndLoss, fy, "FinancialFact['Cost of materials consumed']", revInputs);
            if (!revenueOk)
                list.Add(MetricResult.Insufficient("Material cost % of revenue", MetricUnit.Percent, nonPositiveRevReason, "FinancialFact['Cost of materials consumed']", revInputs));
            else if (matInsuff is not null)
                list.Add(matInsuff);
            else
                list.Add(MetricResult.Ok("Material cost % of revenue", (matVal!.Value / latest.Revenue!.Value) * 100m, MetricUnit.Percent, fyPeriod, "FinancialFact['Cost of materials consumed']", revInputs));

            // A4.3: Other expenses % of revenue = OtherExpenses / Revenue (P&L section)
            var (othVal, othInsuff) = LookupFact(model, "Other expenses % of revenue", "Other Expenses", FinancialStatementSection.ProfitAndLoss, fy, "FinancialFact['Other expenses']", revInputs);
            if (!revenueOk)
                list.Add(MetricResult.Insufficient("Other expenses % of revenue", MetricUnit.Percent, nonPositiveRevReason, "FinancialFact['Other expenses']", revInputs));
            else if (othInsuff is not null)
                list.Add(othInsuff);
            else
                list.Add(MetricResult.Ok("Other expenses % of revenue", (othVal!.Value / latest.Revenue!.Value) * 100m, MetricUnit.Percent, fyPeriod, "FinancialFact['Other expenses']", revInputs));

            // A4.4: Auditor fee % of revenue = PaymentToAuditors / Revenue (P&L section)
            var (audVal, audInsuff) = LookupFact(model, "Auditor fee % of revenue", "Payment to Auditors", FinancialStatementSection.ProfitAndLoss, fy, "FinancialFact['Payment to auditors']", revInputs);
            if (!revenueOk)
                list.Add(MetricResult.Insufficient("Auditor fee % of revenue", MetricUnit.Percent, nonPositiveRevReason, "FinancialFact['Payment to auditors']", revInputs));
            else if (audInsuff is not null)
                list.Add(audInsuff);
            else
                list.Add(MetricResult.Ok("Auditor fee % of revenue", (audVal!.Value / latest.Revenue!.Value) * 100m, MetricUnit.Percent, fyPeriod, "FinancialFact['Payment to auditors']", revInputs));

            // A5.1: Export income % of revenue = IncomeInForeignCurrency / Revenue
            var (expIncVal, expIncInsuff) = LookupParameter(model, "Export income % of revenue", "Income in foreign currency", MetricUnit.Percent, fy, "FinancialParameter['Income in foreign currency']", revInputs);
            if (!revenueOk)
                list.Add(MetricResult.Insufficient("Export income % of revenue", MetricUnit.Percent, nonPositiveRevReason, "FinancialParameter['Income in foreign currency']", revInputs));
            else if (expIncInsuff is not null)
                list.Add(expIncInsuff);
            else
                list.Add(MetricResult.Ok("Export income % of revenue", (expIncVal!.Value / latest.Revenue!.Value) * 100m, MetricUnit.Percent, fyPeriod, "FinancialParameter['Income in foreign currency']", revInputs));

            // A5.2: Net forex exposure = IncomeInForeignCurrency - ExpenseInForeignCurrency
            var (forexIncVal, forexIncInsuff) = LookupParameter(model, "Net forex exposure", "Income in foreign currency", MetricUnit.Crore, fy, "FinancialParameter['Income in foreign currency']", "FinancialParameter['Expense in foreign currency']");
            var (forexExpVal, forexExpInsuff) = LookupParameter(model, "Net forex exposure", "Expense in foreign currency", MetricUnit.Crore, fy, "FinancialParameter['Income in foreign currency']", "FinancialParameter['Expense in foreign currency']");
            if (forexIncInsuff is not null && forexExpInsuff is not null)
            {
                if (forexIncInsuff.InsufficiencyReason?.Contains("explicitly reported as '-'") == true &&
                    forexExpInsuff.InsufficiencyReason?.Contains("explicitly reported as '-'") == true)
                {
                    list.Add(MetricResult.Insufficient("Net forex exposure", MetricUnit.Crore,
                        $"FY{fy}: 'Income in foreign currency' and 'Expense in foreign currency' explicitly reported as '-'",
                        "FinancialParameter['Income in foreign currency']", "FinancialParameter['Expense in foreign currency']"));
                }
                else
                {
                    list.Add(MetricResult.Insufficient("Net forex exposure", MetricUnit.Crore,
                        $"FY{fy}: 'Income in foreign currency' and 'Expense in foreign currency' unavailable ({forexIncInsuff.InsufficiencyReason}; {forexExpInsuff.InsufficiencyReason})",
                        "FinancialParameter['Income in foreign currency']", "FinancialParameter['Expense in foreign currency']"));
                }
            }
            else if (forexIncInsuff is not null)
                list.Add(forexIncInsuff);
            else if (forexExpInsuff is not null)
                list.Add(forexExpInsuff);
            else
            {
                var netForex = forexIncVal!.Value - forexExpVal!.Value;
                list.Add(MetricResult.Ok("Net forex exposure", netForex, MetricUnit.Crore, fyPeriod, "FinancialParameter['Income in foreign currency']", "FinancialParameter['Expense in foreign currency']"));
            }
        }

        return new MetricGroup("Financial trend & leverage", list);
    }

    /// <summary>Reported TotalDebt when present, else LongTermBorrowings + ShortTermBorrowings — only
    /// when BOTH components are non-null (a missing component means debt is unknown, never zero).</summary>
    private static decimal? Debt(FinancialYearData f) =>
        f.TotalDebt ?? (f.LongTermBorrowings is not null && f.ShortTermBorrowings is not null
            ? f.LongTermBorrowings.Value + f.ShortTermBorrowings.Value
            : null);

    private static decimal? YoYPercent(decimal? current, decimal? prior)
    {
        if (current is null || prior is null || prior.Value == 0m) return null;
        return Math.Round((current.Value - prior.Value) / Math.Abs(prior.Value) * 100m, 1);
    }

    /// <summary>Same growth calculation as <see cref="YoYPercent"/> but as an unrounded fraction (not a
    /// rounded percent) — for a metric that divides two growth rates against each other (A2.6), so the
    /// division isn't compounding two independent 1dp roundings.</summary>
    private static decimal? RawGrowthFraction(decimal? current, decimal? prior)
    {
        if (current is null || prior is null || prior.Value == 0m) return null;
        return (current.Value - prior.Value) / Math.Abs(prior.Value);
    }

    /// <summary>Appends a YoY-growth <see cref="MetricResult"/> (latest vs prior FY) and returns the
    /// raw percent so a downstream metric (A3.5) can consume it without re-parsing display text.</summary>
    private static decimal? AddYoY(
        List<MetricResult> list, string label, FinancialYearData? latest, FinancialYearData? prior,
        Func<FinancialYearData, decimal?> selector, params string[] inputs)
    {
        if (latest is null || prior is null)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Percent, "Fewer than 2 reported years", inputs));
            return null;
        }
        var pct = YoYPercent(selector(latest), selector(prior));
        if (pct is null)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Percent,
                $"FY{prior.FinancialYear} value is zero/not reported — growth undefined", inputs));
            return null;
        }
        list.Add(MetricResult.Ok(label, pct.Value, MetricUnit.Percent, $"FY{prior.FinancialYear}–FY{latest.FinancialYear}", inputs));
        return pct;
    }

    private static decimal? AddYoY(
        List<MetricResult> list, string label, FinancialYearData? latest, FinancialYearData? prior,
        Func<FinancialYearData, decimal?> selector, string input, string noteSuffix)
    {
        if (latest is null || prior is null)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Percent, "Fewer than 2 reported years", input));
            return null;
        }
        var pct = YoYPercent(selector(latest), selector(prior));
        if (pct is null)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Percent,
                $"FY{prior.FinancialYear} value is zero/not reported — growth undefined", input));
            return null;
        }
        list.Add(MetricResult.Ok(label, pct.Value, MetricUnit.Percent, $"FY{prior.FinancialYear}–FY{latest.FinancialYear}{noteSuffix}", input));
        return pct;
    }

    /// <summary>CAGR over the widest available span up to 3 CALENDAR years: the base point is the
    /// earliest non-null <paramref name="selector"/> point whose FY is within 3 years of the latest
    /// non-null point (never chosen by counting rows back) — insufficient when no earlier point falls
    /// inside that 3-year window, even if an older point exists further back. The compounding exponent
    /// is always the true elapsed FYs between the chosen points (endPoint.Year - basePoint.Year), which
    /// the window selection now guarantees is between 1 and 3. When the base-year value is negative and
    /// <paramref name="allowNegativeBaseFallback"/> is true, falls back to a total %-change figure with
    /// a caveat in the period text (not a true CAGR) instead of failing outright. Returns the computed
    /// percent (Ok path only) so a downstream metric can reuse it.</summary>
    private static decimal? AddCagr(
        List<MetricResult> list, List<FinancialYearData> years, Func<FinancialYearData, decimal?> selector,
        string label, bool allowNegativeBaseFallback, string input)
    {
        var series = years.Where(y => selector(y) is not null)
            .Select(y => (Year: y.FinancialYear, Value: selector(y)!.Value)).ToList();
        return AddCagrCore(list, series, label, allowNegativeBaseFallback, input);
    }

    /// <summary>The CAGR algorithm itself, over any (Year, Value) series — extracted from
    /// <see cref="AddCagr"/> so a metric keyed on something other than <see cref="FinancialYearData"/>
    /// (e.g. E6's related-party-transaction totals per FY) can reuse the exact same window-selection and
    /// negative-base handling without re-deriving it.</summary>
    private static decimal? AddCagrCore(
        List<MetricResult> list, List<(int Year, decimal Value)> series,
        string label, bool allowNegativeBaseFallback, string input)
    {
        if (series.Count < 2)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Percent,
                $"Fewer than 2 reported years for {label}", input));
            return null;
        }

        var endPoint = series[^1];

        // The base point must be chosen by CALENDAR distance from the end point, not by counting
        // non-null rows back — a sparse series (data points spread further apart than 1 FY, because
        // some FYs' value for this field is null) would otherwise let a 3-row lookback span far more
        // than 3 actual years, producing e.g. a 10-year "CAGR" despite the contract's 3-FY window. This
        // also fixes the companion bug where the exponent must match: it is always the true elapsed FYs
        // between the chosen points, not a count of rows.
        var candidates = series.Where(p => p.Year < endPoint.Year && endPoint.Year - p.Year <= 3)
            .OrderBy(p => p.Year).ToList();
        if (candidates.Count == 0)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Percent,
                $"FY{endPoint.Year}: no earlier reported year within the last 3 FYs — {label} requires a base year inside that window", input));
            return null;
        }
        var basePoint = candidates[0];
        var period = $"FY{basePoint.Year}–FY{endPoint.Year}";
        var yearsElapsed = endPoint.Year - basePoint.Year; // guaranteed 1..3 by the candidates filter above

        if (basePoint.Value == 0m)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Percent,
                $"{period}: base year value is zero — {label} undefined", input));
            return null;
        }

        // CAGR (a fractional root) is only real-valued when base and end share a sign and the ratio is
        // positive — not just when the base is negative. A positive base with a negative end (e.g. PAT
        // swinging from profit to loss) makes the ratio negative too, and Math.Pow(negative, 1/n) is
        // NaN for a non-integer exponent, which throws on cast to decimal — so this checks the ratio,
        // not just the base's sign, before ever computing it.
        var cagrUndefined = basePoint.Value < 0m || endPoint.Value <= 0m;
        if (cagrUndefined)
        {
            if (!allowNegativeBaseFallback)
            {
                list.Add(MetricResult.Insufficient(label, MetricUnit.Percent,
                    $"{period}: base or end year value is zero/negative — CAGR undefined", input));
                return null;
            }
            var totalChange = Math.Round((endPoint.Value - basePoint.Value) / Math.Abs(basePoint.Value) * 100m, 1);
            list.Add(MetricResult.Ok(label, totalChange, MetricUnit.Percent,
                $"{period} — total % change (base or end year is zero/negative, not a true CAGR)", input));
            return totalChange;
        }

        var ratio = (double)(endPoint.Value / basePoint.Value);
        var cagr = Math.Round((decimal)(Math.Pow(ratio, 1.0 / yearsElapsed) - 1) * 100m, 1);
        list.Add(MetricResult.Ok(label, cagr, MetricUnit.Percent, period, input));
        return cagr;
    }

    private static string NormalizeFinancialLabel(string label) =>
        Regex.Replace(label.Trim(), @"\s+", " ").ToLowerInvariant();

    private static bool IsCroreUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return false;
        var norm = new string(unit.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return norm is "rscrore" or "crore" or "crores" or "inrcrore";
    }

    private static (decimal? Value, MetricResult? Insufficient) LookupFact(
        Dossier.DossierModel model,
        string targetConcept,
        string approvedLabel,
        FinancialStatementSection expectedSection,
        int fy,
        params string[] inputs)
    {
        var normApproved = NormalizeFinancialLabel(approvedLabel);
        var matches = model.Financials.Facts
            .Where(f => f.Basis == FinancialBasis.Standalone
                     && f.FinancialYear == fy
                     && f.Section == expectedSection
                     && NormalizeFinancialLabel(f.Label) == normApproved)
            .ToList();

        if (matches.Count == 0)
        {
            return (null, MetricResult.Insufficient(targetConcept, MetricUnit.Percent,
                $"FY{fy}: '{approvedLabel}' not reported in {expectedSection}", inputs));
        }

        var hasDash = matches.Any(f => f.RawValue.Trim() == "-");
        var hasNonNumeric = matches.Any(f => f.NumericValue is null);
        var distinctNumeric = matches.Select(f => f.NumericValue).Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();

        if (distinctNumeric.Count > 0 && hasNonNumeric)
        {
            var conflictDetail = hasDash ? "- vs numeric" : "numeric vs non-numeric";
            return (null, MetricResult.Insufficient(targetConcept, MetricUnit.Percent,
                $"FY{fy}: conflicting values reported for '{approvedLabel}' in {expectedSection} ({conflictDetail})", inputs));
        }

        if (distinctNumeric.Count > 1)
        {
            return (null, MetricResult.Insufficient(targetConcept, MetricUnit.Percent,
                $"FY{fy}: conflicting values reported for '{approvedLabel}' in {expectedSection} ({string.Join(", ", distinctNumeric)})", inputs));
        }

        if (hasDash)
        {
            return (null, MetricResult.Insufficient(targetConcept, MetricUnit.Percent,
                $"FY{fy}: '{approvedLabel}' in {expectedSection} explicitly reported as '-'", inputs));
        }

        if (distinctNumeric.Count == 0)
        {
            return (null, MetricResult.Insufficient(targetConcept, MetricUnit.Percent,
                $"FY{fy}: '{approvedLabel}' in {expectedSection} is not numeric ({matches[0].RawValue})", inputs));
        }

        return (distinctNumeric[0], null);
    }

    private static (decimal? Value, MetricResult? Insufficient) LookupParameter(
        Dossier.DossierModel model,
        string targetConcept,
        string approvedLabel,
        MetricUnit resultUnit,
        int fy,
        params string[] inputs)
    {
        var normApproved = NormalizeFinancialLabel(approvedLabel);
        var matches = model.Financials.Parameters
            .Where(p => p.FinancialYear == fy && NormalizeFinancialLabel(p.ParameterName) == normApproved)
            .ToList();

        if (matches.Count == 0)
        {
            return (null, MetricResult.Insufficient(targetConcept, resultUnit,
                $"FY{fy}: '{approvedLabel}' not reported", inputs));
        }

        // Check if any row was reported as "-"
        var hasDash = matches.Any(p => p.RawValue.Trim() == "-" || p.TextValue?.Trim() == "-");
        var hasNonNumeric = matches.Any(p => p.NumericValue is null);
        var distinctNumeric = matches.Select(p => p.NumericValue).Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();

        if (distinctNumeric.Count > 0 && hasNonNumeric)
        {
            var conflictDetail = hasDash ? "- vs numeric" : "numeric vs non-numeric";
            return (null, MetricResult.Insufficient(targetConcept, resultUnit,
                $"FY{fy}: conflicting values reported for '{approvedLabel}' ({conflictDetail})", inputs));
        }

        if (distinctNumeric.Count > 1)
        {
            return (null, MetricResult.Insufficient(targetConcept, resultUnit,
                $"FY{fy}: conflicting values reported for '{approvedLabel}' ({string.Join(", ", distinctNumeric)})", inputs));
        }

        if (hasDash)
        {
            return (null, MetricResult.Insufficient(targetConcept, resultUnit,
                $"FY{fy}: '{approvedLabel}' explicitly reported as '-'", inputs));
        }

        // Unit safety: must be Crore
        var invalidUnitParam = matches.FirstOrDefault(p => !IsCroreUnit(p.Unit));
        if (invalidUnitParam is not null)
        {
            return (null, MetricResult.Insufficient(targetConcept, resultUnit,
                $"FY{fy}: '{approvedLabel}' unit is '{invalidUnitParam.Unit}' (expected Rs. Crore)", inputs));
        }

        if (distinctNumeric.Count == 0)
        {
            return (null, MetricResult.Insufficient(targetConcept, resultUnit,
                $"FY{fy}: '{approvedLabel}' is not numeric ({matches[0].RawValue})", inputs));
        }

        return (distinctNumeric[0], null);
    }

    /// <summary>Section C — Shareholding analytics (Issue #59 / D4, docs/analytics-catalogue.json §C).
    /// C3/C4/C6 are scoped to the latest FY / AsOnDate their own source reports — summing a percentage
    /// figure across multiple years would produce a meaningless &gt;100% total. C.rejected (FII vs DII
    /// institutional split) is not modelled: the source has no institutional-category field.
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup ShareholdingMetrics(Dossier.DossierModel model)
    {
        var list = new List<MetricResult>();
        var structure = model.Corporate.Structure;
        var shareholders = model.Corporate.Shareholders;
        var pattern = model.Corporate.ShareholdingPattern;

        // C1: Promoter / public holding split — verbatim from CompanyStructure, never recomputed.
        if (structure?.PromoterHoldingPercent is not { } promoterPct)
        {
            list.Add(MetricResult.Insufficient("Promoter / public holding split", MetricUnit.Percent,
                "SHARE HOLDING SUMMARY block not reported (or promoter % missing)",
                "CompanyStructure.PromoterHoldingPercent", "CompanyStructure.PublicHoldingPercent"));
        }
        else
        {
            var publicStr = structure.PublicHoldingPercent is { } pub ? $"{pub:0.##}%" : "not reported";
            list.Add(MetricResult.Ok("Promoter / public holding split", promoterPct, MetricUnit.Percent,
                $"Promoter {promoterPct:0.##}% / Public {publicStr}",
                "CompanyStructure.PromoterHoldingPercent", "CompanyStructure.PublicHoldingPercent"));
        }

        // C2: Shareholder count / concentration — flagged when the base is single-digit (<= 7 total).
        if (structure?.TotalShareholders is not { } totalShareholders)
        {
            list.Add(MetricResult.Insufficient("Shareholder count / concentration", MetricUnit.Count,
                "Total shareholder count not reported",
                "CompanyStructure.TotalShareholders", "CompanyStructure.PromoterShareholders"));
        }
        else
        {
            var promoterShareholdersStr = structure.PromoterShareholders is { } ps ? $"{ps} promoter" : "promoter count not reported";
            var flagSuffix = totalShareholders <= 7 ? " — single-digit shareholder base" : "";
            list.Add(MetricResult.Ok("Shareholder count / concentration", totalShareholders, MetricUnit.Count,
                $"{promoterShareholdersStr} of {totalShareholders} total{flagSuffix}",
                "CompanyStructure.TotalShareholders", "CompanyStructure.PromoterShareholders"));
        }

        // C3 / C4: only the >5%-disclosed sheet is authoritative for a holder's percentage — a Director
        // Shareholding-only row can be well under 5% and would violate the disclosure-threshold caveat
        // if included. Scoped to the latest FY that sheet reports, so a holder isn't summed across
        // multiple years into a nonsensical total.
        var disclosed = shareholders.Where(s => s.SourceType == ShareholdingSourceType.MajorShareholding).ToList();
        if (disclosed.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Top-5 >5%-shareholder concentration", MetricUnit.Percent,
                "No >5%-disclosed shareholders on record", "Shareholding.HoldingPercentage", "Shareholding.FinancialYear"));
            list.Add(MetricResult.Insufficient("Corporate vs individual >5%-shareholder split", MetricUnit.Percent,
                "No >5%-disclosed shareholders on record", "Shareholding.ShareholderType", "Shareholding.HoldingPercentage"));
        }
        else
        {
            var latestYear = disclosed.Max(s => s.FinancialYear);
            var latestYearRows = disclosed.Where(s => s.FinancialYear == latestYear).ToList();
            var missingPct = latestYearRows.Count(s => s.HoldingPercentage is null);

            // Fail closed on ANY missing percentage in the population, rather than silently summing
            // only the holders with a known value — a partial sum over an incomplete population would
            // misrepresent itself as the true top-5 concentration / type split (the excluded holder(s)
            // could plausibly change either result). Per the catalogue's "component sum requires EVERY
            // component" safety gate.
            if (missingPct > 0)
            {
                var reason = $"FY{latestYear}: {missingPct} of {latestYearRows.Count} disclosed holder(s) have no reported HoldingPercentage — the sum would be incomplete";
                list.Add(MetricResult.Insufficient("Top-5 >5%-shareholder concentration", MetricUnit.Percent, reason,
                    "Shareholding.HoldingPercentage", "Shareholding.FinancialYear"));
                list.Add(MetricResult.Insufficient("Corporate vs individual >5%-shareholder split", MetricUnit.Percent, reason,
                    "Shareholding.ShareholderType", "Shareholding.HoldingPercentage"));
            }
            else
            {
                // C3
                var top5 = latestYearRows.OrderByDescending(s => s.HoldingPercentage!.Value).Take(5).ToList();
                var top5Sum = top5.Sum(s => s.HoldingPercentage!.Value);
                list.Add(MetricResult.Ok("Top-5 >5%-shareholder concentration", top5Sum, MetricUnit.Percent,
                    $"FY{latestYear} — top {top5.Count} of {latestYearRows.Count} disclosed >5% holder(s) " +
                    "(only holders above the 5% MCA disclosure threshold are captured)",
                    "Shareholding.HoldingPercentage", "Shareholding.FinancialYear"));

                // C4: grouped by the sheet's own ShareholderType. A blank type is not a category — per
                // the catalogue it makes the metric insufficient only when EVERY disclosed holder lacks
                // a type; when only some do, those rows are excluded from the split (never given a fake
                // "Unspecified" bucket) and the exclusion is named in the period text of the buckets that
                // do render. This is independent of the percentage-completeness gate above — every row
                // reaching here already has a known percentage.
                var typed = latestYearRows.Where(s => !string.IsNullOrWhiteSpace(s.ShareholderType)).ToList();
                if (typed.Count == 0)
                {
                    list.Add(MetricResult.Insufficient("Corporate vs individual >5%-shareholder split", MetricUnit.Percent,
                        $"FY{latestYear}: {latestYearRows.Count} disclosed holder(s), none with a reported ShareholderType",
                        "Shareholding.ShareholderType", "Shareholding.HoldingPercentage"));
                }
                else
                {
                    var untyped = latestYearRows.Count - typed.Count;
                    var exclusionNote = untyped > 0 ? $" ({untyped} with no reported type excluded)" : "";
                    foreach (var g in typed
                        .GroupBy(s => s.ShareholderType!.Trim())
                        .OrderByDescending(g => g.Sum(s => s.HoldingPercentage!.Value)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var sum = g.Sum(s => s.HoldingPercentage!.Value);
                        list.Add(MetricResult.Ok($"Corporate vs individual >5%-shareholder split ({g.Key})", sum, MetricUnit.Percent,
                            $"FY{latestYear} — {g.Count()} of {latestYearRows.Count} disclosed >5% holder(s){exclusionNote}",
                            "Shareholding.ShareholderType", "Shareholding.HoldingPercentage"));
                    }
                }
            }
        }

        // C5 / C6: from the Structure sheet's SEBI-category grids. A parent-only numbered category with
        // no value of its own is never stored (see ShareholdingPatternRow), so grouping by
        // (CategoryGroup ?? Category) never double-counts.
        var dated = pattern.Where(p => p.AsOnDate is not null).ToList();
        if (dated.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Multi-year promoter-holding trend", MetricUnit.Percent,
                "No promoter shareholding-pattern rows on record (Structure sheet grid absent)",
                "ShareholdingPatternRow.HolderClass", "ShareholdingPatternRow.AsOnDate", "ShareholdingPatternRow.EquityPercent"));
        }
        else
        {
            var dates = dated.Select(p => p.AsOnDate!.Value).Distinct().OrderBy(d => d).ToList();
            var singlePoint = dates.Count == 1;
            foreach (var date in dates)
            {
                // Fail closed on ANY Promoter row missing EquityPercent for this date — summing only the
                // rows that happen to have a value (old behaviour) would silently understate the true
                // total and present a partial sum as if it were complete. A missing/valueless row must
                // also never collapse to a false 0% via `?? 0m` on an empty Sum.
                var promoterRows = dated.Where(p => p.AsOnDate == date && p.HolderClass == ShareholderClass.Promoter).ToList();
                var missingValue = promoterRows.Count(p => p.EquityPercent is null);
                var note = singlePoint ? " (single point — no multi-year trend available)" : "";
                if (promoterRows.Count == 0)
                {
                    list.Add(MetricResult.Insufficient($"Multi-year promoter-holding trend (FY{date.Year})", MetricUnit.Percent,
                        $"As on {date:d MMM yyyy}: no Promoter-class rows reported",
                        "ShareholdingPatternRow.HolderClass", "ShareholdingPatternRow.AsOnDate", "ShareholdingPatternRow.EquityPercent"));
                }
                else if (missingValue > 0)
                {
                    list.Add(MetricResult.Insufficient($"Multi-year promoter-holding trend (FY{date.Year})", MetricUnit.Percent,
                        $"As on {date:d MMM yyyy}: {missingValue} of {promoterRows.Count} Promoter-class row(s) have no reported EquityPercent — the total would be incomplete",
                        "ShareholdingPatternRow.HolderClass", "ShareholdingPatternRow.AsOnDate", "ShareholdingPatternRow.EquityPercent"));
                }
                else
                {
                    var promoterSum = promoterRows.Sum(p => p.EquityPercent!.Value);
                    list.Add(MetricResult.Ok($"Multi-year promoter-holding trend (FY{date.Year})", promoterSum, MetricUnit.Percent,
                        $"As on {date:d MMM yyyy}{note}",
                        "ShareholdingPatternRow.HolderClass", "ShareholdingPatternRow.AsOnDate", "ShareholdingPatternRow.EquityPercent"));
                }
            }

            // C6: latest AsOnDate only, one metric per (class, top-level numbered category). Same
            // fail-closed rule as C5 — a category with ANY sub-row missing EquityPercent must not
            // silently sum only the known ones, and must never collapse to a false 0%.
            var latestDate = dates[^1];
            var latestRows = dated.Where(p => p.AsOnDate == latestDate).ToList();
            foreach (var g in latestRows
                .GroupBy(p => (p.HolderClass, TopLevel: p.CategoryGroup ?? p.Category))
                .OrderBy(g => g.Key.HolderClass).ThenBy(g => g.Min(p => p.DisplayOrder)))
            {
                var rows = g.ToList();
                var missing = rows.Count(p => p.EquityPercent is null);
                if (missing > 0)
                {
                    list.Add(MetricResult.Insufficient($"SEBI-category grid rollup ({g.Key.HolderClass}: {g.Key.TopLevel})", MetricUnit.Percent,
                        $"As on {latestDate:d MMM yyyy}: {missing} of {rows.Count} row(s) in this category have no reported EquityPercent — the total would be incomplete",
                        "ShareholdingPatternRow.HolderClass", "ShareholdingPatternRow.Category", "ShareholdingPatternRow.EquityPercent"));
                }
                else
                {
                    var sum = rows.Sum(p => p.EquityPercent!.Value);
                    list.Add(MetricResult.Ok($"SEBI-category grid rollup ({g.Key.HolderClass}: {g.Key.TopLevel})", sum, MetricUnit.Percent,
                        $"As on {latestDate:d MMM yyyy}",
                        "ShareholdingPatternRow.HolderClass", "ShareholdingPatternRow.Category", "ShareholdingPatternRow.EquityPercent"));
                }
            }
        }

        return new MetricGroup("Shareholding", list);
    }

    /// <summary>Section H — EPFO / labour analytics (Issue #62 / D7, docs/analytics-catalogue.json §H).
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup EpfoMetrics(Dossier.DossierModel model)
    {
        var contribs = model.Compliance.Epfo;
        var establishments = model.Compliance.EpfoEstablishments;
        var list = new List<MetricResult>();

        // ── Parse and sort wage months chronologically ──
        var parsedContribs = new List<(EpfoContribution Contrib, DateOnly Month)>();
        var unparseableContribs = new List<EpfoContribution>();

        foreach (var c in contribs)
        {
            if (TryParseWageMonth(c.WageMonth, out var m))
                parsedContribs.Add((c, m));
            else
                unparseableContribs.Add(c);
        }

        var unparseableSuffix = unparseableContribs.Count > 0
            ? $" ({unparseableContribs.Count} unparseable wage month(s) excluded)"
            : "";

        // Remittance-level assessments (establishment-month remittance records)
        var assessed = contribs.Where(c => c.PaymentDate is not null && c.PaymentDueDate is not null).ToList();
        var indeterminate = contribs.Where(c => c.PaymentDate is null || c.PaymentDueDate is null).ToList();

        // ── H1: PF remittance on-time rate ──
        if (contribs.Count == 0)
        {
            list.Add(MetricResult.Insufficient("PF remittance on-time rate", MetricUnit.Percent,
                "No EPFO contribution records on file",
                "EpfoContribution.PaymentDate", "EpfoContribution.PaymentDueDate", "EpfoContribution.PaymentStatus"));
        }
        else if (assessed.Count == 0)
        {
            list.Add(MetricResult.Insufficient("PF remittance on-time rate", MetricUnit.Percent,
                $"0 of {contribs.Count} remittance records have both payment and due dates ({indeterminate.Count} indeterminate)",
                "EpfoContribution.PaymentDate", "EpfoContribution.PaymentDueDate", "EpfoContribution.PaymentStatus"));
        }
        else
        {
            var onTime = assessed.Count(c => c.PaymentDate <= c.PaymentDueDate);
            var late = assessed.Count - onTime;
            var rate = Math.Round((decimal)onTime / assessed.Count * 100m, 1);
            var periodStr = indeterminate.Count > 0
                ? $"{onTime} on-time, {late} late of {assessed.Count} assessed remittances ({indeterminate.Count} indeterminate)"
                : $"{onTime} on-time, {late} late of {assessed.Count} assessed remittances";

            list.Add(MetricResult.Ok("PF remittance on-time rate", rate, MetricUnit.Percent,
                periodStr, "EpfoContribution.PaymentDate", "EpfoContribution.PaymentDueDate", "EpfoContribution.PaymentStatus"));
        }

        // ── H2: PF late-remittance count ──
        if (contribs.Count == 0)
        {
            list.Add(MetricResult.Insufficient("PF late-remittance count", MetricUnit.Count,
                "No EPFO contribution records on file",
                "EpfoContribution.PaymentDate", "EpfoContribution.PaymentDueDate", "EpfoContribution.WageMonth"));
        }
        else if (assessed.Count == 0)
        {
            list.Add(MetricResult.Insufficient("PF late-remittance count", MetricUnit.Count,
                $"{indeterminate.Count} remittance records indeterminate (missing payment or due date)",
                "EpfoContribution.PaymentDate", "EpfoContribution.PaymentDueDate", "EpfoContribution.WageMonth"));
        }
        else
        {
            var lateCount = assessed.Count(c => c.PaymentDate > c.PaymentDueDate);
            var periodStr = indeterminate.Count > 0
                ? $"{lateCount} late remittance(s) of {assessed.Count} assessed remittances ({indeterminate.Count} indeterminate)"
                : $"{lateCount} late remittance(s) of {assessed.Count} assessed remittances";

            list.Add(MetricResult.Ok("PF late-remittance count", (decimal)lateCount, MetricUnit.Count,
                periodStr, "EpfoContribution.PaymentDate", "EpfoContribution.PaymentDueDate", "EpfoContribution.WageMonth"));
        }

        // Month groups ordered strictly by DateOnly
        var monthGroups = parsedContribs
            .GroupBy(x => x.Month)
            .OrderBy(g => g.Key)
            .ToList();

        // ── H3: Latest-month PF contribution ──
        // ── H4: Employee count (EPFO) + trend ──
        // ── H5: PF contribution per recorded EPFO employee (latest month) ──
        bool h3Ok = false;
        decimal? h3AmountCr = null;
        bool h4Ok = false;
        int? h4Headcount = null;

        if (monthGroups.Count == 0)
        {
            var noMonthReason = contribs.Count == 0
                ? "No EPFO contribution records on file"
                : $"All {contribs.Count} EPFO wage month records were unparseable";

            list.Add(MetricResult.Insufficient("Latest-month PF contribution", MetricUnit.Crore,
                noMonthReason, "EpfoContribution.ContributionAmountCrore", "EpfoContribution.WageMonth"));

            list.Add(MetricResult.Insufficient("Employee count (EPFO) + trend", MetricUnit.Count,
                noMonthReason, "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));

            list.Add(MetricResult.Insufficient("PF contribution per recorded EPFO employee (latest month)", MetricUnit.Rupees,
                noMonthReason, "EpfoContribution.ContributionAmountCrore", "EpfoContribution.EmployeeCount"));
        }
        else
        {
            var latestGroup = monthGroups[^1];
            var latestMonth = latestGroup.Key;
            var latestRows = latestGroup.Select(x => x.Contrib).ToList();
            var latestMonthStr = latestMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture);

            // H3: Latest-month PF contribution
            var missingAmount = latestRows.Count(r => r.ContributionAmountCrore is null);
            if (missingAmount > 0)
            {
                list.Add(MetricResult.Insufficient("Latest-month PF contribution", MetricUnit.Crore,
                    $"Incomplete data: {missingAmount} of {latestRows.Count} establishment records in {latestMonthStr} missing ContributionAmount",
                    "EpfoContribution.ContributionAmountCrore", "EpfoContribution.WageMonth"));
            }
            else
            {
                var totalContribCr = latestRows.Sum(r => r.ContributionAmountCrore!.Value);
                h3AmountCr = totalContribCr;
                h3Ok = true;
                var periodStr = $"{latestMonthStr} ({latestRows.Count} establishment(s)){unparseableSuffix}";
                list.Add(MetricResult.Ok("Latest-month PF contribution", totalContribCr, MetricUnit.Crore,
                    periodStr, "EpfoContribution.ContributionAmountCrore", "EpfoContribution.WageMonth"));
            }

            // H4: Employee count (EPFO) + trend
            var missingHeadcount = latestRows.Count(r => r.EmployeeCount is null);
            if (missingHeadcount > 0)
            {
                list.Add(MetricResult.Insufficient("Employee count (EPFO) + trend", MetricUnit.Count,
                    $"Incomplete data: {missingHeadcount} of {latestRows.Count} establishment records in {latestMonthStr} missing EmployeeCount",
                    "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));
            }
            else
            {
                var totalHeadcount = latestRows.Sum(r => r.EmployeeCount!.Value);
                h4Headcount = totalHeadcount;
                h4Ok = true;

                // 12-month prior trend
                var priorMonth = latestMonth.AddMonths(-12);
                var priorGroup = monthGroups.FirstOrDefault(g => g.Key == priorMonth);
                string trendDisclosure;

                if (priorGroup is null)
                {
                    trendDisclosure = "no record 12 months prior";
                }
                else
                {
                    var priorRows = priorGroup.Select(x => x.Contrib).ToList();
                    if (priorRows.Any(r => r.EmployeeCount is null))
                    {
                        trendDisclosure = "trend not assessed due to incomplete prior-month data";
                    }
                    else
                    {
                        static string NormEstId(string? id) => string.IsNullOrWhiteSpace(id) ? "(unknown)" : id.Trim().ToUpperInvariant();
                        var latestEstIds = latestRows.Select(r => NormEstId(r.EstablishmentId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var priorEstIds = priorRows.Select(r => NormEstId(r.EstablishmentId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        if (!latestEstIds.SetEquals(priorEstIds))
                        {
                            var priorMonthStr = priorMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture);
                            var detail = latestEstIds.Count == priorEstIds.Count
                                ? $"{string.Join(", ", latestEstIds)} in {latestMonthStr} vs {string.Join(", ", priorEstIds)} in {priorMonthStr}"
                                : $"{latestEstIds.Count} establishment(s) in {latestMonthStr} vs {priorEstIds.Count} in {priorMonthStr}";
                            trendDisclosure = $"trend not assessed: establishment coverage differs ({detail})";
                        }
                        else
                        {
                            var priorHeadcount = priorRows.Sum(r => r.EmployeeCount!.Value);
                            var delta = totalHeadcount - priorHeadcount;
                            var priorMonthStr = priorMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture);
                            trendDisclosure = $"{delta:+0;-0;0} vs {priorMonthStr}: {priorHeadcount}";
                        }
                    }
                }

                var periodStr = $"{latestMonthStr} ({trendDisclosure}){unparseableSuffix}";
                list.Add(MetricResult.Ok("Employee count (EPFO) + trend", (decimal)totalHeadcount, MetricUnit.Count,
                    periodStr, "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));
            }

            // H5: PF contribution per recorded EPFO employee (latest month)
            if (!h3Ok || !h4Ok)
            {
                var reason = !h4Ok
                    ? "Latest-month employee count is insufficient"
                    : "Latest-month contribution amount is insufficient";
                list.Add(MetricResult.Insufficient("PF contribution per recorded EPFO employee (latest month)", MetricUnit.Rupees,
                    reason, "EpfoContribution.ContributionAmountCrore", "EpfoContribution.EmployeeCount"));
            }
            else if (h4Headcount!.Value <= 0)
            {
                list.Add(MetricResult.Insufficient("PF contribution per recorded EPFO employee (latest month)", MetricUnit.Rupees,
                    "Latest-month employee count is 0",
                    "EpfoContribution.ContributionAmountCrore", "EpfoContribution.EmployeeCount"));
            }
            else
            {
                var inrPerEmp = Math.Round((h3AmountCr!.Value * 10_000_000m) / h4Headcount.Value, 0);
                var periodStr = $"{latestMonthStr} (₹{h3AmountCr.Value:0.##} Cr across {h4Headcount.Value} employees; proxy only, not salary){unparseableSuffix}";
                list.Add(MetricResult.Ok("PF contribution per recorded EPFO employee (latest month)", inrPerEmp, MetricUnit.Rupees,
                    periodStr, "EpfoContribution.ContributionAmountCrore", "EpfoContribution.EmployeeCount"));
            }
        }

        // ── H6: Revenue per EPFO employee ──
        // Aligned strictly to the same financial year (Apr (Y-1) to Mar Y).
        var standalone = model.Financials.Standalone.Where(y => y.Revenue is not null).OrderBy(y => y.FinancialYear).ToList();
        var consolidated = model.Financials.Consolidated.Where(y => y.Revenue is not null).OrderBy(y => y.FinancialYear).ToList();
        var latestFy = standalone.Count > 0 ? standalone[^1] : (consolidated.Count > 0 ? consolidated[^1] : null);

        if (latestFy is null)
        {
            list.Add(MetricResult.Insufficient("Revenue per EPFO employee", MetricUnit.Crore,
                "No revenue data on record",
                "FinancialYearData.Revenue", "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));
        }
        else
        {
            var fyYear = latestFy.FinancialYear;
            var fyStart = new DateOnly(fyYear - 1, 4, 1);
            var fyEnd = new DateOnly(fyYear, 3, 31);
            var matchingGroups = monthGroups.Where(g => g.Key >= fyStart && g.Key <= fyEnd).ToList();

            if (matchingGroups.Count == 0)
            {
                list.Add(MetricResult.Insufficient("Revenue per EPFO employee", MetricUnit.Crore,
                    $"No EPFO contribution records found in matching financial year (FY{fyYear}: Apr {fyYear - 1} \u2013 Mar {fyYear})",
                    "FinancialYearData.Revenue", "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));
            }
            else
            {
                var matchedGroup = matchingGroups[^1];
                var matchedMonth = matchedGroup.Key;
                var matchedRows = matchedGroup.Select(x => x.Contrib).ToList();
                var matchedMonthStr = matchedMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture);

                var missingMatchedHeadcount = matchedRows.Count(r => r.EmployeeCount is null);
                if (missingMatchedHeadcount > 0)
                {
                    list.Add(MetricResult.Insufficient("Revenue per EPFO employee", MetricUnit.Crore,
                        $"Incomplete data: {missingMatchedHeadcount} of {matchedRows.Count} establishment records in FY{fyYear} matching month ({matchedMonthStr}) missing EmployeeCount",
                        "FinancialYearData.Revenue", "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));
                }
                else
                {
                    var fyHeadcount = matchedRows.Sum(r => r.EmployeeCount!.Value);
                    if (fyHeadcount <= 0)
                    {
                        list.Add(MetricResult.Insufficient("Revenue per EPFO employee", MetricUnit.Crore,
                            $"EPFO employee count in matching FY{fyYear} ({matchedMonthStr}) is 0",
                            "FinancialYearData.Revenue", "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));
                    }
                    else
                    {
                        var revPerEmp = Math.Round(latestFy.Revenue!.Value / fyHeadcount, 2);
                        var periodStr = $"FY{fyYear} revenue (₹{latestFy.Revenue!.Value:N2} Cr) vs {matchedMonthStr} EPFO headcount ({fyHeadcount} employees) (EPFO headcount \u2260 total headcount)";
                        list.Add(MetricResult.Ok("Revenue per EPFO employee", revPerEmp, MetricUnit.Crore,
                            periodStr, "FinancialYearData.Revenue", "EpfoContribution.EmployeeCount", "EpfoContribution.WageMonth"));
                    }
                }
            }
        }

        // ── H7: Establishment count + locations + flags ──
        if (establishments.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Establishment count + locations + flags", MetricUnit.Count,
                "No EPFO establishment records on file",
                "EpfoEstablishment.WorkingStatus", "EpfoEstablishment.City", "EpfoEstablishment.Flags"));
        }
        else
        {
            var liveCount = establishments.Count(e => IsLiveEpfoStatus(e.WorkingStatus));
            var closedCount = establishments.Count(e => IsClosedEpfoStatus(e.WorkingStatus));
            var unknownStatusCount = establishments.Count - liveCount - closedCount;

            var validCityEstablishments = establishments.Where(e => IsValidEpfoCity(e.City)).ToList();
            var distinctCities = validCityEstablishments
                .Select(e => e.City!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var unknownCityCount = establishments.Count - validCityEstablishments.Count;

            var flaggedCount = establishments.Count(e => HasSubstantiveEpfoFlag(e.Flags));

            var statusDesc = unknownStatusCount > 0
                ? $"{liveCount} live, {closedCount} closed, {unknownStatusCount} unknown of {establishments.Count} establishment(s)"
                : $"{liveCount} live, {closedCount} closed of {establishments.Count} establishment(s)";

            var locDesc = unknownCityCount > 0
                ? $"{distinctCities.Count} location(s) ({unknownCityCount} unknown city)"
                : $"{distinctCities.Count} location(s)";

            var periodStr = $"{statusDesc} across {locDesc}; {flaggedCount} flagged";

            list.Add(MetricResult.Ok("Establishment count + locations + flags", (decimal)liveCount, MetricUnit.Count,
                periodStr, "EpfoEstablishment.WorkingStatus", "EpfoEstablishment.City", "EpfoEstablishment.Flags"));
        }

        return new MetricGroup("EPFO / labour", list);
    }

    private static readonly string[] EpfoWageMonthFormats =
    [
        "MMM, yyyy", "MMMM, yyyy", "MMM yyyy", "MMMM yyyy", "MMM-yyyy", "MMMM-yyyy", "yyyy-MM", "MM-yyyy", "MM/yyyy", "yyyy/MM"
    ];

    /// <summary>Normalises free-text WageMonth to the 1st of the month anchor DateOnly using an invariant allow-list of month formats.
    /// Ambiguous day-level strings or unsupported formats return false and must be handled fail-closed.</summary>
    public static bool TryParseWageMonth(string? wageMonth, out DateOnly monthAnchor)
    {
        monthAnchor = default;
        if (string.IsNullOrWhiteSpace(wageMonth)) return false;
        var text = wageMonth.Trim();
        foreach (var format in EpfoWageMonthFormats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                monthAnchor = new DateOnly(parsed.Year, parsed.Month, 1);
                return true;
            }
        }
        return false;
    }

    /// <summary>Approved status vocabulary for live EPFO establishments.
    /// Explicitly guards against substring collisions such as 'NOT LIVE'.</summary>
    public static bool IsLiveEpfoStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.Trim().ToUpperInvariant();
        if (s.Contains("NOT LIVE") || s.Contains("CLOSED") || s.Contains("INACTIVE") || s.Contains("DE-REGISTERED") || s.Contains("DEREGISTERED") || s.Contains("SUSPENDED"))
            return false;
        return s.Contains("LIVE") || s.Contains("WORKING") || s.Contains("ACTIVE");
    }

    /// <summary>Approved status vocabulary for closed/inactive EPFO establishments.</summary>
    public static bool IsClosedEpfoStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.Trim().ToUpperInvariant();
        return s.Contains("NOT LIVE") || s.Contains("CLOSED") || s.Contains("INACTIVE") || s.Contains("DE-REGISTERED") || s.Contains("DEREGISTERED") || s.Contains("SUSPENDED");
    }

    private static readonly HashSet<string> CleanEpfoFlagValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "-", "--", "---", "N/A", "NA", "NONE", "NIL", "NO", "NOT APPLICABLE", "NULL"
    };

    /// <summary>Filters out null, whitespace, and placeholder tokens from EPFO establishment flags.</summary>
    public static bool HasSubstantiveEpfoFlag(string? flags)
    {
        if (string.IsNullOrWhiteSpace(flags)) return false;
        var trimmed = flags.Trim();
        return !CleanEpfoFlagValues.Contains(trimmed);
    }

    private static readonly HashSet<string> PlaceholderEpfoCities = new(StringComparer.OrdinalIgnoreCase)
    {
        "-", "--", "---", "N/A", "NA", "NONE", "UNKNOWN", "NOT AVAILABLE", "NOT APPLICABLE", "NULL"
    };

    /// <summary>Counts only genuine, non-placeholder city values as valid establishment locations.</summary>
    public static bool IsValidEpfoCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return false;
        return !PlaceholderEpfoCities.Contains(city.Trim());
    }

    // ── Section J: Peer comparison analytics (Issue #63 / D8, docs/analytics-catalogue.json §J) ──

    public sealed record PeerMetricDefinition(string CanonicalName, MetricUnit Unit, PeerMetricDirection Direction);

    private static readonly System.Text.RegularExpressions.Regex MultiWhitespaceRegex =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string NormalizePeerMetricName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        var collapsed = MultiWhitespaceRegex.Replace(trimmed, " ");
        return collapsed.ToUpperInvariant();
    }

    public static string NormalizeCin(string? cin)
    {
        if (string.IsNullOrWhiteSpace(cin)) return "";
        return cin.Trim().ToUpperInvariant();
    }

    public static string NormalizeLegalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.Trim();
        var collapsed = MultiWhitespaceRegex.Replace(trimmed, " ");
        return collapsed.ToUpperInvariant();
    }

    private static readonly Dictionary<string, PeerMetricDefinition> PeerMetricAllowList = new(StringComparer.Ordinal)
    {
        // Margins & Returns
        ["EBITDA MARGIN (%)"] = new("EBITDA Margin (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["NET PROFIT MARGIN (%)"] = new("Net Profit Margin (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["OPERATING PROFIT MARGIN (%)"] = new("Operating Profit Margin (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["PBIT MARGIN (%)"] = new("PBIT Margin (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["RETURN ON NET WORTH (%)"] = new("Return on Net Worth (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["RETURN ON CAPITAL EMPLOYED (%)"] = new("Return on Capital Employed (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["RETURN ON ASSETS (%)"] = new("Return on Assets (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["ROE (%)"] = new("ROE (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["ROCE (%)"] = new("ROCE (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["ROA (%)"] = new("ROA (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["SALES GROWTH (%)"] = new("Sales Growth (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["REVENUE GROWTH (%)"] = new("Revenue Growth (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),
        ["NET PROFIT GROWTH (%)"] = new("Net Profit Growth (%)", MetricUnit.Percent, PeerMetricDirection.HigherIsBetter),

        // Working Capital & Turnover Days
        ["DEBTORS / SALES (DAYS)"] = new("Debtors / Sales (Days)", MetricUnit.Days, PeerMetricDirection.LowerIsBetter),
        ["INVENTORY / SALES (DAYS)"] = new("Inventory / Sales (Days)", MetricUnit.Days, PeerMetricDirection.LowerIsBetter),
        ["PAYABLES / SALES (DAYS)"] = new("Payables / Sales (Days)", MetricUnit.Days, PeerMetricDirection.LowerIsBetter),
        ["CASH CONVERSION CYCLE (DAYS)"] = new("Cash Conversion Cycle (Days)", MetricUnit.Days, PeerMetricDirection.LowerIsBetter),

        // Liquidity & Solvency Ratios
        ["CURRENT RATIO"] = new("Current Ratio", MetricUnit.Ratio, PeerMetricDirection.HigherIsBetter),
        ["QUICK RATIO"] = new("Quick Ratio", MetricUnit.Ratio, PeerMetricDirection.HigherIsBetter),
        ["INTEREST COVERAGE RATIO"] = new("Interest Coverage Ratio", MetricUnit.Ratio, PeerMetricDirection.HigherIsBetter),
        ["DEBT / EQUITY"] = new("Debt / Equity", MetricUnit.Ratio, PeerMetricDirection.LowerIsBetter),
        ["DEBT/EQUITY"] = new("Debt / Equity", MetricUnit.Ratio, PeerMetricDirection.LowerIsBetter),
        ["DEBT RATIO"] = new("Debt Ratio", MetricUnit.Ratio, PeerMetricDirection.LowerIsBetter),
        ["SALES / NET FIXED ASSETS"] = new("Sales / Net Fixed Assets", MetricUnit.Ratio, PeerMetricDirection.HigherIsBetter),
        ["TOTAL DEBT / NET WORTH"] = new("Total Debt / Net Worth", MetricUnit.Ratio, PeerMetricDirection.LowerIsBetter),
        ["ASSET TURNOVER RATIO"] = new("Asset Turnover Ratio", MetricUnit.Ratio, PeerMetricDirection.HigherIsBetter)
    };

    public static bool TryGetPeerMetricDefinition(string? rawName, out PeerMetricDefinition def)
    {
        var key = NormalizePeerMetricName(rawName);
        if (!string.IsNullOrEmpty(key) && PeerMetricAllowList.TryGetValue(key, out var found))
        {
            def = found;
            return true;
        }
        def = null!;
        return false;
    }

    public static string SentimentFor(PeerMetricDirection dir, PeerPosition pos) => dir switch
    {
        PeerMetricDirection.HigherIsBetter => pos switch
        {
            PeerPosition.Above => "favourable",
            PeerPosition.Below => "adverse",
            PeerPosition.InLine => "neutral",
            _ => "unknown"
        },
        PeerMetricDirection.LowerIsBetter => pos switch
        {
            PeerPosition.Above => "adverse",
            PeerPosition.Below => "favourable",
            PeerPosition.InLine => "neutral",
            _ => "unknown"
        },
        _ => pos == PeerPosition.InLine ? "neutral" : "unknown"
    };

    /// <summary>Section J — Peer comparison analytics (Issue #63 / D8, docs/analytics-catalogue.json §J).
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup PeerComparisonMetrics(Dossier.DossierModel model)
    {
        var list = new List<MetricResult>();
        var coverCin = NormalizeCin(model.Cover.Cin);

        // ── J2: Rank in source closest-peer list ──
        var closest = model.Financials.ClosestPeersList;
        if (closest.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Rank in source closest-peer list", MetricUnit.Count,
                "Closest peers block not reported in workbook",
                "PeerCompany.Rank", "PeerCompany.RevenueCrore"));
        }
        else if (closest.Any(p => !p.FinancialYear.HasValue))
        {
            list.Add(MetricResult.Insufficient("Rank in source closest-peer list", MetricUnit.Count,
                "Closest peers block contains missing financial year(s)",
                "PeerCompany.FinancialYear"));
        }
        else
        {
            var distinctFys = closest.Select(p => p.FinancialYear!.Value).Distinct().ToList();
            if (distinctFys.Count > 1)
            {
                list.Add(MetricResult.Insufficient("Rank in source closest-peer list", MetricUnit.Count,
                    $"Closest peers block contains inconsistent financial years ({string.Join(", ", distinctFys)})",
                    "PeerCompany.FinancialYear"));
            }
            else
            {
                var peerFy = distinctFys[0];
                var n = closest.Count;
                var ranks = closest.Select(p => p.Rank).ToList();
                if (ranks.Any(r => r < 1 || r > n) || ranks.Distinct().Count() != n)
                {
                    list.Add(MetricResult.Insufficient("Rank in source closest-peer list", MetricUnit.Count,
                        $"Closest peers block has invalid or duplicate ranks (expected 1..{n})",
                        "PeerCompany.Rank"));
                }
                else
                {
                    var companyCin = coverCin;
                    PeerCompany? matchedSelf = null;
                    string? matchFailure = null;

                    if (!string.IsNullOrEmpty(companyCin))
                    {
                        var matches = closest.Where(p => NormalizeCin(p.Cin) == companyCin).ToList();
                        if (matches.Count == 1)
                        {
                            matchedSelf = matches[0];
                        }
                        else if (matches.Count > 1)
                        {
                            matchFailure = $"Duplicate CIN matches in closest peers list ({companyCin})";
                        }
                        else
                        {
                            matchFailure = "Company CIN not found in closest peers list";
                        }
                    }
                    else
                    {
                        var companyName = NormalizeLegalName(model.Cover.CompanyName);
                        if (string.IsNullOrEmpty(companyName))
                        {
                            matchFailure = "Company CIN and legal name are unavailable for matching";
                        }
                        else
                        {
                            var matches = closest.Where(p => NormalizeLegalName(p.LegalName) == companyName).ToList();
                            if (matches.Count == 1)
                            {
                                matchedSelf = matches[0];
                            }
                            else if (matches.Count > 1)
                            {
                                matchFailure = $"Duplicate company name matches in closest peers list ({companyName})";
                            }
                            else
                            {
                                matchFailure = "Company name not found in closest peers list";
                            }
                        }
                    }

                    if (matchFailure is not null)
                    {
                        list.Add(MetricResult.Insufficient("Rank in source closest-peer list", MetricUnit.Count,
                            matchFailure,
                            "PeerCompany.Cin", "PeerCompany.LegalName", "DossierCover.Cin", "DossierCover.CompanyName"));
                    }
                    else if (matchedSelf!.RevenueCrore is null)
                    {
                        list.Add(MetricResult.Insufficient("Rank in source closest-peer list", MetricUnit.Count,
                            "Company revenue not reported in closest peers list",
                            "PeerCompany.RevenueCrore"));
                    }
                    else
                    {
                        list.Add(MetricResult.Ok("Rank in source closest-peer list", (decimal)matchedSelf.Rank, MetricUnit.Count,
                            $"Rank {matchedSelf.Rank} of {n} closest peers by revenue (₹{matchedSelf.RevenueCrore:0.##} Cr, FY{peerFy})",
                            "PeerCompany.Cin", "PeerCompany.LegalName", "PeerCompany.Rank", "PeerCompany.RevenueCrore", "PeerCompany.FinancialYear"));
                    }
                }
            }
        }

        // ── J3: Count of peers in sample ──
        var peerMetrics = model.Financials.PeerComparison;
        if (peerMetrics.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Count of peers in sample", MetricUnit.Count,
                "No peer comparison records on file", "PeerComparisonMetric.PeerCount"));
        }
        else
        {
            var latestPeerFy = peerMetrics.Max(m => m.FinancialYear);
            var fyMetrics = peerMetrics.Where(m => m.FinancialYear == latestPeerFy).ToList();
            var countsWithVal = fyMetrics.Where(m => m.PeerCount.HasValue).Select(m => m.PeerCount!.Value).ToList();

            if (countsWithVal.Count == 0)
            {
                list.Add(MetricResult.Insufficient("Count of peers in sample", MetricUnit.Count,
                    $"Peer count metadata not reported for FY{latestPeerFy}",
                    "PeerComparisonMetric.PeerCount"));
            }
            else if (countsWithVal.Any(c => c <= 0))
            {
                list.Add(MetricResult.Insufficient("Count of peers in sample", MetricUnit.Count,
                    $"Invalid non-positive peer count metadata for FY{latestPeerFy}",
                    "PeerComparisonMetric.PeerCount"));
            }
            else
            {
                var distinctCounts = countsWithVal.Distinct().ToList();
                if (distinctCounts.Count > 1)
                {
                    list.Add(MetricResult.Insufficient("Count of peers in sample", MetricUnit.Count,
                        $"Inconsistent peer sample counts reported for FY{latestPeerFy} ({string.Join(", ", distinctCounts)})",
                        "PeerComparisonMetric.PeerCount"));
                }
                else
                {
                    var meta = fyMetrics[0];
                    var indSeg = !string.IsNullOrWhiteSpace(meta.Industry)
                        ? (!string.IsNullOrWhiteSpace(meta.Segment) ? $" ({meta.Industry} · {meta.Segment})" : $" ({meta.Industry})")
                        : "";
                    list.Add(MetricResult.Ok("Count of peers in sample", (decimal)distinctCounts[0], MetricUnit.Count,
                        $"FY{latestPeerFy}{indSeg}", "PeerComparisonMetric.PeerCount"));
                }
            }
        }

        // ── J1: Metric vs peer median (per metric, per FY) ──
        if (peerMetrics.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Metric vs peer median", MetricUnit.Unspecified,
                "No peer comparison records on file",
                "PeerComparisonMetric.CompanyValue", "PeerComparisonMetric.PeerMedianValue"));
        }
        else
        {
            var items = peerMetrics.Select(m =>
            {
                var isKnown = TryGetPeerMetricDefinition(m.MetricName, out var def);
                var groupKey = isKnown ? def.CanonicalName : NormalizePeerMetricName(m.MetricName);
                var rawTrimmed = m.MetricName?.Trim() ?? "";
                return new
                {
                    Raw = m,
                    IsKnown = isKnown,
                    Definition = def,
                    GroupKey = groupKey,
                    RawTrimmed = rawTrimmed,
                    Year = m.FinancialYear
                };
            });

            var grouped = items
                .GroupBy(x => (x.GroupKey, x.Year))
                .OrderByDescending(g => g.Key.Year)
                .ThenBy(g => g.Key.GroupKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var g in grouped)
            {
                var groupItems = g.ToList();
                var first = groupItems[0];
                var year = g.Key.Year;

                if (!first.IsKnown)
                {
                    var rawName = first.RawTrimmed;
                    list.Add(MetricResult.Insufficient($"{rawName} vs peer median", MetricUnit.Unspecified,
                        $"Unknown peer metric '{rawName}' — unit and direction not defined in catalogue allow-list",
                        "PeerComparisonMetric.CompanyValue", "PeerComparisonMetric.PeerMedianValue"));
                    continue;
                }

                var def = first.Definition!;
                var canonicalLabel = $"{def.CanonicalName} vs peer median";

                if (groupItems.Count > 1)
                {
                    list.Add(MetricResult.Insufficient(canonicalLabel, def.Unit,
                        $"Duplicate peer comparison records on file for '{def.CanonicalName}' in FY{year}",
                        "PeerComparisonMetric.CompanyValue", "PeerComparisonMetric.PeerMedianValue"));
                    continue;
                }

                var sample = groupItems[0].Raw;
                if (sample.CompanyValue is null)
                {
                    list.Add(MetricResult.Insufficient(canonicalLabel, def.Unit,
                        $"Company value not reported for '{def.CanonicalName}' in FY{year}",
                        "PeerComparisonMetric.CompanyValue", "PeerComparisonMetric.PeerMedianValue"));
                }
                else if (sample.PeerMedianValue is null)
                {
                    list.Add(MetricResult.Insufficient(canonicalLabel, def.Unit,
                        $"Peer median not reported for '{def.CanonicalName}' in FY{year}",
                        "PeerComparisonMetric.CompanyValue", "PeerComparisonMetric.PeerMedianValue"));
                }
                else
                {
                    var delta = sample.CompanyValue.Value - sample.PeerMedianValue.Value;
                    var pos = PeerComparisonDisplayRules.Compare(sample.CompanyValue, sample.PeerMedianValue);
                    var sentiment = SentimentFor(def.Direction, pos);
                    var periodStr = $"FY{year} (Company {sample.CompanyValue.Value:0.##} vs Median {sample.PeerMedianValue.Value:0.##}, {pos} / {sentiment})";

                    list.Add(MetricResult.Ok(canonicalLabel, delta, def.Unit, periodStr,
                        "PeerComparisonMetric.CompanyValue", "PeerComparisonMetric.PeerMedianValue"));
                }
            }
        }

        return new MetricGroup("Peer comparison", list);
    }

    /// <summary>Section K — Capital reconciliation analytics (Issue #98 / K1, docs/analytics-catalogue.json §K).
    /// Extends the §B10 cross-source-discrepancy pattern to a second independently-sourced, already-typed
    /// pair of figures: the "About the Company" identity snapshot vs the latest standalone Balance Sheet.
    /// A non-zero difference is a genuine cross-period comparison, never a confirmed error — capital can
    /// legitimately change after the FY the balance sheet reports. "About the Company" is a hard-required
    /// sheet (ingestion fails without it), so only the standalone-financials side can genuinely be
    /// "sheet not in this upload" rather than "field blank" — <see cref="SheetCoverage.WasAbsent"/> is used
    /// only on that side. Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup CapitalReconciliationMetrics(Dossier.DossierModel model)
    {
        const string label = "MCA master-data paid-up capital vs standalone share capital";
        var list = new List<MetricResult>();
        var profilePaidUp = model.Corporate.PaidUpCapital;
        var latest = model.Financials.Latest;

        if (profilePaidUp is null)
        {
            list.Add(MetricResult.Insufficient(label, MetricUnit.Crore,
                "Paid-up capital not reported on the About the Company sheet",
                "CompanyProfile.PaidUpCapital", "FinancialYearData.ShareCapital"));
        }
        else if (latest?.ShareCapital is null)
        {
            var reason = model.SourceCoverage.WasAbsent(SheetAliases.StandaloneFinancialData)
                ? "Standalone Financial Data sheet not in this upload"
                : latest is null
                    ? "No standalone financial year data on record"
                    : $"FY{latest.FinancialYear}: Share Capital row not reported on the Standalone Financial Data sheet";
            list.Add(MetricResult.Insufficient(label, MetricUnit.Crore, reason,
                "CompanyProfile.PaidUpCapital", "FinancialYearData.ShareCapital"));
        }
        else
        {
            // Exact subtraction — both source columns are decimal(18,4), and rounding here would lose
            // real parsed precision. MetricResult.DisplayValue() rounds only for on-screen formatting.
            var diff = profilePaidUp.Value - latest.ShareCapital.Value;
            var asOfSuffix = model.Cover.SourceSnapshotDate is { } snap ? $" as of {snap:d MMM yyyy}" : "";
            var period = $"FY{latest.FinancialYear} — MCA master-data snapshot ₹{profilePaidUp.Value:0.##} Cr{asOfSuffix} " +
                $"vs standalone share capital ₹{latest.ShareCapital.Value:0.##} Cr";
            list.Add(MetricResult.Ok(label, diff, MetricUnit.Crore, period,
                "CompanyProfile.PaidUpCapital", "FinancialYearData.ShareCapital"));
        }

        return new MetricGroup("Capital reconciliation", list);
    }

    /// <summary>Section E — Related-party-transaction analytics (Issue #65 / D10, docs/analytics-catalogue.json §E).
    /// The "Related Party Transactions" sheet is a <see cref="Excel.SheetAliases.TrackedOptionalSheets"/> entry
    /// (absent from a large share of the wider portfolio — see <see cref="Excel.Parsers.RelatedPartyTransactionsParser"/>'s
    /// doc comment), so an empty result is worded as "sheet not in this upload" only when
    /// <see cref="SheetCoverage.WasAbsent"/> confirms that; a present-but-empty sheet is a distinct, legitimate
    /// outcome ("no related-party dealings that period"). E1/E5 bucket by the row's own
    /// <see cref="RelatedPartyTransaction.FinancialYearEnding"/> year; rows with no reported year cannot be
    /// placed in a year bucket and are excluded (noted in each bucket's period text), mirroring
    /// <c>ShareholdingMetrics</c>' handling of undated pattern rows. E3/E4 are scoped to the latest reported FY
    /// only, matching the established C3/C4/C6/K1 "latest-period-only" convention — summing a transaction-type
    /// or subsidiary total across multiple years would misrepresent a single period's concentration as a
    /// multi-year one. E6 first intersects E1's clean per-FY RPT totals with reported Revenue years, then
    /// runs both series through <see cref="AddCagrCore"/> (the same calendar-bounded window/negative-base
    /// algorithm A2.1 uses) restricted to that shared year set — since the window it picks depends only on
    /// which years are present, giving both calls the identical year set guarantees the identical (base,
    /// end) pair, so the two CAGRs are always compared over the same span (never independently-windowed).
    /// Pure computation over <paramref name="model"/>.</summary>
    public static MetricGroup RelatedPartyTransactionMetrics(Dossier.DossierModel model)
    {
        var list = new List<MetricResult>();
        var rpts = model.Corporate.RelatedPartyTransactions;

        string AbsentOrEmptyReason() => model.SourceCoverage.WasAbsent(SheetAliases.RelatedPartyTransactions)
            ? "Related Party Transactions sheet not in this upload"
            : "No related-party-transaction rows reported";

        if (rpts.Count == 0)
        {
            var reason = AbsentOrEmptyReason();
            list.Add(MetricResult.Insufficient("Total RPT value per FY", MetricUnit.Crore, reason,
                "RelatedPartyTransaction.AmountCrore", "RelatedPartyTransaction.FinancialYearEnding"));
            list.Add(MetricResult.Insufficient("RPT as % of revenue", MetricUnit.Percent, reason,
                "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
            list.Add(MetricResult.Insufficient("RPT by transaction type", MetricUnit.Crore, reason,
                "RelatedPartyTransaction.TransactionType", "RelatedPartyTransaction.AmountCrore"));
            list.Add(MetricResult.Insufficient("RPT to subsidiaries", MetricUnit.Crore, reason,
                "RelatedPartyTransaction.RelationshipRaw", "RelatedPartyTransaction.AmountCrore"));
            list.Add(MetricResult.Insufficient("Distinct related entities transacted per FY", MetricUnit.Count, reason,
                "RelatedPartyTransaction.EntityNameNormalized", "RelatedPartyTransaction.FinancialYearEnding"));
            list.Add(MetricResult.Insufficient("RPT growing faster than revenue", MetricUnit.Count, reason,
                "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
            return new MetricGroup("Related-party transactions", list);
        }

        var dated = rpts.Where(r => r.FinancialYearEnding is not null).ToList();
        var undatedCount = rpts.Count - dated.Count;
        var undatedNote = undatedCount > 0 ? $" ({undatedCount} row(s) with no reported Financial Year Ending excluded)" : "";
        const string noFyReason = "No related-party-transaction row has a reported Financial Year Ending";

        // E1: Total RPT value per FY — fails closed per-FY on any missing Amount in that FY's population
        // (the "component sum requires EVERY component" gate, per C3/C6).
        var rptByYear = new Dictionary<int, decimal>();
        if (dated.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Total RPT value per FY", MetricUnit.Crore, noFyReason,
                "RelatedPartyTransaction.AmountCrore", "RelatedPartyTransaction.FinancialYearEnding"));
        }
        else
        {
            foreach (var g in dated.GroupBy(r => r.FinancialYearEnding!.Value.Year).OrderBy(g => g.Key))
            {
                var rows = g.ToList();
                var missing = rows.Count(r => r.AmountCrore is null);
                if (missing > 0)
                {
                    list.Add(MetricResult.Insufficient($"Total RPT value per FY (FY{g.Key})", MetricUnit.Crore,
                        $"FY{g.Key}: {missing} of {rows.Count} transaction(s) have no reported Amount — the sum would be incomplete",
                        "RelatedPartyTransaction.AmountCrore", "RelatedPartyTransaction.FinancialYearEnding"));
                }
                else
                {
                    var sum = rows.Sum(r => r.AmountCrore!.Value);
                    rptByYear[g.Key] = sum;
                    list.Add(MetricResult.Ok($"Total RPT value per FY (FY{g.Key})", sum, MetricUnit.Crore,
                        $"FY{g.Key} — {rows.Count} transaction(s){undatedNote}",
                        "RelatedPartyTransaction.AmountCrore", "RelatedPartyTransaction.FinancialYearEnding"));
                }
            }
        }

        // E2: RPT as % of revenue — matched by calendar year (FinancialYearEnding.Year == FinancialYearData.FinancialYear);
        // only computed for years with both a clean E1 total and a non-zero reported Revenue.
        if (dated.Count == 0)
        {
            list.Add(MetricResult.Insufficient("RPT as % of revenue", MetricUnit.Percent, noFyReason,
                "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
        }
        else
        {
            foreach (var year in dated.Select(r => r.FinancialYearEnding!.Value.Year).Distinct().OrderBy(y => y))
            {
                if (!rptByYear.TryGetValue(year, out var rptTotal))
                {
                    list.Add(MetricResult.Insufficient($"RPT as % of revenue (FY{year})", MetricUnit.Percent,
                        $"FY{year}: total RPT value could not be computed (see 'Total RPT value per FY')",
                        "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
                    continue;
                }
                var fy = model.Financials.Standalone.FirstOrDefault(f => f.FinancialYear == year);
                if (fy?.Revenue is null)
                {
                    list.Add(MetricResult.Insufficient($"RPT as % of revenue (FY{year})", MetricUnit.Percent,
                        $"FY{year}: Revenue not reported on the Standalone Financial Data sheet",
                        "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
                }
                else if (fy.Revenue.Value == 0m)
                {
                    list.Add(MetricResult.Insufficient($"RPT as % of revenue (FY{year})", MetricUnit.Percent,
                        $"FY{year}: Revenue is zero — percentage undefined",
                        "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
                }
                else
                {
                    var pct = Math.Round(rptTotal / fy.Revenue.Value * 100m, 1);
                    list.Add(MetricResult.Ok($"RPT as % of revenue (FY{year})", pct, MetricUnit.Percent,
                        $"FY{year} — RPT ₹{rptTotal:0.##} Cr / Revenue ₹{fy.Revenue.Value:0.##} Cr",
                        "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
                }
            }
        }

        // E3 / E4: point-in-time splits, scoped to the latest reported Financial Year Ending.
        if (dated.Count == 0)
        {
            list.Add(MetricResult.Insufficient("RPT by transaction type", MetricUnit.Crore, noFyReason,
                "RelatedPartyTransaction.TransactionType", "RelatedPartyTransaction.AmountCrore"));
            list.Add(MetricResult.Insufficient("RPT to subsidiaries", MetricUnit.Crore, noFyReason,
                "RelatedPartyTransaction.RelationshipRaw", "RelatedPartyTransaction.AmountCrore"));
        }
        else
        {
            var latestYear = dated.Max(r => r.FinancialYearEnding!.Value.Year);
            var latestRows = dated.Where(r => r.FinancialYearEnding!.Value.Year == latestYear).ToList();
            var missingAmt = latestRows.Count(r => r.AmountCrore is null);

            if (missingAmt > 0)
            {
                var reason = $"FY{latestYear}: {missingAmt} of {latestRows.Count} transaction(s) have no reported Amount — the sum would be incomplete";
                list.Add(MetricResult.Insufficient("RPT by transaction type", MetricUnit.Crore, reason,
                    "RelatedPartyTransaction.TransactionType", "RelatedPartyTransaction.AmountCrore"));
                list.Add(MetricResult.Insufficient("RPT to subsidiaries", MetricUnit.Crore, reason,
                    "RelatedPartyTransaction.RelationshipRaw", "RelatedPartyTransaction.AmountCrore"));
            }
            else
            {
                // E3: grouped by the sheet's own TransactionType. A blank type is excluded from the split
                // rather than given a fake "Unspecified" bucket (C4's convention); insufficient only when
                // EVERY row in the FY lacks a type.
                var typed = latestRows.Where(r => !string.IsNullOrWhiteSpace(r.TransactionType)).ToList();
                if (typed.Count == 0)
                {
                    list.Add(MetricResult.Insufficient("RPT by transaction type", MetricUnit.Crore,
                        $"FY{latestYear}: {latestRows.Count} transaction(s), none with a reported TransactionType",
                        "RelatedPartyTransaction.TransactionType", "RelatedPartyTransaction.AmountCrore"));
                }
                else
                {
                    var untyped = latestRows.Count - typed.Count;
                    var exclusionNote = untyped > 0 ? $" ({untyped} with no reported type excluded)" : "";
                    foreach (var g in typed.GroupBy(r => r.TransactionType!.Trim())
                        .OrderByDescending(g => g.Sum(r => r.AmountCrore!.Value)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var sum = g.Sum(r => r.AmountCrore!.Value);
                        list.Add(MetricResult.Ok($"RPT by transaction type ({g.Key})", sum, MetricUnit.Crore,
                            $"FY{latestYear} — {g.Count()} of {latestRows.Count} transaction(s){exclusionNote}",
                            "RelatedPartyTransaction.TransactionType", "RelatedPartyTransaction.AmountCrore"));
                    }
                }

                // E4: RPT to subsidiaries — RelationshipRaw containing "Subsidiary" (case-insensitive
                // substring match against this free-text MCA-sourced column, e.g. "Subsidiary",
                // "Wholly-owned Subsidiary"). Every row in latestRows already has a known Amount (the
                // missingAmt gate above), so a zero-match result is a genuine Ok(0), not "insufficient."
                var subsidiaryRows = latestRows.Where(r => r.RelationshipRaw?.Contains("Subsidiary", StringComparison.OrdinalIgnoreCase) == true).ToList();
                if (subsidiaryRows.Count == 0)
                {
                    list.Add(MetricResult.Ok("RPT to subsidiaries", 0m, MetricUnit.Crore,
                        $"FY{latestYear} — no transactions with a subsidiary-labelled relationship",
                        "RelatedPartyTransaction.RelationshipRaw", "RelatedPartyTransaction.AmountCrore"));
                }
                else
                {
                    var sum = subsidiaryRows.Sum(r => r.AmountCrore!.Value);
                    list.Add(MetricResult.Ok("RPT to subsidiaries", sum, MetricUnit.Crore,
                        $"FY{latestYear} — {subsidiaryRows.Count} of {latestRows.Count} transaction(s) with a subsidiary-labelled relationship",
                        "RelatedPartyTransaction.RelationshipRaw", "RelatedPartyTransaction.AmountCrore"));
                }
            }
        }

        // E5: Distinct related entities transacted per FY. EntityNameNormalized is always populated by the
        // parser (derived from the required EntityName column), so no missing-value gate applies here.
        if (dated.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Distinct related entities transacted per FY", MetricUnit.Count, noFyReason,
                "RelatedPartyTransaction.EntityNameNormalized", "RelatedPartyTransaction.FinancialYearEnding"));
        }
        else
        {
            foreach (var g in dated.GroupBy(r => r.FinancialYearEnding!.Value.Year).OrderBy(g => g.Key))
            {
                var distinctCount = g.Select(r => r.EntityNameNormalized).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                list.Add(MetricResult.Ok($"Distinct related entities transacted per FY (FY{g.Key})", distinctCount, MetricUnit.Count,
                    $"FY{g.Key} — {g.Count()} transaction(s){undatedNote}",
                    "RelatedPartyTransaction.EntityNameNormalized", "RelatedPartyTransaction.FinancialYearEnding"));
            }
        }

        // E6: RPT growing faster than revenue — both CAGRs MUST be computed over the exact same (base,
        // end) FY pair, or the flag compares two unrelated spans (e.g. RPT's most recent 1-year jump
        // against revenue's full 3-year run) and can assert the wrong direction entirely (PR #103 review).
        // Fix: build the shared "clean RPT total (E1) AND reported Revenue" FY intersection first, then
        // hand AddCagrCore two series restricted to that IDENTICAL set of years — its calendar-bounded
        // window selection is then guaranteed to resolve to the same (base, end) pair for both, since it
        // depends only on which years are present, not on their values.
        var revenueByYear = model.Financials.Standalone.Where(f => f.Revenue is not null)
            .ToDictionary(f => f.FinancialYear, f => f.Revenue!.Value);
        var commonYears = rptByYear.Keys.Where(revenueByYear.ContainsKey).OrderBy(y => y).ToList();

        if (commonYears.Count < 2)
        {
            list.Add(MetricResult.Insufficient("RPT growing faster than revenue", MetricUnit.Count,
                "Fewer than 2 financial years have both a clean RPT total and a reported Revenue figure — a CAGR comparison requires a shared FY window",
                "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
        }
        else
        {
            var scratch = new List<MetricResult>();
            var rptSeries = commonYears.Select(y => (Year: y, Value: rptByYear[y])).ToList();
            var revenueSeries = commonYears.Select(y => (Year: y, Value: revenueByYear[y])).ToList();
            var rptCagr = AddCagrCore(scratch, rptSeries, "RPT CAGR (internal)", allowNegativeBaseFallback: false, "RelatedPartyTransaction.AmountCrore");
            var revenueCagr = AddCagrCore(scratch, revenueSeries, "Revenue CAGR (internal)", allowNegativeBaseFallback: false, "FinancialYearData.Revenue");

            if (rptCagr is null || revenueCagr is null)
            {
                list.Add(MetricResult.Insufficient("RPT growing faster than revenue", MetricUnit.Count,
                    "RPT CAGR or Revenue CAGR could not be computed within the shared FY window (no base year within 3 calendar years of the latest shared FY, or a zero/negative base or end value)",
                    "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
            }
            else
            {
                var flag = rptCagr.Value > revenueCagr.Value;
                list.Add(MetricResult.Ok("RPT growing faster than revenue", flag ? 1m : 0m, MetricUnit.Count,
                    $"RPT CAGR {rptCagr.Value:0.#}% vs Revenue CAGR {revenueCagr.Value:0.#}% (shared FY window)",
                    "RelatedPartyTransaction.AmountCrore", "FinancialYearData.Revenue"));
            }
        }

        return new MetricGroup("Related-party transactions", list);
    }
}
