using MCAROC_Analysis.Data.Entities;
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
        var groups = new List<MetricGroup>
        {
            ChargeRegisterMetrics(model),
            GstComplianceMetrics(model)
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
}
