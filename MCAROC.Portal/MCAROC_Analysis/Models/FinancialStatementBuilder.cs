using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>Constructs comprehensive <see cref="FinancialStatementViewModel"/> instances interleaving
/// typed <see cref="FinancialYearData"/> properties and <see cref="FinancialFact"/> rows in standard
/// accounting statement order, with subtotals highlighted and year-inferred flags surfaced.</summary>
public static class FinancialStatementBuilder
{
    private record TemplateRow(string Label, Func<FinancialYearData, decimal?>? TypedGetter, bool IsSubtotal, bool IsHeader);

    private static readonly TemplateRow[] BalanceSheetTemplate =
    [
        new("Share Capital", f => f.ShareCapital, false, false),
        new("Reserves and Surplus", null, false, false),
        new("Money Received Against Share Warrants", null, false, false),
        new("Total Equity", f => f.NetWorth, true, false),
        new("Share Application Money Pending Allotment", null, false, false),
        new("Long Term Borrowings", f => f.LongTermBorrowings, false, false),
        new("Deferred Tax Liabilities (Net)", null, false, false),
        new("Other Long Term Liabilities", null, false, false),
        new("Long Term Provisions", null, false, false),
        new("Total Non-Current Liabilities", null, true, false),
        new("Short Term Borrowings", f => f.ShortTermBorrowings, false, false),
        new("Trade Payables", f => f.TradePayables, false, false),
        new("Other Current Liabilities", null, false, false),
        new("Short Term Provisions", null, false, false),
        new("Total Current Liabilities", f => f.CurrentLiabilities, true, false),
        new("Total Liabilities", null, true, false),
        new("Total Equity and Liabilities", null, true, false),
        new("Gross Fixed Assets", null, false, false),
        new("Less: Accumulated Depreciation", null, false, false),
        new("Net Fixed Assets", null, true, false),
        new("Capital Work-in-Progress", null, false, false),
        new("Non-Current Investments", null, false, false),
        new("Deferred Tax Assets (Net)", null, false, false),
        new("Long-Term Loans and Advances", null, false, false),
        new("Other Non-Current Assets", null, false, false),
        new("Total Non-Current Assets", null, true, false),
        new("Current Investments", null, false, false),
        new("Inventories", f => f.Inventory, false, false),
        new("Trade Receivables", f => f.TradeReceivables, false, false),
        new("Cash and Bank Balances", f => f.CashAndBank, false, false),
        new("Short-Term Loans and Advances", null, false, false),
        new("Other Current Assets", null, false, false),
        new("Total Current Assets", f => f.CurrentAssets, true, false),
        new("Total Assets", null, true, false),
    ];

    private static readonly TemplateRow[] PnlTemplate =
    [
        new("Net Revenue", f => f.Revenue, false, false),
        new("Revenue from Operations", f => f.Revenue, false, false),
        new("Other Income", f => f.OtherIncome, false, false),
        new("Total Revenue", null, true, false),
        new("Cost of Materials Consumed", null, false, false),
        new("Purchases of Stock-in-Trade", null, false, false),
        new("Changes in Inventories of Finished Goods, Work-in-Progress and Stock-in-Trade", null, false, false),
        new("Employee Benefit Expense", null, false, false),
        new("Employee Benefits Expense", null, false, false),
        new("Finance Costs", f => f.FinanceCost, false, false),
        new("Depreciation and Amortization Expense", null, false, false),
        new("Other Expenses", null, false, false),
        new("Total Expenses", null, true, false),
        new("Operating Profit ( EBITDA )", f => f.Ebitda, true, false),
        new("Profit Before Interest and Tax", f => f.Ebit, true, false),
        new("Profit Before Exceptional and Extraordinary Items and Tax", null, true, false),
        new("Exceptional Items", null, false, false),
        new("Profit Before Tax", f => f.Pbt, true, false),
        new("Tax Expense", null, false, false),
        new("Current Tax", null, false, false),
        new("Deferred Tax", null, false, false),
        new("Profit for the Period", f => f.Pat, true, false),
        new("Profit After Tax (PAT)", f => f.Pat, true, false),
        new("Other Comprehensive Income", null, false, false),
        new("Total Comprehensive Income", null, true, false),
    ];

