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
            ChargeRegisterMetrics(model)
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

        // B1: Total open charge amount
        if (all.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Total open charge amount", MetricUnit.Crore,
                "No charge records on file", "RocCharge.CurrentAmount"));
        }
        else
        {
            list.Add(MetricResult.Ok("Total open charge amount", charges.TotalOpenAmount,
                MetricUnit.Crore, asOfStr, "RocCharge.CurrentAmount"));
        }

        // B2: Total satisfied charge amount
        if (all.Count == 0)
        {
            list.Add(MetricResult.Insufficient("Total satisfied charge amount", MetricUnit.Crore,
                "No charge records on file", "RocCharge.CurrentAmount"));
        }
        else
        {
            var totalSatAmount = satisfied.Sum(c => c.CurrentAmount ?? 0m);
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
        var totalOpen = charges.TotalOpenAmount;
        if (totalOpen == 0m)
        {
            list.Add(MetricResult.Insufficient("Top-3 lender concentration", MetricUnit.Percent,
                "Total open charge amount is zero", "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }
        else
        {
            var lconc = charges.LenderConcentration;
            var top3Amt = lconc.Take(3).Sum(c => c.RegisteredAmount);
            var top3Pct = Math.Round(top3Amt / totalOpen * 100m, 1);
            var periodNote = lconc.Count < 3 ? $"{asOfStr} ({lconc.Count} holders)" : asOfStr;
            list.Add(MetricResult.Ok("Top-3 lender concentration", top3Pct,
                MetricUnit.Percent, periodNote, "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }

        // B4b: Lender concentration (HHI)
        if (totalOpen == 0m)
        {
            list.Add(MetricResult.Insufficient("Lender concentration (HHI)", MetricUnit.Ratio,
                "Total open charge amount is zero", "RocCharge.LatestChargeHolderNormalized", "RocCharge.CurrentAmount"));
        }
        else
        {
            decimal hhi = 0m;
            foreach (var r in charges.LenderConcentration)
            {
                var share = r.RegisteredAmount / totalOpen;
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
            var unclassifiedAmt = open
                .Where(c => SecurityTypeLabels(c).Count == 0)
                .Sum(c => c.CurrentAmount ?? 0m);
            list.Add(MetricResult.Ok("Unclassified open charge amount", unclassifiedAmt,
                MetricUnit.Crore, asOfStr, "DossierComputations.SecurityTypeLabels", "RocCharge.CurrentAmount"));
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
                "No charge records on file", "RocChargeEvent.EventDate", "RocChargeEvent.ChargeAmount"));
            list.Add(MetricResult.Insufficient("Charges created in last 24 months", MetricUnit.Count,
                "No charge records on file", "RocChargeEvent.EventDate"));
            list.Add(MetricResult.Insufficient("Amount created in last 24 months", MetricUnit.Crore,
                "No charge records on file", "RocChargeEvent.EventDate", "RocChargeEvent.ChargeAmount"));
        }
        else
        {
            var creationEvents = all.SelectMany(c => c.Events)
                .Where(e => e.EventType == ChargeEventType.Creation && e.EventDate is not null)
                .ToList();
            var cutoff12 = asOfDate.AddYears(-1);
            var cutoff24 = asOfDate.AddYears(-2);

            var c12 = creationEvents.Where(e => e.EventDate!.Value >= cutoff12 && e.EventDate!.Value <= asOfDate).ToList();
            var c24 = creationEvents.Where(e => e.EventDate!.Value >= cutoff24 && e.EventDate!.Value <= asOfDate).ToList();

            var amt12 = c12.Sum(e => e.ChargeAmount ?? 0m);
            var amt24 = c24.Sum(e => e.ChargeAmount ?? 0m);

            list.Add(MetricResult.Ok("Charges created in last 12 months", c12.Count,
                MetricUnit.Count, $"trailing 12 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate"));
            list.Add(MetricResult.Ok("Amount created in last 12 months", amt12,
                MetricUnit.Crore, $"trailing 12 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate", "RocChargeEvent.ChargeAmount"));

            list.Add(MetricResult.Ok("Charges created in last 24 months", c24.Count,
                MetricUnit.Count, $"trailing 24 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate"));
            list.Add(MetricResult.Ok("Amount created in last 24 months", amt24,
                MetricUnit.Crore, $"trailing 24 months to {asOfDate:d MMM yyyy}", "RocChargeEvent.EventDate", "RocChargeEvent.ChargeAmount"));
        }

        // B8: Joint / consortium charge count
        var jointOrConsortium = open.Count(c => c.Events.Any(e => e.JointHolding == true || e.ConsortiumHolding == true));
        list.Add(MetricResult.Ok("Joint / consortium charge count", jointOrConsortium,
            MetricUnit.Count, asOfStr, "RocChargeEvent.JointHolding", "RocChargeEvent.ConsortiumHolding"));

        // B9: Charge-to-paid-up-capital ratio
        if (model.Corporate.PaidUpCapital is { } puc && puc > 0m)
        {
            var ratio = Math.Round(totalOpen / puc, 2);
            list.Add(MetricResult.Ok("Charge-to-paid-up-capital ratio", ratio,
                MetricUnit.Times, asOfStr, "RocCharge.CurrentAmount", "CompanyProfile.PaidUpCapital"));
        }
        else
        {
            list.Add(MetricResult.Insufficient("Charge-to-paid-up-capital ratio", MetricUnit.Times,
                "Paid-up capital not reported or zero", "RocCharge.CurrentAmount", "CompanyProfile.PaidUpCapital"));
        }

        // B10: Open charges vs balance-sheet borrowings
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
                var ratio = Math.Round(totalOpen / debt, 2);
                list.Add(MetricResult.Ok("Open charges vs balance-sheet borrowings", ratio,
                    MetricUnit.Times, $"FY{latestFy.FinancialYear}",
                    "RocCharge.CurrentAmount", "FinancialYearData.LongTermBorrowings", "FinancialYearData.ShortTermBorrowings"));
            }
        }

        // B11: Charge filing-lag median + negative-lag anomalies
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
        }
        else
        {
            var median = nonNegLags[nonNegLags.Count / 2];
            list.Add(MetricResult.Ok("Median charge filing lag", (decimal)median,
                MetricUnit.Days, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));
        }

        list.Add(MetricResult.Ok("Negative filing-lag anomalies", negLagEvents.Count,
            MetricUnit.Count, asOfStr, "RocChargeEvent.EventDate", "RocChargeEvent.FilingDate"));

        return new MetricGroup("Charge register", list);
    }
}
