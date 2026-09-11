using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class FinancialTrendMetricsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static string? RocFixture()
    {
        var dir = Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis.Tests", "Fixtures", "workbooks");
        var roc = Path.Combine(dir, "roc.xls");
        return File.Exists(roc) ? roc : null;
    }

    private static DossierModel CreateMinimalDossier(List<FinancialYearData> standalone)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null),
            Financials: new DossierFinancials(standalone, [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    private static MetricResult M(MetricGroup group, string label) => Assert.Single(group.Metrics, m => m.Label == label);

    [Fact]
    public void No_financial_years_makes_every_metric_insufficient()
    {
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier([]));

        Assert.Equal("Financial trend & leverage", group.Title);
        Assert.True(group.HasAny); // insufficiency results still populate the group
        Assert.All(group.Metrics, m => Assert.False(m.HasValue));
    }

    [Fact]
    public void Revenue_cagr_uses_a_3_year_window_and_names_the_span()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2014, Revenue = 1447.53m },
            new() { FinancialYear = 2015, Revenue = 1349.11m },
            new() { FinancialYear = 2016, Revenue = 1041.74m },
            new() { FinancialYear = 2017, Revenue = 1284.2m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Revenue CAGR");
        Assert.True(m.HasValue);
        Assert.Equal(-3.9m, m.Value);
        Assert.Equal("FY2014–FY2017", m.Period);
    }

    [Fact]
    public void Revenue_cagr_exponent_uses_elapsed_FYs_not_the_count_of_reported_points()
    {
        // Regression: FY2015 has no Revenue row at all (a gap, not a zero) between FY2014 and FY2016/17.
        // The exponent must be the 3 elapsed calendar years (2017-2014), not 2 (the count of non-null
        // points minus 1) — using the point-count as the exponent overstates the rate on any sparse series.
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2014, Revenue = 100m },
            new() { FinancialYear = 2016, Revenue = 200m },
            new() { FinancialYear = 2017, Revenue = 300m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Revenue CAGR");
        Assert.True(m.HasValue);
        Assert.Equal(44.2m, m.Value); // (300/100)^(1/3) - 1 = 44.2%, NOT (300/100)^(1/2)-1 = 73.2%
        Assert.Equal("FY2014–FY2017", m.Period);
    }

    [Fact]
    public void Revenue_cagr_never_reaches_further_back_than_3_calendar_years_for_the_base_point()
    {
        // Regression: 4 non-null points spread 3 years apart each (9 years total). Selecting the base
        // point by counting rows back (old bug) would pick FY2008 (3 rows back), producing a ~9-year
        // "CAGR" despite the contract's 3-FY window. The base point must be the earliest point that is
        // still within 3 calendar years of the latest — here that's FY2014, not FY2008 or FY2011.
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2008, Revenue = 50m },
            new() { FinancialYear = 2011, Revenue = 80m },
            new() { FinancialYear = 2014, Revenue = 120m },
            new() { FinancialYear = 2017, Revenue = 300m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Revenue CAGR");
        Assert.True(m.HasValue);
        Assert.Equal("FY2014–FY2017", m.Period); // NOT FY2008-FY2017 or FY2011-FY2017
        Assert.Equal(35.7m, m.Value); // (300/120)^(1/3) - 1 = 35.7%
    }

    [Fact]
    public void Revenue_cagr_is_insufficient_when_no_earlier_point_falls_within_the_3_year_window()
    {
        // Two non-null points exist (passes the ">= 2 points" check) but they're 9 years apart — there
        // is no base year within the contract's 3-FY window, so this must fail closed, not silently use
        // the only other point available regardless of how far back it is.
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2008, Revenue = 50m },
            new() { FinancialYear = 2017, Revenue = 300m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Revenue CAGR");
        Assert.False(m.HasValue);
        Assert.Contains("no earlier reported year within the last 3 FYs", m.InsufficiencyReason);
    }

    [Fact]
    public void Revenue_cagr_is_insufficient_with_fewer_than_2_years()
    {
        var group = DossierComputations.FinancialTrendMetrics(
            CreateMinimalDossier([new() { FinancialYear = 2017, Revenue = 100m }]));

        var m = M(group, "Revenue CAGR");
        Assert.False(m.HasValue);
        Assert.Contains("Fewer than 2", m.InsufficiencyReason);
    }

    [Fact]
    public void Pat_cagr_falls_back_to_total_change_on_a_negative_base_year()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2014, Pat = -273.22m },
            new() { FinancialYear = 2017, Pat = 2.62m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "PAT CAGR");
        Assert.True(m.HasValue); // fallback still yields a value, not "insufficient"
        Assert.Equal(101.0m, m.Value);
        Assert.Contains("not a true CAGR", m.Period);
    }

    [Fact]
    public void Revenue_cagr_has_no_fallback_and_stays_insufficient_on_a_negative_base_year()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2014, Revenue = -10m },
            new() { FinancialYear = 2017, Revenue = 100m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Revenue CAGR");
        Assert.False(m.HasValue); // unlike A2.2/A2.3, A2.1 has no total-%-change fallback
    }

    [Fact]
    public void Pat_cagr_does_not_crash_when_a_positive_base_swings_to_a_negative_end()
    {
        // Regression: Math.Pow(negative, 1/n) is NaN for a non-integer n, which throws on cast to
        // decimal — a positive base with a negative end must route to the fallback, not the true-CAGR
        // formula, purely from the ratio's sign (checking only the base's sign misses this case).
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2023, Pat = 10m },
            new() { FinancialYear = 2025, Pat = -50m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "PAT CAGR");
        Assert.True(m.HasValue);
        Assert.Equal(-600.0m, m.Value); // (-50 - 10) / |10| * 100
        Assert.Contains("not a true CAGR", m.Period);
    }

    [Fact]
    public void Debt_growth_requires_both_borrowing_components_when_total_debt_is_unreported()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2016, LongTermBorrowings = 2321.28m, ShortTermBorrowings = 1693.35m },
            new() { FinancialYear = 2017, LongTermBorrowings = 2283.25m, ShortTermBorrowings = null }, // missing component
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Total debt growth (YoY)");
        Assert.False(m.HasValue); // never falls back to treating the missing component as 0
    }

    [Fact]
    public void Net_worth_growth_flags_a_negative_latest_net_worth()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, NetWorth = 90m },
            new() { FinancialYear = 2025, NetWorth = -40m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Net worth growth (YoY)");
        Assert.True(m.HasValue);
        Assert.Equal(-144.4m, m.Value);
        Assert.Contains("net worth is negative", m.Period);
    }

    [Fact]
    public void Operating_leverage_is_insufficient_when_revenue_growth_is_zero()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2016, Revenue = 100m, Ebitda = 20m },
            new() { FinancialYear = 2017, Revenue = 100m, Ebitda = 30m },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        var m = M(group, "Operating leverage");
        Assert.False(m.HasValue);
        Assert.Contains("n/m", m.InsufficiencyReason);
    }

    [Fact]
    public void Cfo_pat_and_fcf_proxy_are_insufficient_on_an_inferred_cash_flow_year()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2017, Cfo = 10m, Cfi = 5m, Pat = 20m, CashFlowYearInferred = true },
        };
        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years));

        Assert.False(M(group, "Free cash flow (proxy)").HasValue);
        Assert.False(M(group, "CFO / PAT").HasValue);
        Assert.False(M(group, "Cash-to-accrual divergence").HasValue);
    }

    [Fact]
    public void Debt_funded_growth_flag_requires_debt_growth_over_20_percent_AND_exceeding_revenue_growth()
    {
        // Debt growth (30%) exceeds revenue CAGR (5%) and clears the 20% floor -> flagged.
        var flagged = new List<FinancialYearData>
        {
            new() { FinancialYear = 2016, Revenue = 100m, LongTermBorrowings = 100m, ShortTermBorrowings = 0m },
            new() { FinancialYear = 2017, Revenue = 105m, LongTermBorrowings = 130m, ShortTermBorrowings = 0m },
        };
        var flaggedGroup = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(flagged));
        var flag = M(flaggedGroup, "Debt-funded-growth flag");
        Assert.True(flag.HasValue);
        Assert.Equal(1m, flag.Value);

        // Debt growth (11.8%) doesn't clear the 20% floor -> not flagged, even though it exceeds revenue.
        var notFlagged = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 360m, TotalDebt = 340m },
            new() { FinancialYear = 2025, Revenue = 300m, TotalDebt = 380m },
        };
        var notFlaggedGroup = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(notFlagged));
        var notFlag = M(notFlaggedGroup, "Debt-funded-growth flag");
        Assert.True(notFlag.HasValue);
        Assert.Equal(0m, notFlag.Value);
    }

    [SkippableFact]
    public void Coastal_fixture_exact_financial_trend_metrics()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);
        var standaloneSheet = sheets.Single(s => s.Name == "Standalone Financial Data");
        var parsed = StandaloneFinancialDataParser.Parse(standaloneSheet, 1, 1, 1, out _);

        Assert.Equal(4, parsed.Items.Count); // FY2014-FY2017
        Assert.Single(parsed.Warnings, w => w.IssueCode == "CASH_FLOW_YEAR_ALIGNMENT_ASSUMED");

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(parsed.Items));
        Assert.Equal("Financial trend & leverage", group.Title);

        // A2.1: Revenue CAGR, FY2014 (1447.53) -> FY2017 (1284.2), n=3
        var a21 = M(group, "Revenue CAGR");
        Assert.Equal(-3.9m, a21.Value);
        Assert.Equal("FY2014–FY2017", a21.Period);

        // A2.2: PAT CAGR — negative base year (FY2014 = -273.22) -> total % change fallback
        var a22 = M(group, "PAT CAGR");
        Assert.Equal(101.0m, a22.Value);
        Assert.Contains("not a true CAGR", a22.Period);

        // A2.3: EBITDA CAGR, FY2014 (225.7) -> FY2017 (523.41), n=3
        var a23 = M(group, "EBITDA CAGR");
        Assert.Equal(32.4m, a23.Value);

        // A2.4: total debt growth YoY, FY2016 (4014.63) -> FY2017 (4117.52)
        var a24 = M(group, "Total debt growth (YoY)");
        Assert.Equal(2.6m, a24.Value);
        Assert.Equal("FY2016–FY2017", a24.Period);

        // A2.5: net worth growth YoY, FY2016 (267.38) -> FY2017 (270) — positive, no negative-flag suffix
        var a25 = M(group, "Net worth growth (YoY)");
        Assert.Equal(1.0m, a25.Value);
        Assert.DoesNotContain("negative", a25.Period);

        // A2.6: operating leverage = EBITDA growth (raw) / Revenue growth (raw), FY2016->FY2017
        var a26 = M(group, "Operating leverage");
        Assert.Equal(6.80m, a26.Value);

        // A3.1 / A3.2: net debt and net debt / EBITDA, FY2017
        var a31 = M(group, "Net debt");
        Assert.Equal(4084.50m, a31.Value);
        Assert.Equal("FY2017", a31.Period);

        var a32 = M(group, "Net debt / EBITDA");
        Assert.Equal(7.80m, a32.Value);

        // A3.3 / A3.4 / A3.6: FY2017's cash-flow figures are column-inferred -> all three insufficient
        Assert.False(M(group, "Free cash flow (proxy)").HasValue);
        Assert.Contains("column-inferred", M(group, "Free cash flow (proxy)").InsufficiencyReason);
        Assert.False(M(group, "CFO / PAT").HasValue);
        Assert.False(M(group, "Cash-to-accrual divergence").HasValue);

        // A3.5: debt growth (2.6%) doesn't clear the 20% floor -> not flagged
        var a35 = M(group, "Debt-funded-growth flag");
        Assert.Equal(0m, a35.Value);
    }
}
