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

    private static DossierModel CreateMinimalDossier(
        List<FinancialYearData> standalone,
        List<FinancialFact>? facts = null,
        List<FinancialParameter>? parameters = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], []),
            Financials: new DossierFinancials(standalone, [], facts ?? [], parameters ?? [], [], []),
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

    private static string? UploadFixture(params string[] pathSegments)
    {
        var parts = new List<string> { RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis", "App_Data", "Uploads" };
        parts.AddRange(pathSegments);
        var path = Path.Combine(parts.ToArray());
        return File.Exists(path) ? path : null;
    }

    [SkippableFact]
    public void Roc_fixture_cost_structure_and_forex_control_totals()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc!);
        var standaloneSheet = sheets.Single(s => s.Name == "Standalone Financial Data");
        var parsed = StandaloneFinancialDataParser.Parse(standaloneSheet, 1, 1, 1, out var facts);
        var highlightsSheet = sheets.FirstOrDefault(s => s.Name == "Highlights");
        var annexureSheet = sheets.FirstOrDefault(s => s.Name == "Annexure - Financial Parameters");
        var parsedParams = FinancialParametersParser.Parse(highlightsSheet, annexureSheet, 1, 1, 1);

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(parsed.Items, facts, parsedParams.Items));

        // Latest standalone FY is FY2017 with Revenue = 1284.20 Cr
        // A4.1: Employee cost % of revenue (Employee benefits expense = 96.22 Cr, unrounded = 7.4925985...%, displayed = 7.49%)
        var a41 = M(group, "Employee cost % of revenue");
        Assert.True(a41.HasValue);
        Assert.Equal(96.22m / 1284.20m * 100m, a41.Value);
        Assert.Equal("7.49%", a41.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a41.Unit);
        Assert.Equal("FY2017", a41.Period);

        // A4.2: Material cost % of revenue (Cost of Materials Consumed = 395.13 Cr, unrounded = 30.76857...%, displayed = 30.77%)
        var a42 = M(group, "Material cost % of revenue");
        Assert.True(a42.HasValue);
        Assert.Equal(395.13m / 1284.20m * 100m, a42.Value);
        Assert.Equal("30.77%", a42.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a42.Unit);
        Assert.Equal("FY2017", a42.Period);

        // A4.3: Other expenses % of revenue (Other Expenses = 457.15 Cr, unrounded = 35.59803...%, displayed = 35.60%)
        var a43 = M(group, "Other expenses % of revenue");
        Assert.True(a43.HasValue);
        Assert.Equal(457.15m / 1284.20m * 100m, a43.Value);
        Assert.Equal("35.60%", a43.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a43.Unit);
        Assert.Equal("FY2017", a43.Period);

        // A4.4: Auditor fee % of revenue (Payment to Auditors = 0.0 Cr -> 0.0%, displayed = 0%)
        var a44 = M(group, "Auditor fee % of revenue");
        Assert.True(a44.HasValue);
        Assert.Equal(0.0m, a44.Value);
        Assert.Equal("0%", a44.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a44.Unit);
        Assert.Equal("FY2017", a44.Period);

        // A5.1: Export income % of revenue (Income in foreign currency = '-' -> Insufficient)
        var a51 = M(group, "Export income % of revenue");
        Assert.False(a51.HasValue);
        Assert.Contains("explicitly reported as '-'", a51.InsufficiencyReason);
        Assert.Equal(MetricUnit.Percent, a51.Unit);

        // A5.2: Net forex exposure (Both income and expense are '-' -> Insufficient)
        var a52 = M(group, "Net forex exposure");
        Assert.False(a52.HasValue);
        Assert.Contains("explicitly reported as '-'", a52.InsufficiencyReason);
        Assert.Equal(MetricUnit.Crore, a52.Unit);
    }

    [SkippableFact]
    public void Upload_1_fixture_cost_structure_and_forex_control_totals()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var f1 = UploadFixture("1", "original", "1.xls");
        Skip.If(f1 is null, "1.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(f1!);
        var standaloneSheet = sheets.Single(s => s.Name == "Standalone Financial Data");
        var parsed = StandaloneFinancialDataParser.Parse(standaloneSheet, 1, 1, 1, out var facts);
        var highlightsSheet = sheets.FirstOrDefault(s => s.Name == "Highlights");
        var annexureSheet = sheets.FirstOrDefault(s => s.Name == "Annexure - Financial Parameters");
        var parsedParams = FinancialParametersParser.Parse(highlightsSheet, annexureSheet, 1, 1, 1);

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(parsed.Items, facts, parsedParams.Items));

        // Latest standalone FY is FY2025 with Revenue = 20.25 Cr
        // A4.1: Employee cost % of revenue (Employee benefits expense = 41.0 Cr, unrounded = 202.4691...%, displayed = 202.47%)
        var a41 = M(group, "Employee cost % of revenue");
        Assert.True(a41.HasValue);
        Assert.Equal(41.0m / 20.25m * 100m, a41.Value);
        Assert.Equal("202.47%", a41.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a41.Unit);
        Assert.Equal("FY2025", a41.Period);

        // A4.2: Material cost % of revenue (Cost of Materials Consumed = 15.48 Cr, unrounded = 76.4444...%, displayed = 76.44%)
        var a42 = M(group, "Material cost % of revenue");
        Assert.True(a42.HasValue);
        Assert.Equal(15.48m / 20.25m * 100m, a42.Value);
        Assert.Equal("76.44%", a42.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a42.Unit);
        Assert.Equal("FY2025", a42.Period);

        // A4.3: Other expenses % of revenue (Other Expenses = 21.28 Cr, unrounded = 105.0864...%, displayed = 105.09%)
        var a43 = M(group, "Other expenses % of revenue");
        Assert.True(a43.HasValue);
        Assert.Equal(21.28m / 20.25m * 100m, a43.Value);
        Assert.Equal("105.09%", a43.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a43.Unit);
        Assert.Equal("FY2025", a43.Period);

        // A4.4: Auditor fee % of revenue (Payment to Auditors = 0.12 Cr, unrounded = 0.59259...%, displayed = 0.59%)
        var a44 = M(group, "Auditor fee % of revenue");
        Assert.True(a44.HasValue);
        Assert.Equal(0.12m / 20.25m * 100m, a44.Value);
        Assert.Equal("0.59%", a44.DisplayValue());
        Assert.Equal(MetricUnit.Percent, a44.Unit);
        Assert.Equal("FY2025", a44.Period);

        // A5.1 / A5.2: foreign currency reported as '-'
        var a51 = M(group, "Export income % of revenue");
        Assert.False(a51.HasValue);
        Assert.Contains("explicitly reported as '-'", a51.InsufficiencyReason);

        var a52 = M(group, "Net forex exposure");
        Assert.False(a52.HasValue);
        Assert.Contains("explicitly reported as '-'", a52.InsufficiencyReason);
    }

    [Fact]
    public void Synthetic_cost_and_forex_happy_path_and_unrounded_storage()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 300.0m, Basis = FinancialBasis.Standalone }
        };

        var facts = new List<FinancialFact>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = 100.0m },
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Other Expenses", NumericValue = 45.0m },
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Payment to Auditors", NumericValue = 1.5m }
        };

        var parameters = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Employee benefits expense", NumericValue = 60.0m, Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = 75.0m, Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Expense in foreign currency", NumericValue = 25.0m, Unit = "Rs. Crore" }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, facts, parameters));

        // A4.1: 60 / 300 = 20.0%
        var a41 = M(group, "Employee cost % of revenue");
        Assert.True(a41.HasValue);
        Assert.Equal(20.0m, a41.Value);
        Assert.Equal("20%", a41.DisplayValue());

        // A4.2: 100 / 300 = 33.33333333333333333333333333% (unrounded in Value, displayed as 33.33%)
        var a42 = M(group, "Material cost % of revenue");
        Assert.True(a42.HasValue);
        Assert.Equal(100.0m / 300.0m * 100m, a42.Value);
        Assert.Equal("33.33%", a42.DisplayValue());

        // A4.3: 45 / 300 = 15.0%
        var a43 = M(group, "Other expenses % of revenue");
        Assert.True(a43.HasValue);
        Assert.Equal(15.0m, a43.Value);
        Assert.Equal("15%", a43.DisplayValue());

        // A4.4: 1.5 / 300 = 0.5%
        var a44 = M(group, "Auditor fee % of revenue");
        Assert.True(a44.HasValue);
        Assert.Equal(0.5m, a44.Value);
        Assert.Equal("0.50%", a44.DisplayValue());

        // A5.1: 75 / 300 = 25.0%
        var a51 = M(group, "Export income % of revenue");
        Assert.True(a51.HasValue);
        Assert.Equal(25.0m, a51.Value);
        Assert.Equal("25%", a51.DisplayValue());

        // A5.2: 75 - 25 = 50.0 Cr
        var a52 = M(group, "Net forex exposure");
        Assert.True(a52.HasValue);
        Assert.Equal(50.0m, a52.Value);
        Assert.Equal("₹50 Cr", a52.DisplayValue());
    }

    [Fact]
    public void Negative_net_forex_exposure()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        var parameters = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = 10.0m, Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Expense in foreign currency", NumericValue = 45.5m, Unit = "Rs. Crore" }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], parameters));

        var a52 = M(group, "Net forex exposure");
        Assert.True(a52.HasValue);
        Assert.Equal(-35.5m, a52.Value);
        Assert.Equal("₹-35.50 Cr", a52.DisplayValue());
    }

    [Fact]
    public void Zero_or_negative_revenue_makes_all_percent_metrics_insufficient()
    {
        var zeroRevYears = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 0m, Basis = FinancialBasis.Standalone }
        };

        var facts = new List<FinancialFact>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = 50.0m }
        };
        var parameters = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Employee benefits expense", NumericValue = 20.0m, Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = 30.0m, Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Expense in foreign currency", NumericValue = 10.0m, Unit = "Rs. Crore" }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(zeroRevYears, facts, parameters));

        Assert.False(M(group, "Employee cost % of revenue").HasValue);
        Assert.Contains("revenue is zero, negative, or not reported", M(group, "Employee cost % of revenue").InsufficiencyReason);

        Assert.False(M(group, "Material cost % of revenue").HasValue);
        Assert.Contains("revenue is zero, negative, or not reported", M(group, "Material cost % of revenue").InsufficiencyReason);

        Assert.False(M(group, "Export income % of revenue").HasValue);
        Assert.Contains("revenue is zero, negative, or not reported", M(group, "Export income % of revenue").InsufficiencyReason);

        // Net forex exposure does NOT depend on revenue -> still computes
        var a52 = M(group, "Net forex exposure");
        Assert.True(a52.HasValue);
        Assert.Equal(20.0m, a52.Value);
    }

    [Fact]
    public void Parameter_unit_safety_rejects_non_crore_scale()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        var parameters = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Employee benefits expense", NumericValue = 50.0m, Unit = "Rs. Lacs" },
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = 10.0m, Unit = "USD" },
            new() { FinancialYear = 2024, ParameterName = "Expense in foreign currency", NumericValue = 5.0m, Unit = null }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], parameters));

        var a41 = M(group, "Employee cost % of revenue");
        Assert.False(a41.HasValue);
        Assert.Contains("unit is 'Rs. Lacs' (expected Rs. Crore)", a41.InsufficiencyReason);

        var a51 = M(group, "Export income % of revenue");
        Assert.False(a51.HasValue);
        Assert.Contains("unit is 'USD' (expected Rs. Crore)", a51.InsufficiencyReason);

        var a52 = M(group, "Net forex exposure");
        Assert.False(a52.HasValue);
        Assert.Contains("expected Rs. Crore", a52.InsufficiencyReason);
    }

    [Fact]
    public void Fact_section_safety_rejects_non_pnl_facts()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        var facts = new List<FinancialFact>
        {
            // Placed in BalanceSheet instead of ProfitAndLoss
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.BalanceSheet, Label = "Cost of Materials Consumed", NumericValue = 50.0m }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, facts, []));

        var a42 = M(group, "Material cost % of revenue");
        Assert.False(a42.HasValue);
        Assert.Contains("not reported in ProfitAndLoss", a42.InsufficiencyReason);
    }

    [Fact]
    public void Competing_duplicate_facts_with_differing_values_fail_closed()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        var facts = new List<FinancialFact>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = 30.0m },
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of materials consumed", NumericValue = 35.0m }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, facts, []));

        var a42 = M(group, "Material cost % of revenue");
        Assert.False(a42.HasValue);
        Assert.Contains("conflicting values reported for 'Cost of Materials Consumed' in ProfitAndLoss", a42.InsufficiencyReason);
        Assert.Contains("30.0", a42.InsufficiencyReason);
        Assert.Contains("35.0", a42.InsufficiencyReason);
    }

    [Fact]
    public void Duplicate_identical_facts_succeed()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        var facts = new List<FinancialFact>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = 30.0m },
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of materials consumed", NumericValue = 30.0m }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, facts, []));

        var a42 = M(group, "Material cost % of revenue");
        Assert.True(a42.HasValue);
        Assert.Equal(30.0m, a42.Value);
        Assert.Equal("30%", a42.DisplayValue());
    }

    [Fact]
    public void Competing_duplicate_parameters_with_differing_values_fail_closed()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        var parameters = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Employee benefits expense", NumericValue = 20.0m, Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Employee Benefits Expense", NumericValue = 25.0m, Unit = "Rs. Crore" }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], parameters));

        var a41 = M(group, "Employee cost % of revenue");
        Assert.False(a41.HasValue);
        Assert.Contains("conflicting values reported for 'Employee benefits expense'", a41.InsufficiencyReason);
    }

    [Fact]
    public void Dash_parameter_distinguished_from_unreported_parameter()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        // Explicit dash parameter
        var dashParams = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", RawValue = "-", TextValue = "-", NumericValue = null, Unit = "Rs. Crore" }
        };
        var dashGroup = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], dashParams));
        var a51Dash = M(dashGroup, "Export income % of revenue");
        Assert.False(a51Dash.HasValue);
        Assert.Contains("explicitly reported as '-'", a51Dash.InsufficiencyReason);

        // Completely unreported parameter
        var emptyGroup = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], []));
        var a51Missing = M(emptyGroup, "Export income % of revenue");
        Assert.False(a51Missing.HasValue);
        Assert.Equal("FY2024: 'Income in foreign currency' not reported", a51Missing.InsufficiencyReason);
    }

    [Fact]
    public void Competing_duplicate_facts_mixing_numeric_and_non_numeric_fail_closed()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        // Case 1: Numeric vs non-dash non-numeric (e.g. "N/A")
        var factsNa = new List<FinancialFact>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = 30.0m, RawValue = "30.0" },
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = null, RawValue = "N/A" }
        };
        var groupNa = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, factsNa, []));
        var a42Na = M(groupNa, "Material cost % of revenue");
        Assert.False(a42Na.HasValue);
        Assert.Contains("conflicting values reported for 'Cost of Materials Consumed' in ProfitAndLoss (numeric vs non-numeric)", a42Na.InsufficiencyReason);

        // Case 2: Numeric vs explicit dash ("-")
        var factsDash = new List<FinancialFact>
        {
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = 30.0m, RawValue = "30.0" },
            new() { FinancialYear = 2024, Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.ProfitAndLoss, Label = "Cost of Materials Consumed", NumericValue = null, RawValue = "-" }
        };
        var groupDash = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, factsDash, []));
        var a42Dash = M(groupDash, "Material cost % of revenue");
        Assert.False(a42Dash.HasValue);
        Assert.Contains("conflicting values reported for 'Cost of Materials Consumed' in ProfitAndLoss (- vs numeric)", a42Dash.InsufficiencyReason);
    }

    [Fact]
    public void Competing_duplicate_parameters_mixing_numeric_and_non_numeric_fail_closed()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m, Basis = FinancialBasis.Standalone }
        };

        // Case 1: Numeric vs non-dash non-numeric (e.g. "N/A")
        var paramsNa = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = 20.0m, RawValue = "20.0", Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = null, RawValue = "N/A", TextValue = "N/A", Unit = "Rs. Crore" }
        };
        var groupNa = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], paramsNa));
        var a51Na = M(groupNa, "Export income % of revenue");
        Assert.False(a51Na.HasValue);
        Assert.Contains("conflicting values reported for 'Income in foreign currency' (numeric vs non-numeric)", a51Na.InsufficiencyReason);

        // Case 2: Numeric vs explicit dash ("-")
        var paramsDash = new List<FinancialParameter>
        {
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = 20.0m, RawValue = "20.0", Unit = "Rs. Crore" },
            new() { FinancialYear = 2024, ParameterName = "Income in foreign currency", NumericValue = null, RawValue = "-", TextValue = "-", Unit = "Rs. Crore" }
        };
        var groupDash = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], paramsDash));
        var a51Dash = M(groupDash, "Export income % of revenue");
        Assert.False(a51Dash.HasValue);
        Assert.Contains("conflicting values reported for 'Income in foreign currency' (- vs numeric)", a51Dash.InsufficiencyReason);
    }

    [Fact]
    public void Strict_latest_fy_alignment_no_backfilling()
    {
        var years = new List<FinancialYearData>
        {
            new() { FinancialYear = 2023, Revenue = 100.0m, Basis = FinancialBasis.Standalone },
            new() { FinancialYear = 2024, Revenue = 200.0m, Basis = FinancialBasis.Standalone }
        };

        // Parameters only for 2023, none for 2024
        var parameters = new List<FinancialParameter>
        {
            new() { FinancialYear = 2023, ParameterName = "Employee benefits expense", NumericValue = 20.0m, Unit = "Rs. Crore" }
        };

        var group = DossierComputations.FinancialTrendMetrics(CreateMinimalDossier(years, [], parameters));

        var a41 = M(group, "Employee cost % of revenue");
        Assert.False(a41.HasValue);
        Assert.Equal("FY2024: 'Employee benefits expense' not reported", a41.InsufficiencyReason);
    }
}
