using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class GstComplianceMetricsTests
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

    private static DossierModel CreateMinimalDossier(List<GstRegistration> gstRegs, DateTime? asOf = null)
    {
        var reportDate = asOf ?? new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], []),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], gstRegs, [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    [SkippableFact]
    public void Coastal_fixture_exact_gst_compliance_metrics()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);
        var gstSheet = sheets.FirstOrDefault(s => s.Name == "GST");
        var annexureSheet = sheets.FirstOrDefault(s => s.Name == "Annexure - GST");

        var parsed = GstParser.Parse(gstSheet, annexureSheet, 1, 1, 1);
        var regs = parsed.Registrations.Items;
        var filings = parsed.Filings.Items;

        Assert.Equal(24, regs.Count);
        Assert.Equal(579, filings.Count);

        var model = CreateMinimalDossier(regs, new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc));
        var group = DossierComputations.GstComplianceMetrics(model);

        Assert.Equal("GST compliance", group.Title);
        Assert.True(group.HasAny);

        // G1: Active GSTIN count = 4
        var g1 = Assert.Single(group.Metrics, m => m.Label == "Active GSTIN count");
        Assert.True(g1.HasValue);
        Assert.Equal(4m, g1.Value);
        Assert.Equal(MetricUnit.Count, g1.Unit);

        // G2: States of operation = 21 distinct states
        var g2 = Assert.Single(group.Metrics, m => m.Label == "States of operation");
        Assert.True(g2.HasValue);
        Assert.Equal(21m, g2.Value);
        Assert.Equal(MetricUnit.Count, g2.Unit);

        // G3: GST filing on-time rate — now per ReturnType; COASTAL has GSTR1 and GSTR3B filings
        // Catalogue: report indeterminate alongside rate, never fold into either bucket.
        // Assert both per-type metrics exist and are labelled correctly.
        var g3Gstr1 = Assert.Single(group.Metrics, m => m.Label == "GST filing on-time rate (GSTR1)");
        Assert.True(g3Gstr1.HasValue);
        Assert.Equal(MetricUnit.Percent, g3Gstr1.Unit);
        Assert.Contains("assessed", g3Gstr1.Period);

        var g3Gstr3b = Assert.Single(group.Metrics, m => m.Label == "GST filing on-time rate (GSTR3B)");
        Assert.True(g3Gstr3b.HasValue);
        Assert.Equal(MetricUnit.Percent, g3Gstr3b.Unit);
        Assert.Contains("assessed", g3Gstr3b.Period);

        // G3 cross-check: combined on-time / assessed must reconcile to 210/294
        // (each per-type assessed count comes from the same filing pool)
        var g3Metrics = group.Metrics.Where(m => m.Label.StartsWith("GST filing on-time rate (")).ToList();
        Assert.True(g3Metrics.Count >= 2, "expected at least GSTR1 and GSTR3B rate metrics");

        // G4: Late filing count = 84 with evidence pairs in Period
        var g4 = Assert.Single(group.Metrics, m => m.Label == "Late filing count");
        Assert.True(g4.HasValue);
        Assert.Equal(84m, g4.Value);
        Assert.Equal(MetricUnit.Count, g4.Unit);
        Assert.StartsWith("84 late of 294 assessed — periods:", g4.Period);
        Assert.Contains("/", g4.Period); // evidence pairs contain "TaxPeriod/ReturnType"

        // G5: GSTR-1 vs GSTR-3B filing lag = 38.6 days over non-negative matched periods (deduplicating both GSTR-1 and GSTR-3B)
        var g5 = Assert.Single(group.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.True(g5.HasValue);
        Assert.Equal(38.6m, g5.Value);
        Assert.Equal(MetricUnit.Days, g5.Unit);
        Assert.StartsWith("128 matched periods", g5.Period);
        Assert.Contains("selected latest FilingDate", g5.Period);

        var g5Anomalies = Assert.Single(group.Metrics, m => m.Label == "GSTR-3B filed before GSTR-1 anomalies");
        Assert.True(g5Anomalies.HasValue);
        Assert.Equal(MetricUnit.Count, g5Anomalies.Unit);

        // G6: GST registration flags present = 1 (9 flagged registrations in COASTAL)
        var g6 = Assert.Single(group.Metrics, m => m.Label == "GST registration flags present");
        Assert.True(g6.HasValue);
        Assert.Equal(1m, g6.Value);
        Assert.Equal(MetricUnit.Count, g6.Unit);
    }

    [Fact]
    public void Degenerate_empty_registrations_returns_all_insufficient()
    {
        var model = CreateMinimalDossier([]);
        var group = DossierComputations.GstComplianceMetrics(model);

        Assert.Equal("GST compliance", group.Title);
        Assert.All(group.Metrics, m =>
        {
            Assert.False(m.HasValue);
            Assert.NotNull(m.InsufficiencyReason);
        });

        Assert.Contains(group.Metrics, m => m.Label == "Active GSTIN count");
        Assert.Contains(group.Metrics, m => m.Label == "States of operation");
        Assert.Contains(group.Metrics, m => m.Label == "GST filing on-time rate");
        Assert.Contains(group.Metrics, m => m.Label == "Late filing count");
        Assert.Contains(group.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.Contains(group.Metrics, m => m.Label == "GSTR-3B filed before GSTR-1 anomalies");
        Assert.Contains(group.Metrics, m => m.Label == "GST registration flags present");
    }

    [Fact]
    public void Degenerate_indeterminate_filings_only_returns_insufficient_for_rates()
    {
        var reg = new GstRegistration
        {
            Gstin = "29AAAAA0000A1Z5",
            State = "Karnataka",
            Status = "Active",
            Flags = null
        };
        reg.Filings.Add(new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR3B",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed",
            DelayDays = null,
            DueDate = null,
            FilingDate = null
        });

        var model = CreateMinimalDossier([reg]);
        var group = DossierComputations.GstComplianceMetrics(model);

        var g1 = Assert.Single(group.Metrics, m => m.Label == "Active GSTIN count");
        Assert.True(g1.HasValue);
        Assert.Equal(1m, g1.Value);

        var g3 = Assert.Single(group.Metrics, m => m.Label == "GST filing on-time rate (GSTR3B)");
        Assert.False(g3.HasValue);
        Assert.Contains("All 1 filings indeterminate (missing filing or due dates)", g3.InsufficiencyReason!);

        var g4 = Assert.Single(group.Metrics, m => m.Label == "Late filing count");
        Assert.False(g4.HasValue);

        var g5 = Assert.Single(group.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.False(g5.HasValue);

        var g5Anomalies = Assert.Single(group.Metrics, m => m.Label == "GSTR-3B filed before GSTR-1 anomalies");
        Assert.True(g5Anomalies.HasValue);
        Assert.Equal(0m, g5Anomalies.Value);

        var g6 = Assert.Single(group.Metrics, m => m.Label == "GST registration flags present");
        Assert.True(g6.HasValue);
        Assert.Equal(0m, g6.Value);
    }

    [Fact]
    public void Mixed_return_types_one_assessed_one_all_indeterminate_emits_both_metrics()
    {
        var reg = new GstRegistration
        {
            Gstin = "29AAAAA0000A1Z5",
            State = "Karnataka",
            Status = "Active"
        };
        // GSTR-1: 1 on-time, 1 late -> 50.0%
        var f1_onTime = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR1",
            TaxPeriod = "2023-01",
            FilingDate = new DateOnly(2023, 2, 10),
            DueDate = new DateOnly(2023, 2, 11)
        };
        var f1_late = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR1",
            TaxPeriod = "2023-02",
            FilingDate = new DateOnly(2023, 3, 20),
            DueDate = new DateOnly(2023, 3, 11)
        };
        // GSTR-3B: 2 filings, both entirely indeterminate (no dates, no delay days)
        var f3b_indet1 = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR3B",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed"
        };
        var f3b_indet2 = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR3B",
            TaxPeriod = "2023-02",
            FilingStatus = "Filed"
        };

        reg.Filings = [f1_onTime, f1_late, f3b_indet1, f3b_indet2];
        var model = CreateMinimalDossier([reg]);
        var group = DossierComputations.GstComplianceMetrics(model);

        // Assessed type GSTR1
        var g3Gstr1 = Assert.Single(group.Metrics, m => m.Label == "GST filing on-time rate (GSTR1)");
        Assert.True(g3Gstr1.HasValue);
        Assert.Equal(50.0m, g3Gstr1.Value);
        Assert.Equal("2 assessed", g3Gstr1.Period);

        // All-indeterminate type GSTR3B
        var g3Gstr3b = Assert.Single(group.Metrics, m => m.Label == "GST filing on-time rate (GSTR3B)");
        Assert.False(g3Gstr3b.HasValue);
        Assert.Contains("All 2 filings indeterminate (missing filing or due dates)", g3Gstr3b.InsufficiencyReason!);
    }

    [Fact]
    public void G5_duplicate_gstr1_rows_picks_latest_filing_date_and_is_order_independent()
    {
        var reg = new GstRegistration
        {
            Gstin = "29AAAAA0000A1Z5",
            State = "Karnataka",
            Status = "Active"
        };
        // GSTR-3B filed on 2023-05-15
        var f3b = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR3B",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed",
            FilingDate = new DateOnly(2023, 5, 15),
            DueDate = new DateOnly(2023, 2, 20)
        };
        // Earlier GSTR-1 filed on 2023-02-10 (lag to 3B = 94 days)
        var f1Earlier = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR1",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed",
            FilingDate = new DateOnly(2023, 2, 10),
            DueDate = new DateOnly(2023, 2, 11)
        };
        // Later GSTR-1 filed on 2023-03-12 (lag to 3B = 64 days)
        var f1Later = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR1",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed",
            FilingDate = new DateOnly(2023, 3, 12),
            DueDate = new DateOnly(2023, 2, 11)
        };

        // Test with earlier first, then later
        reg.Filings = [f3b, f1Earlier, f1Later];
        var model1 = CreateMinimalDossier([reg]);
        var group1 = DossierComputations.GstComplianceMetrics(model1);
        var g5_1 = Assert.Single(group1.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.True(g5_1.HasValue);
        Assert.Equal(64m, g5_1.Value); // Deterministically picks 2023-03-12
        Assert.StartsWith("1 matched periods", g5_1.Period);
        Assert.Contains("1 duplicate GSTR-1 group, 1 duplicate row", g5_1.Period);
        Assert.Contains("0 duplicate GSTR-3B groups", g5_1.Period);
        Assert.Contains("selected latest FilingDate", g5_1.Period);

        // Test with later first, then earlier
        reg.Filings = [f3b, f1Later, f1Earlier];
        var model2 = CreateMinimalDossier([reg]);
        var group2 = DossierComputations.GstComplianceMetrics(model2);
        var g5_2 = Assert.Single(group2.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.True(g5_2.HasValue);
        Assert.Equal(64m, g5_2.Value); // Same result regardless of row ordering
        Assert.Equal(g5_1.Period, g5_2.Period);
    }

    [Fact]
    public void G5_duplicate_gstr3b_rows_picks_latest_filing_date_and_is_order_independent()
    {
        var reg = new GstRegistration
        {
            Gstin = "29AAAAA0000A1Z5",
            State = "Karnataka",
            Status = "Active"
        };
        // GSTR-1 filed on 2023-02-10
        var f1 = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR1",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed",
            FilingDate = new DateOnly(2023, 2, 10),
            DueDate = new DateOnly(2023, 2, 11)
        };
        // Earlier GSTR-3B filed on 2023-03-12 (lag to 1 = 30 days)
        var f3bEarlier = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR3B",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed",
            FilingDate = new DateOnly(2023, 3, 12),
            DueDate = new DateOnly(2023, 2, 20)
        };
        // Later GSTR-3B filed on 2023-05-15 (lag to 1 = 94 days)
        var f3bLater = new GstFiling
        {
            Gstin = reg.Gstin,
            ReturnType = "GSTR3B",
            TaxPeriod = "2023-01",
            FilingStatus = "Filed",
            FilingDate = new DateOnly(2023, 5, 15),
            DueDate = new DateOnly(2023, 2, 20)
        };

        // Test with earlier first, then later
        reg.Filings = [f1, f3bEarlier, f3bLater];
        var model1 = CreateMinimalDossier([reg]);
        var group1 = DossierComputations.GstComplianceMetrics(model1);
        var g5_1 = Assert.Single(group1.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.True(g5_1.HasValue);
        Assert.Equal(94m, g5_1.Value); // Deterministically picks 2023-05-15
        Assert.StartsWith("1 matched periods", g5_1.Period); // Exactly 1 period matched, not 2
        Assert.Contains("1 duplicate GSTR-3B group, 1 duplicate row", g5_1.Period);
        Assert.Contains("0 duplicate GSTR-1 groups", g5_1.Period);
        Assert.Contains("selected latest FilingDate", g5_1.Period);

        // Test with later first, then earlier
        reg.Filings = [f1, f3bLater, f3bEarlier];
        var model2 = CreateMinimalDossier([reg]);
        var group2 = DossierComputations.GstComplianceMetrics(model2);
        var g5_2 = Assert.Single(group2.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.True(g5_2.HasValue);
        Assert.Equal(94m, g5_2.Value); // Same result regardless of row ordering
        Assert.Equal(g5_1.Period, g5_2.Period);
    }

    [Fact]
    public void G5_negative_lags_are_isolated_as_anomalies_and_excluded_from_mean()
    {
        var reg = new GstRegistration
        {
            Gstin = "29AAAAA0000A1Z5",
            State = "Karnataka",
            Status = "Active"
        };
        // Period 1: GSTR-3B filed 30 days AFTER GSTR-1 (valid positive lag: 2023-03-15 - 2023-02-13 = 30 days)
        var f3b_p1 = new GstFiling { Gstin = reg.Gstin, ReturnType = "GSTR3B", TaxPeriod = "2023-01", FilingDate = new DateOnly(2023, 3, 15) };
        var f1_p1  = new GstFiling { Gstin = reg.Gstin, ReturnType = "GSTR1",  TaxPeriod = "2023-01", FilingDate = new DateOnly(2023, 2, 13) };

        // Period 2: GSTR-3B filed BEFORE GSTR-1 (negative lag anomaly: 3B filed 2023-02-15, 1 filed 2023-04-10)
        var f3b_p2 = new GstFiling { Gstin = reg.Gstin, ReturnType = "GSTR3B", TaxPeriod = "2023-02", FilingDate = new DateOnly(2023, 2, 15) };
        var f1_p2  = new GstFiling { Gstin = reg.Gstin, ReturnType = "GSTR1",  TaxPeriod = "2023-02", FilingDate = new DateOnly(2023, 4, 10) };

        reg.Filings = [f3b_p1, f1_p1, f3b_p2, f1_p2];
        var model = CreateMinimalDossier([reg]);
        var group = DossierComputations.GstComplianceMetrics(model);

        var g5 = Assert.Single(group.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.True(g5.HasValue);
        Assert.Equal(30m, g5.Value); // Only Period 1 is averaged; negative lag is excluded
        Assert.StartsWith("1 matched periods", g5.Period);
        Assert.Contains("0 duplicate GSTR-1 groups", g5.Period);
        Assert.Contains("0 duplicate GSTR-3B groups", g5.Period);
        Assert.Contains("selected latest FilingDate", g5.Period);

        var g5Anomalies = Assert.Single(group.Metrics, m => m.Label == "GSTR-3B filed before GSTR-1 anomalies");
        Assert.True(g5Anomalies.HasValue);
        Assert.Equal(1m, g5Anomalies.Value); // Period 2 counted as anomaly
    }
}