    private static readonly TemplateRow[] CashFlowTemplate =
    [
        new("Net Cash Flows from / ( Used in ) Operating Activities", f => f.Cfo, true, false),
        new("Net Cash Flows from / ( Used in ) Investing Activities", f => f.Cfi, true, false),
        new("Net Cash Flows from / ( Used in ) Financing Activities", f => f.Cff, true, false),
        new("Net Increase / (Decrease) in Cash and Cash Equivalents", null, true, false),
        new("Cash and Cash Equivalents at Beginning of the Year", null, false, false),
        new("Cash and Cash Equivalents at End of the Year", null, true, false),
    ];

    public static FinancialStatementViewModel Build(
        string key,
        string title,
        FinancialStatementSection section,
        IReadOnlyList<FinancialYearData> standaloneYears,
        IReadOnlyList<FinancialYearData> consolidatedYears,
        IReadOnlyList<FinancialFact> facts,
        string unit = "₹ Crore",
        string? description = null)
    {
        var stdData = BuildViewData(section, standaloneYears, facts.Where(f => f.Basis == FinancialBasis.Standalone).ToList(), unit);
        var conData = BuildViewData(section, consolidatedYears, facts.Where(f => f.Basis == FinancialBasis.Consolidated).ToList(), unit);

        return new FinancialStatementViewModel(key, title, stdData, conData, description);
    }

    public static FinancialStatementViewModel BuildRatios(
        string key,
        string title,
        IReadOnlyList<FinancialYearData> standaloneYears,
        IReadOnlyList<FinancialYearData> consolidatedYears,
        IReadOnlyList<FinancialFact> facts,
        string? description = null)
    {
        var stdFacts = facts.Where(f => f.Basis == FinancialBasis.Standalone && f.Section == FinancialStatementSection.Ratios).ToList();
        var conFacts = facts.Where(f => f.Basis == FinancialBasis.Consolidated && f.Section == FinancialStatementSection.Ratios).ToList();

        var stdData = BuildRatiosViewData(standaloneYears, stdFacts);
        var conData = BuildRatiosViewData(consolidatedYears, conFacts);

        return new FinancialStatementViewModel(key, title, stdData, conData, description);
    }

