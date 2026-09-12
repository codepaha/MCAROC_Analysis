using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CapitalReconciliationMetricsTests
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
        decimal? paidUpCapital,
        List<FinancialYearData>? standalone = null,
        List<FinancialYearData>? consolidated = null,
        DateTime? sourceSnapshotDate = null,
        SheetCoverage? sourceCoverage = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate, sourceSnapshotDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, paidUpCapital, []),
            Financials: new DossierFinancials(standalone ?? [], consolidated ?? [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: sourceCoverage ?? SheetCoverage.Empty,
            Metrics: []);
    }

    private static SheetCoverage CoverageWithStandaloneAbsent()
    {
        var run = new IngestionRun
        {
            AbsentOptionalSheetsJson = JsonSerializer.Serialize(new[]
            {
                SheetAliases.CanonicalName(SheetAliases.StandaloneFinancialData)
            })
        };
        return SheetCoverage.From(run);
    }

    private static MetricResult M(MetricGroup group) =>
        Assert.Single(group.Metrics, m => m.Label == "MCA master-data paid-up capital vs standalone share capital");

    [Fact]
    public void Computes_the_signed_difference_and_names_both_values_and_the_FY()
    {
        var standalone = new List<FinancialYearData> { new() { FinancialYear = 2017, ShareCapital = 330.94m } };
        var model = CreateMinimalDossier(10.01m, standalone);

        var group = DossierComputations.CapitalReconciliationMetrics(model);
        Assert.Equal("Capital reconciliation", group.Title);

        var m = M(group);
        Assert.True(m.HasValue);
        Assert.Equal(-320.93m, m.Value);
        Assert.Contains("FY2017", m.Period);
        Assert.Contains("₹10.01 Cr", m.Period);
        Assert.Contains("₹330.94 Cr", m.Period);
    }

    [Fact]
    public void Negative_and_positive_and_zero_differences_all_produce_an_Ok_result()
    {
        var negative = CreateMinimalDossier(10m, [new() { FinancialYear = 2017, ShareCapital = 50m }]);
        Assert.Equal(-40m, M(DossierComputations.CapitalReconciliationMetrics(negative)).Value);

        var positive = CreateMinimalDossier(50m, [new() { FinancialYear = 2017, ShareCapital = 10m }]);
        Assert.Equal(40m, M(DossierComputations.CapitalReconciliationMetrics(positive)).Value);

        var zero = CreateMinimalDossier(25m, [new() { FinancialYear = 2017, ShareCapital = 25m }]);
        Assert.Equal(0m, M(DossierComputations.CapitalReconciliationMetrics(zero)).Value);
    }

    [Fact]
    public void A_reported_zero_on_either_side_is_a_valid_value_not_insufficient()
    {
        // Zero paid-up capital, a real (if unusual) reported figure — never treated as "missing."
        var zeroProfile = CreateMinimalDossier(0m, [new() { FinancialYear = 2017, ShareCapital = 25m }]);
        var m1 = M(DossierComputations.CapitalReconciliationMetrics(zeroProfile));
        Assert.True(m1.HasValue);
        Assert.Equal(-25m, m1.Value);

        var zeroShareCapital = CreateMinimalDossier(10m, [new() { FinancialYear = 2017, ShareCapital = 0m }]);
        var m2 = M(DossierComputations.CapitalReconciliationMetrics(zeroShareCapital));
        Assert.True(m2.HasValue);
        Assert.Equal(10m, m2.Value);
    }

    [Fact]
    public void Uses_the_latest_standalone_year_and_never_reads_consolidated_data()
    {
        var standalone = new List<FinancialYearData>
        {
            new() { FinancialYear = 2015, ShareCapital = 100m },
            new() { FinancialYear = 2017, ShareCapital = 50m },
            new() { FinancialYear = 2016, ShareCapital = 200m },
        };
        // Consolidated has a later/larger figure that must never leak into this metric.
        var consolidated = new List<FinancialYearData> { new() { FinancialYear = 2020, ShareCapital = 9999m } };
        var model = CreateMinimalDossier(10m, standalone, consolidated);

        var m = M(DossierComputations.CapitalReconciliationMetrics(model));
        Assert.True(m.HasValue);
        Assert.Equal(-40m, m.Value); // 10 - 50 (FY2017, the latest STANDALONE year)
        Assert.Contains("FY2017", m.Period);
    }

    [Fact]
    public void Is_insufficient_when_paid_up_capital_is_not_reported()
    {
        var model = CreateMinimalDossier(null, [new() { FinancialYear = 2017, ShareCapital = 50m }]);
        var m = M(DossierComputations.CapitalReconciliationMetrics(model));
        Assert.False(m.HasValue);
        Assert.Contains("Paid-up capital not reported", m.InsufficiencyReason);
    }

    [Fact]
    public void Is_insufficient_with_no_standalone_data_and_the_sheet_was_absent()
    {
        var model = CreateMinimalDossier(10m, standalone: [], sourceCoverage: CoverageWithStandaloneAbsent());
        var m = M(DossierComputations.CapitalReconciliationMetrics(model));
        Assert.False(m.HasValue);
        Assert.Contains("not in this upload", m.InsufficiencyReason);
    }

    [Fact]
    public void Is_insufficient_with_no_standalone_data_and_the_sheet_was_present_but_empty()
    {
        // Sheet coverage says the sheet WAS present (SheetCoverage.Empty = nothing tracked as absent),
        // but zero years were extracted — a different reason than "not in this upload".
        var model = CreateMinimalDossier(10m, standalone: []);
        var m = M(DossierComputations.CapitalReconciliationMetrics(model));
        Assert.False(m.HasValue);
        Assert.DoesNotContain("not in this upload", m.InsufficiencyReason);
        Assert.Contains("No standalone financial year data", m.InsufficiencyReason);
    }

    [Fact]
    public void Is_insufficient_when_the_latest_standalone_year_has_no_ShareCapital_reported()
    {
        // The sheet and the FY row are present — only the Share Capital line itself is missing.
        var model = CreateMinimalDossier(10m, [new() { FinancialYear = 2017, ShareCapital = null }]);
        var m = M(DossierComputations.CapitalReconciliationMetrics(model));
        Assert.False(m.HasValue);
        Assert.Contains("FY2017", m.InsufficiencyReason);
        Assert.Contains("Share Capital row not reported", m.InsufficiencyReason);
    }

    [Fact]
    public void Names_the_source_snapshot_date_when_available()
    {
        var snap = new DateTime(2022, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var model = CreateMinimalDossier(10.01m, [new() { FinancialYear = 2017, ShareCapital = 330.94m }], sourceSnapshotDate: snap);

        var m = M(DossierComputations.CapitalReconciliationMetrics(model));
        Assert.Contains("as of 15 Jun 2022", m.Period);
    }

    [Fact]
    public void Omits_the_as_of_date_when_no_source_snapshot_date_is_available()
    {
        var model = CreateMinimalDossier(10.01m, [new() { FinancialYear = 2017, ShareCapital = 330.94m }]);
        var m = M(DossierComputations.CapitalReconciliationMetrics(model));
        Assert.DoesNotContain("as of", m.Period);
    }

    [SkippableFact]
    public void Coastal_fixture_exact_capital_reconciliation()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);
        var aboutSheet = sheets.Single(s => s.Name == "About the Company");
        var profile = CompanyProfileParser.Parse(aboutSheet, 1, 1, 1).Items.Single();

        var standaloneSheet = sheets.Single(s => s.Name == "Standalone Financial Data");
        var standalone = StandaloneFinancialDataParser.Parse(standaloneSheet, 1, 1, 1, out _).Items.ToList();

        var model = CreateMinimalDossier(profile.PaidUpCapital, standalone);
        var group = DossierComputations.CapitalReconciliationMetrics(model);

        var m = M(group);
        Assert.True(m.HasValue);
        // PaidUpCapital=10.01 (About the Company) vs FY2017 (latest) ShareCapital=330.94 (Balance Sheet)
        // — a real, substantial discrepancy in the reference company's own data.
        Assert.Equal(-320.93m, m.Value);
        Assert.Contains("FY2017", m.Period);
    }
}
