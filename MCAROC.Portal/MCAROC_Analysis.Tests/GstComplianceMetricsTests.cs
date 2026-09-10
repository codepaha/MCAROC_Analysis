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
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], gstRegs, [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
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

        // G3: GST filing on-time rate = 210 on-time / 294 assessed = 71.4%
        var g3 = Assert.Single(group.Metrics, m => m.Label == "GST filing on-time rate");
        Assert.True(g3.HasValue);
        Assert.Equal(71.4m, g3.Value);
        Assert.Equal(MetricUnit.Percent, g3.Unit);
        Assert.Contains("294 assessed", g3.Period);
        Assert.Contains("285 indeterminate", g3.Period);

        // G4: Late filing count = 84
        var g4 = Assert.Single(group.Metrics, m => m.Label == "Late filing count");
        Assert.True(g4.HasValue);
        Assert.Equal(84m, g4.Value);
        Assert.Equal(MetricUnit.Count, g4.Unit);
        Assert.Equal("84 late of 294 assessed", g4.Period);

        // G5: GSTR-1 vs GSTR-3B filing lag = 211.1 days over 274 matched periods
        var g5 = Assert.Single(group.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.True(g5.HasValue);
        Assert.Equal(211.1m, g5.Value);
        Assert.Equal(MetricUnit.Days, g5.Unit);
        Assert.Equal("274 matched periods", g5.Period);

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

        var g3 = Assert.Single(group.Metrics, m => m.Label == "GST filing on-time rate");
        Assert.False(g3.HasValue);
        Assert.Contains("No GST filings with deterministic filing and due dates", g3.InsufficiencyReason!);

        var g4 = Assert.Single(group.Metrics, m => m.Label == "Late filing count");
        Assert.False(g4.HasValue);

        var g5 = Assert.Single(group.Metrics, m => m.Label == "GSTR-1 vs GSTR-3B filing lag");
        Assert.False(g5.HasValue);

        var g6 = Assert.Single(group.Metrics, m => m.Label == "GST registration flags present");
        Assert.True(g6.HasValue);
        Assert.Equal(0m, g6.Value);
    }
}