    private static FinancialStatementViewData BuildViewData(
        FinancialStatementSection section,
        IReadOnlyList<FinancialYearData> yearDataList,
        IReadOnlyList<FinancialFact> sectionFacts,
        string unit)
    {
        var factsForSection = sectionFacts.Where(f => f.Section == section).ToList();

        var years = yearDataList.Select(y => y.FinancialYear)
            .Concat(factsForSection.Where(f => f.FinancialYear.HasValue).Select(f => f.FinancialYear!.Value))
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        if (years.Count == 0)
            return new FinancialStatementViewData([], [], false, unit);

        var yearDataByYear = yearDataList.ToDictionary(y => y.FinancialYear);
        var factsByLabel = factsForSection
            .GroupBy(f => f.Label.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var template = section switch
        {
            FinancialStatementSection.BalanceSheet => BalanceSheetTemplate,
            FinancialStatementSection.ProfitAndLoss => PnlTemplate,
            FinancialStatementSection.CashFlow => CashFlowTemplate,
            _ => Array.Empty<TemplateRow>()
        };

        var rows = new List<FinancialStatementRow>();
        var consumedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasYearInferred = yearDataList.Any(y => y.CashFlowYearInferred) || factsForSection.Any(f => f.YearInferred);

        // 1. Process template rows in order
        foreach (var t in template)
        {
            var matchingFacts = factsByLabel.TryGetValue(t.Label, out var fList) ? fList : null;
            var valuesByYear = new Dictionary<int, (decimal? Numeric, string? Raw, bool YearInferred)>();
            var hasAnyValue = false;
            int? minRow = null;

            foreach (var y in years)
            {
                decimal? numVal = null;
                string? rawVal = null;
                var inferred = false;

                if (t.TypedGetter is not null && yearDataByYear.TryGetValue(y, out var yd))
                {
                    numVal = t.TypedGetter(yd);
                    if (numVal is not null)
                    {
                        hasAnyValue = true;
                        if (section == FinancialStatementSection.CashFlow && yd.CashFlowYearInferred)
                            inferred = true;
                    }
                }

                if (numVal is null && matchingFacts is not null)
                {
                    var fact = matchingFacts.FirstOrDefault(f => f.FinancialYear == y);
                    if (fact is not null)
                    {
                        numVal = fact.NumericValue;
                        rawVal = fact.RawValue;
                        inferred = fact.YearInferred;
                        hasAnyValue = true;
                        if (fact.SourceRowNumber.HasValue && (minRow is null || fact.SourceRowNumber.Value < minRow.Value))
                            minRow = fact.SourceRowNumber;
                    }
                }

                valuesByYear[y] = (numVal, rawVal, inferred);
            }

            if (hasAnyValue || t.IsHeader)
            {
                rows.Add(new FinancialStatementRow
                {
                    Label = t.Label,
                    IsSubtotal = t.IsSubtotal,
                    IsHeader = t.IsHeader,
                    SourceRowNumber = minRow,
                    ValuesByYear = valuesByYear
                });
                consumedLabels.Add(t.Label);
            }
        }

        // 2. Append any unmapped facts that were NOT in the standard template, ordered by SourceRowNumber
        var remainingFacts = factsByLabel
            .Where(kvp => !consumedLabels.Contains(kvp.Key))
            .OrderBy(kvp => kvp.Value.Min(f => f.SourceRowNumber ?? int.MaxValue))
            .ToList();

        foreach (var (label, factList) in remainingFacts)
        {
            var valuesByYear = new Dictionary<int, (decimal? Numeric, string? Raw, bool YearInferred)>();
            var hasAnyValue = false;
            int? minRow = null;

            foreach (var y in years)
            {
                var fact = factList.FirstOrDefault(f => f.FinancialYear == y);
                if (fact is not null)
                {
                    valuesByYear[y] = (fact.NumericValue, fact.RawValue, fact.YearInferred);
                    hasAnyValue = true;
                    if (fact.SourceRowNumber.HasValue && (minRow is null || fact.SourceRowNumber.Value < minRow.Value))
                        minRow = fact.SourceRowNumber;
                }
                else
                {
                    valuesByYear[y] = (null, null, false);
                }
            }

            if (hasAnyValue)
            {
                var isSubtotal = IsLikelySubtotal(label);
                rows.Add(new FinancialStatementRow
                {
                    Label = label,
                    IsSubtotal = isSubtotal,
                    IsHeader = false,
                    SourceRowNumber = minRow,
                    ValuesByYear = valuesByYear
                });
            }
        }

        return new FinancialStatementViewData(years, rows, hasYearInferred, unit);
    }

    private static FinancialStatementViewData BuildRatiosViewData(
        IReadOnlyList<FinancialYearData> yearDataList,
        IReadOnlyList<FinancialFact> ratioFacts)
    {
        var years = yearDataList.Select(y => y.FinancialYear)
            .Concat(ratioFacts.Where(f => f.FinancialYear.HasValue).Select(f => f.FinancialYear!.Value))
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();

        if (years.Count == 0)
            return new FinancialStatementViewData([], [], false, "");

        var groupedRatios = ratioFacts
            .GroupBy(f => f.Label.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(f => f.SourceRowNumber ?? int.MaxValue))
            .ToList();

        var rows = new List<FinancialStatementRow>();
        foreach (var grp in groupedRatios)
        {
            var valuesByYear = new Dictionary<int, (decimal? Numeric, string? Raw, bool YearInferred)>();
            int? minRow = null;

            foreach (var y in years)
            {
                var fact = grp.FirstOrDefault(f => f.FinancialYear == y);
                if (fact is not null)
                {
                    valuesByYear[y] = (fact.NumericValue, fact.RawValue, fact.YearInferred);
                    if (fact.SourceRowNumber.HasValue && (minRow is null || fact.SourceRowNumber.Value < minRow.Value))
                        minRow = fact.SourceRowNumber;
                }
                else
                {
                    valuesByYear[y] = (null, null, false);
                }
            }

            rows.Add(new FinancialStatementRow
            {
                Label = grp.Key,
                IsSubtotal = false,
                IsHeader = false,
                SourceRowNumber = minRow,
                ValuesByYear = valuesByYear
            });
        }

        return new FinancialStatementViewData(years, rows, false, "");
    }

    private static bool IsLikelySubtotal(string label)
    {
        var l = label.Trim().ToLowerInvariant();
        return l.StartsWith("total")
            || l.StartsWith("net cash")
            || l.StartsWith("operating profit")
            || l.StartsWith("profit before")
            || l.StartsWith("profit for the period")
            || l.StartsWith("net profit")
            || l.StartsWith("net worth");
    }
}
