using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Tests;

/// <summary>#116 (C6)'s reference dossier-side mapping: <see cref="DossierComputations.BuildRevenueTrendSeries"/>.</summary>
public class RevenueTrendSeriesTests
{
    [Fact]
    public void Maps_financial_years_to_a_crore_series_sorted_by_year()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2026, Revenue = 150m },
            new() { FinancialYear = 2024, Revenue = 100m },
            new() { FinancialYear = 2025, Revenue = 120m },
        };

        var series = DossierComputations.BuildRevenueTrendSeries(years);

        Assert.Equal("Revenue", series.Label);
        Assert.Equal(MetricUnit.Crore, series.Unit);
        Assert.Contains("FinancialYearData.Revenue", series.Inputs);
        Assert.Equal(["FY2024", "FY2025", "FY2026"], series.Points.Select(p => p.Period.Label));
        Assert.Equal([100m, 120m, 150m], series.Points.Select(p => p.Value));
    }

    [Fact]
    public void A_null_revenue_year_stays_null_never_coerced_to_zero()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2025, Revenue = null },
            new() { FinancialYear = 2026, Revenue = 50m },
        };

        var series = DossierComputations.BuildRevenueTrendSeries(years);

        Assert.Null(series.Points[0].Value);
        Assert.Equal(50m, series.Points[1].Value);
    }

    [Fact]
    public void Throws_when_there_are_no_financial_years_the_caller_must_guard_before_calling()
    {
        Assert.Throws<ArgumentException>(() => DossierComputations.BuildRevenueTrendSeries([]));
    }
}
