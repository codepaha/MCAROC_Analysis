using System.Globalization;
using System.Text;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class EpfoMetricsTests
{
    public EpfoMetricsTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
        List<EpfoContribution> contribs,
        List<EpfoEstablishment>? establishments = null,
        List<FinancialYearData>? standaloneFinancials = null,
        DateTime? asOf = null)
    {
        var reportDate = asOf ?? new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, []),
            Financials: new DossierFinancials(standaloneFinancials ?? [], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], contribs, establishments ?? [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    [Fact]
    public void TryParseWageMonth_parses_supported_formats_and_anchors_to_first_of_month()
    {
        Assert.True(DossierComputations.TryParseWageMonth("May, 2026", out var m1));
        Assert.Equal(new DateOnly(2026, 5, 1), m1);

        Assert.True(DossierComputations.TryParseWageMonth("May 2026", out var m2));
        Assert.Equal(new DateOnly(2026, 5, 1), m2);

        Assert.True(DossierComputations.TryParseWageMonth("May-2026", out var m3));
        Assert.Equal(new DateOnly(2026, 5, 1), m3);

        Assert.True(DossierComputations.TryParseWageMonth("2026-05", out var m4));
        Assert.Equal(new DateOnly(2026, 5, 1), m4);

        Assert.True(DossierComputations.TryParseWageMonth("05-2026", out var m5));
        Assert.Equal(new DateOnly(2026, 5, 1), m5);

        Assert.True(DossierComputations.TryParseWageMonth("05/2026", out var m6));
        Assert.Equal(new DateOnly(2026, 5, 1), m6);

        Assert.True(DossierComputations.TryParseWageMonth("September, 2025", out var m7));
        Assert.Equal(new DateOnly(2025, 9, 1), m7);

        Assert.True(DossierComputations.TryParseWageMonth("December 2016", out var m8));
        Assert.Equal(new DateOnly(2016, 12, 1), m8);

        // Invalid or ambiguous inputs must fail
        Assert.False(DossierComputations.TryParseWageMonth(null, out _));
        Assert.False(DossierComputations.TryParseWageMonth("", out _));
        Assert.False(DossierComputations.TryParseWageMonth("   ", out _));
        Assert.False(DossierComputations.TryParseWageMonth("-", out _));
        Assert.False(DossierComputations.TryParseWageMonth("invalid", out _));
        Assert.False(DossierComputations.TryParseWageMonth("2026", out _));
    }

    [Fact]
    public void StatusVocabulary_accurately_classifies_live_closed_and_not_live()
    {
        Assert.True(DossierComputations.IsLiveEpfoStatus("LIVE ESTABLISHMENT"));
        Assert.True(DossierComputations.IsLiveEpfoStatus("LIVE"));
        Assert.True(DossierComputations.IsLiveEpfoStatus("Working"));
        Assert.True(DossierComputations.IsLiveEpfoStatus("Active"));

        // Critical regression: "NOT LIVE" must NEVER be classified as live!
        Assert.False(DossierComputations.IsLiveEpfoStatus("NOT LIVE"));
        Assert.True(DossierComputations.IsClosedEpfoStatus("NOT LIVE"));

        Assert.True(DossierComputations.IsClosedEpfoStatus("CLOSED/ALL MEMBER EXCLUDED"));
        Assert.True(DossierComputations.IsClosedEpfoStatus("CLOSED"));
        Assert.True(DossierComputations.IsClosedEpfoStatus("INACTIVE"));
        Assert.True(DossierComputations.IsClosedEpfoStatus("DE-REGISTERED"));

        // Unknown status
        Assert.False(DossierComputations.IsLiveEpfoStatus("PENDING"));
        Assert.False(DossierComputations.IsClosedEpfoStatus("PENDING"));
    }

    [Fact]
    public void HasSubstantiveEpfoFlag_and_IsValidEpfoCity_filter_placeholders()
    {
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag(null));
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag(""));
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag("   "));
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag("-"));
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag("--"));
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag("N/A"));
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag("None"));
        Assert.False(DossierComputations.HasSubstantiveEpfoFlag("Nil"));
        Assert.True(DossierComputations.HasSubstantiveEpfoFlag("Payment After Due Date in last 12 Months"));

        Assert.False(DossierComputations.IsValidEpfoCity(null));
        Assert.False(DossierComputations.IsValidEpfoCity(""));
        Assert.False(DossierComputations.IsValidEpfoCity("   "));
        Assert.False(DossierComputations.IsValidEpfoCity("-"));
        Assert.False(DossierComputations.IsValidEpfoCity("N/A"));
        Assert.False(DossierComputations.IsValidEpfoCity("UNKNOWN"));
        Assert.True(DossierComputations.IsValidEpfoCity("BHUBANESWAR"));
        Assert.True(DossierComputations.IsValidEpfoCity("Kolkata"));
    }

    [Fact]
    public void H1_and_H2_remittance_record_semantics_handle_on_time_late_and_indeterminate()
    {
        var contribs = new List<EpfoContribution>
        {
            new() { WageMonth = "Jan, 2026", PaymentDueDate = new DateOnly(2026, 2, 15), PaymentDate = new DateOnly(2026, 2, 10) }, // on-time
            new() { WageMonth = "Feb, 2026", PaymentDueDate = new DateOnly(2026, 3, 15), PaymentDate = new DateOnly(2026, 3, 15) }, // on-time (same day)
            new() { WageMonth = "Mar, 2026", PaymentDueDate = new DateOnly(2026, 4, 15), PaymentDate = new DateOnly(2026, 4, 20) }, // late
            new() { WageMonth = "Apr, 2026", PaymentDueDate = null, PaymentDate = new DateOnly(2026, 5, 10) },                       // indeterminate
            new() { WageMonth = "May, 2026", PaymentDueDate = new DateOnly(2026, 6, 15), PaymentDate = null }                        // indeterminate
        };

        var model = CreateMinimalDossier(contribs);
        var group = DossierComputations.EpfoMetrics(model);

        var h1 = Assert.Single(group.Metrics, m => m.Label == "PF remittance on-time rate");
        Assert.True(h1.HasValue);
        Assert.Equal(66.7m, h1.Value); // 2 on-time / 3 assessed = 66.7%
        Assert.Equal(MetricUnit.Percent, h1.Unit);
        Assert.Equal("2 on-time, 1 late of 3 assessed remittances (2 indeterminate)", h1.Period);

        var h2 = Assert.Single(group.Metrics, m => m.Label == "PF late-remittance count");
        Assert.True(h2.HasValue);
        Assert.Equal(1m, h2.Value);
        Assert.Equal(MetricUnit.Count, h2.Unit);
        Assert.Equal("1 late remittance(s) of 3 assessed remittances (2 indeterminate)", h2.Period);
    }

    [Fact]
    public void H1_and_H2_fail_closed_when_zero_remittances_assessed()
    {
        var contribs = new List<EpfoContribution>
        {
            new() { WageMonth = "Jan, 2026", PaymentDueDate = null, PaymentDate = null }
        };

        var model = CreateMinimalDossier(contribs);
        var group = DossierComputations.EpfoMetrics(model);

        var h1 = Assert.Single(group.Metrics, m => m.Label == "PF remittance on-time rate");
        Assert.False(h1.HasValue);
        Assert.Contains("0 of 1 remittance records have both payment and due dates", h1.InsufficiencyReason);

        var h2 = Assert.Single(group.Metrics, m => m.Label == "PF late-remittance count");
        Assert.False(h2.HasValue);
        Assert.Contains("indeterminate", h2.InsufficiencyReason);
    }

    [Fact]
    public void H3_and_H4_fail_closed_on_incomplete_multi_establishment_data()
    {
        // Two establishments reporting for the same latest month May, 2026:
        // Est1 has 5 employees, 0.10 Cr
        // Est2 has null employees, 0.05 Cr
        var contribs = new List<EpfoContribution>
        {
            new() { EstablishmentId = "EST1", WageMonth = "May, 2026", EmployeeCount = 5, ContributionAmountCrore = 0.10m },
            new() { EstablishmentId = "EST2", WageMonth = "May, 2026", EmployeeCount = null, ContributionAmountCrore = 0.05m }
        };

        var model = CreateMinimalDossier(contribs);
        var group = DossierComputations.EpfoMetrics(model);

        // H3 is valid because both have ContributionAmountCrore
        var h3 = Assert.Single(group.Metrics, m => m.Label == "Latest-month PF contribution");
        Assert.True(h3.HasValue);
        Assert.Equal(0.15m, h3.Value);

        // H4 must FAIL CLOSED because Est2 is missing EmployeeCount (cannot emit partial sum of 5)
        var h4 = Assert.Single(group.Metrics, m => m.Label == "Employee count (EPFO) + trend");
        Assert.False(h4.HasValue);
        Assert.Contains("Incomplete data: 1 of 2 establishment records in May 2026 missing EmployeeCount", h4.InsufficiencyReason);

        // H5 must also fail closed because H4 is insufficient
        var h5 = Assert.Single(group.Metrics, m => m.Label == "PF contribution per recorded EPFO employee (latest month)");
        Assert.False(h5.HasValue);
        Assert.Contains("Latest-month employee count is insufficient", h5.InsufficiencyReason);
    }

    [Fact]
    public void H3_fails_closed_when_establishment_missing_contribution_amount()
    {
        var contribs = new List<EpfoContribution>
        {
            new() { EstablishmentId = "EST1", WageMonth = "May, 2026", EmployeeCount = 5, ContributionAmountCrore = 0.10m },
            new() { EstablishmentId = "EST2", WageMonth = "May, 2026", EmployeeCount = 10, ContributionAmountCrore = null }
        };

        var model = CreateMinimalDossier(contribs);
        var group = DossierComputations.EpfoMetrics(model);

        var h3 = Assert.Single(group.Metrics, m => m.Label == "Latest-month PF contribution");
        Assert.False(h3.HasValue);
        Assert.Contains("Incomplete data: 1 of 2 establishment records in May 2026 missing ContributionAmount", h3.InsufficiencyReason);

        var h5 = Assert.Single(group.Metrics, m => m.Label == "PF contribution per recorded EPFO employee (latest month)");
        Assert.False(h5.HasValue);
        Assert.Contains("Latest-month contribution amount is insufficient", h5.InsufficiencyReason);
    }

    [Fact]
    public void H4_trend_discloses_incomplete_prior_month_data_explicitly()
    {
        var contribs = new List<EpfoContribution>
        {
            // Latest month: complete
            new() { EstablishmentId = "EST1", WageMonth = "May, 2026", EmployeeCount = 10, ContributionAmountCrore = 0.1m },
            // 12m prior month: May 2025 has one establishment with null headcount
            new() { EstablishmentId = "EST1", WageMonth = "May, 2025", EmployeeCount = 8, ContributionAmountCrore = 0.1m },
            new() { EstablishmentId = "EST2", WageMonth = "May, 2025", EmployeeCount = null, ContributionAmountCrore = 0.1m }
        };

        var model = CreateMinimalDossier(contribs);
        var group = DossierComputations.EpfoMetrics(model);

        var h4 = Assert.Single(group.Metrics, m => m.Label == "Employee count (EPFO) + trend");
        Assert.True(h4.HasValue);
        Assert.Equal(10m, h4.Value);
        Assert.Contains("trend not assessed due to incomplete prior-month data", h4.Period);
    }

    [Fact]
    public void H5_computes_inr_per_employee_and_discloses_proxy_caveat()
    {
        var contribs = new List<EpfoContribution>
        {
            new() { EstablishmentId = "EST1", WageMonth = "May, 2026", EmployeeCount = 20, ContributionAmountCrore = 0.02m } // 0.02 Cr = 200,000 INR -> 10,000 INR/emp
        };

        var model = CreateMinimalDossier(contribs);
        var group = DossierComputations.EpfoMetrics(model);

        var h5 = Assert.Single(group.Metrics, m => m.Label == "PF contribution per recorded EPFO employee (latest month)");
        Assert.True(h5.HasValue);
        Assert.Equal(10_000m, h5.Value);
        Assert.Equal(MetricUnit.Rupees, h5.Unit);
        Assert.Contains("proxy only, not salary", h5.Period);
    }

    [Fact]
    public void H6_revenue_per_employee_strictly_matches_financial_year_period()
    {
        // FY2024 revenue: 100.0 Cr (FY2024 runs 1 Apr 2023 to 31 Mar 2024)
        var financials = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m }
        };

        var contribs = new List<EpfoContribution>
        {
            new() { EstablishmentId = "EST1", WageMonth = "Oct, 2023", EmployeeCount = 25, ContributionAmountCrore = 0.05m }, // in FY2024
            new() { EstablishmentId = "EST1", WageMonth = "Mar, 2024", EmployeeCount = 50, ContributionAmountCrore = 0.05m }, // latest in FY2024 -> 50 emps
            new() { EstablishmentId = "EST1", WageMonth = "May, 2026", EmployeeCount = 5, ContributionAmountCrore = 0.01m }   // 2 years later! Must NOT be used.
        };

        var model = CreateMinimalDossier(contribs, standaloneFinancials: financials);
        var group = DossierComputations.EpfoMetrics(model);

        var h6 = Assert.Single(group.Metrics, m => m.Label == "Revenue per EPFO employee");
        Assert.True(h6.HasValue);
        Assert.Equal(2.00m, h6.Value); // 100.0 / 50 = 2.00 Cr / emp (Mar 2024), NOT 100 / 5 = 20.0 Cr (May 2026)
        Assert.Equal(MetricUnit.Crore, h6.Unit);
        Assert.Contains("FY2024 revenue (₹100.00 Cr) vs Mar 2024 EPFO headcount (50 employees)", h6.Period);
        Assert.Contains("EPFO headcount ≠ total headcount", h6.Period);
    }

    [Fact]
    public void H6_fails_closed_when_no_epfo_data_in_matching_financial_year()
    {
        // FY2024 revenue (1 Apr 2023 to 31 Mar 2024), but EPFO data is only in 2026
        var financials = new List<FinancialYearData>
        {
            new() { FinancialYear = 2024, Revenue = 100.0m }
        };

        var contribs = new List<EpfoContribution>
        {
            new() { EstablishmentId = "EST1", WageMonth = "May, 2026", EmployeeCount = 5, ContributionAmountCrore = 0.01m }
        };

        var model = CreateMinimalDossier(contribs, standaloneFinancials: financials);
        var group = DossierComputations.EpfoMetrics(model);

        var h6 = Assert.Single(group.Metrics, m => m.Label == "Revenue per EPFO employee");
        Assert.False(h6.HasValue);
        Assert.Contains("No EPFO contribution records found in matching financial year (FY2024: Apr 2023 – Mar 2024)", h6.InsufficiencyReason);
    }

    [Fact]
    public void H7_accurately_counts_live_units_valid_locations_and_substantive_flags()
    {
        var establishments = new List<EpfoEstablishment>
        {
            new() { EstablishmentId = "E1", WorkingStatus = "LIVE ESTABLISHMENT", City = "Bhubaneswar", Flags = "Payment After Due Date in last 12 Months" },
            new() { EstablishmentId = "E2", WorkingStatus = "WORKING", City = "Kolkata", Flags = "-" },
            new() { EstablishmentId = "E3", WorkingStatus = "ACTIVE", City = "Bhubaneswar", Flags = "N/A" },
            new() { EstablishmentId = "E4", WorkingStatus = "CLOSED", City = "Joda", Flags = null },
            new() { EstablishmentId = "E5", WorkingStatus = "NOT LIVE", City = "-", Flags = "--" },       // NOT LIVE is closed, "-" is placeholder city
            new() { EstablishmentId = "E6", WorkingStatus = "PENDING_VERIF", City = null, Flags = "Audit pending" } // Unknown status, unknown city, real flag
        };

        var model = CreateMinimalDossier([], establishments: establishments);
        var group = DossierComputations.EpfoMetrics(model);

        var h7 = Assert.Single(group.Metrics, m => m.Label == "Establishment count + locations + flags");
        Assert.True(h7.HasValue);
        Assert.Equal(3m, h7.Value); // 3 live (E1, E2, E3)
        Assert.Equal(MetricUnit.Count, h7.Unit);

        // 3 live, 2 closed (E4, E5), 1 unknown (E6) of 6 establishments
        // Valid distinct cities: Bhubaneswar, Kolkata, Joda -> 3 locations (2 unknown city: E5 has '-', E6 has null)
        // Substantive flags: E1, E6 -> 2 flagged
        Assert.Contains("3 live, 2 closed, 1 unknown of 6 establishment(s)", h7.Period);
        Assert.Contains("3 location(s) (2 unknown city)", h7.Period);
        Assert.Contains("2 flagged", h7.Period);
    }

    [SkippableFact]
    public void EpfoMetrics_reconciles_against_real_coastal_fixture()
    {
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);
        var epfoSheet = sheets.Single(s => s.Name == "Annexure - EPFO Establishments");
        var estSheet = sheets.Single(s => s.Name == "EPFO Establishments");
        var finSheet = sheets.Single(s => s.Name == "Standalone Financial Data");

        var contribs = EpfoParser.Parse(epfoSheet, 1, 1, 1).Items;
        var ests = EpfoParser.ParseEstablishments(estSheet, 1, 1, 1).Items;
        var financials = StandaloneFinancialDataParser.Parse(finSheet, 1, 1, 1, out _).Items;

        Assert.Equal(77, contribs.Count);
        Assert.Equal(4, ests.Count);
        Assert.Equal(4, financials.Count);

        var model = CreateMinimalDossier(contribs, ests, financials);
        var group = DossierComputations.EpfoMetrics(model);

        Assert.Equal("EPFO / labour", group.Title);
        Assert.True(group.HasAny);

        // H1: PF remittance on-time rate = 16 on-time / 77 assessed = 20.8%
        var h1 = Assert.Single(group.Metrics, m => m.Label == "PF remittance on-time rate");
        Assert.True(h1.HasValue);
        Assert.Equal(20.8m, h1.Value);
        Assert.Equal(MetricUnit.Percent, h1.Unit);
        Assert.Equal("16 on-time, 61 late of 77 assessed remittances", h1.Period);

        // H2: PF late-remittance count = 61
        var h2 = Assert.Single(group.Metrics, m => m.Label == "PF late-remittance count");
        Assert.True(h2.HasValue);
        Assert.Equal(61m, h2.Value);
        Assert.Equal(MetricUnit.Count, h2.Unit);
        Assert.Equal("61 late remittance(s) of 77 assessed remittances", h2.Period);

        // H3: Latest-month PF contribution = May 2026 -> 0.00 Cr
        var h3 = Assert.Single(group.Metrics, m => m.Label == "Latest-month PF contribution");
        Assert.True(h3.HasValue);
        Assert.Equal(0m, h3.Value);
        Assert.Equal(MetricUnit.Crore, h3.Unit);
        Assert.Equal("May 2026 (1 establishment(s))", h3.Period);

        // H4: Employee count (EPFO) + trend = May 2026 -> 5 employees, delta vs May 2025 (7 employees) = -2
        var h4 = Assert.Single(group.Metrics, m => m.Label == "Employee count (EPFO) + trend");
        Assert.True(h4.HasValue);
        Assert.Equal(5m, h4.Value);
        Assert.Equal(MetricUnit.Count, h4.Unit);
        Assert.Equal("May 2026 (-2 vs May 2025: 7)", h4.Period);

        // H5: PF contribution per recorded EPFO employee = 0 INR
        var h5 = Assert.Single(group.Metrics, m => m.Label == "PF contribution per recorded EPFO employee (latest month)");
        Assert.True(h5.HasValue);
        Assert.Equal(0m, h5.Value);
        Assert.Equal(MetricUnit.Rupees, h5.Unit);
        Assert.Contains("proxy only, not salary", h5.Period);

        // H6: Revenue per EPFO employee = FY2017 (1284.20 Cr) / Mar 2017 headcount (46 emps) = 27.92 Cr / emp
        var h6 = Assert.Single(group.Metrics, m => m.Label == "Revenue per EPFO employee");
        Assert.True(h6.HasValue);
        Assert.Equal(27.92m, h6.Value);
        Assert.Equal(MetricUnit.Crore, h6.Unit);
        Assert.Equal("FY2017 revenue (₹1,284.20 Cr) vs Mar 2017 EPFO headcount (46 employees) (EPFO headcount ≠ total headcount)", h6.Period);

        // H7: Establishment count + locations + flags = 3 live, 1 closed across 4 locations (BHUBANESWAR, KOLKATA, DHALAI, JODA); 3 flagged
        var h7 = Assert.Single(group.Metrics, m => m.Label == "Establishment count + locations + flags");
        Assert.True(h7.HasValue);
        Assert.Equal(3m, h7.Value);
        Assert.Equal(MetricUnit.Count, h7.Unit);
        Assert.Equal("3 live, 1 closed of 4 establishment(s) across 4 location(s); 3 flagged", h7.Period);
    }
}
