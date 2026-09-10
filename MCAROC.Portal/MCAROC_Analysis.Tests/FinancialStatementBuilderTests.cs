using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class FinancialStatementBuilderTests
{
    [Fact]
    public void BuildsBalanceSheetWithTypedAndUnmappedFacts()
    {
        var stdYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, ShareCapital = 10m, NetWorth = 50m, CurrentAssets = 100m },
            new() { FinancialYear = 2025, Basis = FinancialBasis.Standalone, ShareCapital = 10m, NetWorth = 60m, CurrentAssets = 120m },
        };

        var facts = new List<FinancialFact>
        {
            new()
            {
                Basis = FinancialBasis.Standalone,
                Section = FinancialStatementSection.BalanceSheet,
                Label = "Reserves and Surplus",
                FinancialYear = 2024,
                NumericValue = 40m,
                SourceRowNumber = 4
            },
            new()
            {
                Basis = FinancialBasis.Standalone,
                Section = FinancialStatementSection.BalanceSheet,
                Label = "Reserves and Surplus",
                FinancialYear = 2025,
                NumericValue = 50m,
                SourceRowNumber = 4
            },
            new()
            {
                Basis = FinancialBasis.Standalone,
                Section = FinancialStatementSection.BalanceSheet,
                Label = "Gross Fixed Assets",
                FinancialYear = 2024,
                NumericValue = 200m,
                SourceRowNumber = 20
            },
            new()
            {
                Basis = FinancialBasis.Standalone,
                Section = FinancialStatementSection.BalanceSheet,
                Label = "Gross Fixed Assets",
                FinancialYear = 2025,
                NumericValue = 220m,
                SourceRowNumber = 20
            }
        };

        var vm = FinancialStatementBuilder.Build(
            "bs", "Balance Sheet", FinancialStatementSection.BalanceSheet,
            stdYears, [], facts, "₹ Crore");

        Assert.True(vm.HasAnyData);
        Assert.False(vm.HasConsolidated);
        Assert.Equal(2, vm.Standalone.Years.Count);
        Assert.Equal([2025, 2024], vm.Standalone.Years);

        // Check that Share Capital (typed), Reserves and Surplus (fact), Total Equity (typed subtotal), Gross Fixed Assets (fact) exist
        var shareCap = vm.Standalone.Rows.Single(r => r.Label == "Share Capital");
        Assert.Equal(10m, shareCap.GetValue(2025).Numeric);

        var reserves = vm.Standalone.Rows.Single(r => r.Label == "Reserves and Surplus");
        Assert.Equal(50m, reserves.GetValue(2025).Numeric);
        Assert.Equal(40m, reserves.GetValue(2024).Numeric);

        var totalEquity = vm.Standalone.Rows.Single(r => r.Label == "Total Equity");
        Assert.True(totalEquity.IsSubtotal);
        Assert.Equal(60m, totalEquity.GetValue(2025).Numeric);

        var gfa = vm.Standalone.Rows.Single(r => r.Label == "Gross Fixed Assets");
        Assert.Equal(220m, gfa.GetValue(2025).Numeric);
    }

    [Fact]
    public void BuildsPnlWithTypedAndUnmappedFacts()
    {
        var stdYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2025, Basis = FinancialBasis.Standalone, Revenue = 500m, Ebitda = 80m, FinanceCost = 15m, Pbt = 45m, Pat = 30m }
        };

        var facts = new List<FinancialFact>
        {
            new()
            {
                Basis = FinancialBasis.Standalone,
                Section = FinancialStatementSection.ProfitAndLoss,
                Label = "Employee Benefit Expense",
                FinancialYear = 2025,
                NumericValue = 120m,
                SourceRowNumber = 12
            },
            new()
            {
                Basis = FinancialBasis.Standalone,
                Section = FinancialStatementSection.ProfitAndLoss,
                Label = "Current Tax",
                FinancialYear = 2025,
                NumericValue = 15m,
                SourceRowNumber = 25
            }
        };

        var vm = FinancialStatementBuilder.Build(
            "pl", "Profit & Loss", FinancialStatementSection.ProfitAndLoss,
            stdYears, [], facts, "₹ Crore");

        Assert.Single(vm.Standalone.Years);
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Revenue from Operations" && r.GetValue(2025).Numeric == 500m);
        Assert.DoesNotContain(vm.Standalone.Rows, r => r.Label == "Net Revenue");
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Employee Benefit Expense" && r.GetValue(2025).Numeric == 120m);
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Finance Costs" && r.GetValue(2025).Numeric == 15m);
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Profit Before Tax" && r.IsSubtotal && r.GetValue(2025).Numeric == 45m);
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Current Tax" && r.GetValue(2025).Numeric == 15m);
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Profit for the Period" && r.IsSubtotal && r.GetValue(2025).Numeric == 30m);
        Assert.DoesNotContain(vm.Standalone.Rows, r => r.Label == "Profit After Tax (PAT)");
    }

    [Fact]
    public void PnlDoesNotDuplicateAliasesWhenSourceFactsAreAbsent()
    {
        var stdYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2025, Basis = FinancialBasis.Standalone, Revenue = 100m, Pat = 10m }
        };

        var vm = FinancialStatementBuilder.Build(
            "pl", "Profit & Loss", FinancialStatementSection.ProfitAndLoss,
            stdYears, [], [], "₹ Crore");

        // Assert exactly one revenue line and one PAT line exists
        var revRows = vm.Standalone.Rows.Where(r => r.Label is "Revenue from Operations" or "Net Revenue").ToList();
        Assert.Single(revRows);
        Assert.Equal("Revenue from Operations", revRows[0].Label);
        Assert.Equal(100m, revRows[0].GetValue(2025).Numeric);

        var patRows = vm.Standalone.Rows.Where(r => r.Label is "Profit for the Period" or "Profit After Tax (PAT)").ToList();
        Assert.Single(patRows);
        Assert.Equal("Profit for the Period", patRows[0].Label);
        Assert.Equal(10m, patRows[0].GetValue(2025).Numeric);
    }

    [Fact]
    public void BuildsCashFlowAndSurfacesInferredYearFlags()
    {
        var stdYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Cfo = 25m, Cfi = -10m, Cff = -5m, CashFlowYearInferred = true },
            new() { FinancialYear = 2025, Basis = FinancialBasis.Standalone, Cfo = 30m, Cfi = -15m, Cff = -8m, CashFlowYearInferred = true },
        };

        var facts = new List<FinancialFact>
        {
            new()
            {
                Basis = FinancialBasis.Standalone,
                Section = FinancialStatementSection.CashFlow,
                Label = "Net Increase / (Decrease) in Cash and Cash Equivalents",
                FinancialYear = 2025,
                NumericValue = 7m,
                YearInferred = true
            }
        };

        var vm = FinancialStatementBuilder.Build(
            "cf", "Cash Flow", FinancialStatementSection.CashFlow,
            stdYears, [], facts, "₹ Crore");

        Assert.True(vm.HasYearInferred);
        Assert.True(vm.Standalone.HasYearInferred);

        var cfo = vm.Standalone.Rows.Single(r => r.Label.Contains("Operating Activities"));
        Assert.True(cfo.IsSubtotal);
        Assert.Equal(30m, cfo.GetValue(2025).Numeric);
        Assert.True(cfo.GetValue(2025).YearInferred);

        var netInc = vm.Standalone.Rows.Single(r => r.Label.Contains("Net Increase"));
        Assert.Equal(7m, netInc.GetValue(2025).Numeric);
        Assert.True(netInc.GetValue(2025).YearInferred);
    }

    [Fact]
    public void BuildsRatiosFromFinancialFacts()
    {
        var facts = new List<FinancialFact>
        {
            new() { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.Ratios, Label = "Current Ratio", FinancialYear = 2024, NumericValue = 1.45m, SourceRowNumber = 101 },
            new() { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.Ratios, Label = "Current Ratio", FinancialYear = 2025, NumericValue = 1.62m, SourceRowNumber = 101 },
            new() { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.Ratios, Label = "Debt / Equity Ratio", FinancialYear = 2024, NumericValue = 2.10m, SourceRowNumber = 102 },
            new() { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.Ratios, Label = "Debt / Equity Ratio", FinancialYear = 2025, NumericValue = 1.85m, SourceRowNumber = 102 },
            new() { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.Ratios, Label = "Operating Profit Margin (%)", FinancialYear = 2025, RawValue = "14.5%", SourceRowNumber = 103 },
        };

        var vm = FinancialStatementBuilder.BuildRatios("ratios", "Financial Ratios", [], [], facts);

        Assert.True(vm.HasAnyData);
        Assert.Equal(2, vm.Standalone.Years.Count);
        Assert.Equal(3, vm.Standalone.Rows.Count);

        var curRatio = vm.Standalone.Rows.Single(r => r.Label == "Current Ratio");
        Assert.Equal(1.62m, curRatio.GetValue(2025).Numeric);
        Assert.Equal(1.45m, curRatio.GetValue(2024).Numeric);

        var opMargin = vm.Standalone.Rows.Single(r => r.Label == "Operating Profit Margin (%)");
        Assert.Equal("14.5%", opMargin.GetValue(2025).Raw);
    }

    [Fact]
    public void SupportsBothStandaloneAndConsolidatedBases()
    {
        var stdYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2025, Basis = FinancialBasis.Standalone, Revenue = 400m, NetWorth = 100m }
        };
        var conYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2025, Basis = FinancialBasis.Consolidated, Revenue = 650m, NetWorth = 180m }
        };

        var facts = new List<FinancialFact>
        {
            new() { Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.BalanceSheet, Label = "Reserves and Surplus", FinancialYear = 2025, NumericValue = 90m },
            new() { Basis = FinancialBasis.Consolidated, Section = FinancialStatementSection.BalanceSheet, Label = "Reserves and Surplus", FinancialYear = 2025, NumericValue = 160m },
        };

        var vm = FinancialStatementBuilder.Build(
            "bs", "Balance Sheet", FinancialStatementSection.BalanceSheet,
            stdYears, conYears, facts, "₹ Crore");

        Assert.True(vm.HasConsolidated);
        Assert.True(vm.Standalone.HasData);
        Assert.True(vm.Consolidated.HasData);

        var stdReserves = vm.Standalone.Rows.Single(r => r.Label == "Reserves and Surplus");
        Assert.Equal(90m, stdReserves.GetValue(2025).Numeric);

        var conReserves = vm.Consolidated.Rows.Single(r => r.Label == "Reserves and Surplus");
        Assert.Equal(160m, conReserves.GetValue(2025).Numeric);
    }

    [Fact]
    public void FallbackToTypedColumnsWhenFactsAreEmpty()
    {
        var stdYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2025, Basis = FinancialBasis.Standalone, Revenue = 300m, NetWorth = 80m, ShareCapital = 20m, Pat = 15m }
        };

        var vm = FinancialStatementBuilder.Build(
            "bs", "Balance Sheet", FinancialStatementSection.BalanceSheet,
            stdYears, [], [], "₹ Crore");

        Assert.True(vm.HasAnyData);
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Share Capital" && r.GetValue(2025).Numeric == 20m);
        Assert.Contains(vm.Standalone.Rows, r => r.Label == "Total Equity" && r.GetValue(2025).Numeric == 80m);
    }
}
