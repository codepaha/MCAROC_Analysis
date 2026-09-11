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
            GstComplianceMetrics(model),
            LitigationMetrics(model),
            DirectorsMetrics(model),
            FinancialTrendMetrics(model)
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

    /// <summary>Section A — Financial trend &amp; leverage analytics (Issue #57 / D2,
    /// docs/analytics-catalogue.json §A, A2/A3). A1.x (the 16 source-reported ratios) is already
    /// surfaced verbatim by B1/#39's Ratios sub-tab — not re-modelled here, since <c>MetricResult</c>
    /// is for DERIVED values and the catalogue is explicit that those 16 are never recomputed.
    /// A4/A5 (cost structure, forex) are D9/#64's scope, not this one's.
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
}
