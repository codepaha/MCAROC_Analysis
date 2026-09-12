using System.Text;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests.Dossier;

public class ChargeRegisterMetricsTests
{
    public ChargeRegisterMetricsTests()
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

    private static (string Roc, string Charge)? Fixtures()
    {
        var dir = Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis.Tests", "Fixtures", "workbooks");
        var roc = Path.Combine(dir, "roc.xls");
        var charge = Path.Combine(dir, "charge.xls");
        if (File.Exists(roc) && File.Exists(charge)) return (roc, charge);

        var env = Environment.GetEnvironmentVariable("MCAROC_RECON_FIXTURES") ?? @"E:\Downloads";
        if (!string.IsNullOrWhiteSpace(env))
        {
            var r = Path.Combine(env, "U45203OR1995PLC003982.xls");
            var c = Path.Combine(env, "U45203OR1995PLC003982-charge.xls");
            if (File.Exists(r) && File.Exists(c)) return (r, c);
        }
        return null;
    }

    [SkippableFact]
    public void Charge_register_metrics_reproduce_exact_values_on_coastal_fixture()
    {
        Skip.If(Fixtures() is null, "COASTAL fixtures not found — see Fixtures/README.md");
        var fx = Fixtures()!.Value;

        var reader = new ExcelSheetReader();
        var rocWb = reader.ReadWorkbook(fx.Roc);
        var chargeWb = reader.ReadWorkbook(fx.Charge);

        var chargesResult = ChargesParser.Parse(rocWb, chargeWb, true, 1, 1, 1, 2);
        var charges = chargesResult.Items;
        Assert.Equal(213, charges.Count);
        Assert.Equal(337, charges.Sum(c => c.Events.Count));

        var open = DossierComputations.OpenChargesByAmount(charges);
        var satisfied = DossierComputations.SatisfiedChargesBySatisfaction(charges);
        Assert.Equal(163, open.Count);
        Assert.Equal(50, satisfied.Count);

        var profileSheet = SheetAliases.Find(rocWb, SheetAliases.CompanyProfile);
        var profile = profileSheet is not null ? CompanyProfileParser.Parse(profileSheet, 1, 1, 1).Items.FirstOrDefault() : null;
        Assert.NotNull(profile);
        Assert.Equal(10.01m, profile!.PaidUpCapital);

        var finSheet = SheetAliases.Find(rocWb, SheetAliases.StandaloneFinancialData);
        var finResult = finSheet is not null ? StandaloneFinancialDataParser.Parse(finSheet, 1, 1, 1, out _, FinancialBasis.Standalone) : new ParseResult<FinancialYearData>();
        var standalone = finResult.Items;
        var latestFy = standalone.OrderBy(f => f.FinancialYear).LastOrDefault();
        Assert.NotNull(latestFy);
        Assert.Equal(2017, latestFy!.FinancialYear);
        Assert.Equal(2283.25m, latestFy.LongTermBorrowings);
        Assert.Equal(1834.27m, latestFy.ShortTermBorrowings);

        var asOf = new DateTime(2017, 3, 31);
        var dossierModel = new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("COASTAL PROJECTS LIMITED", "U45203OR1995PLC003982", "AAACC4128F",
                new DateOnly(1995, 5, 2), "Active", "TestClient", asOf, asOf),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, profile.PaidUpCapital, [], []),
            Financials: new DossierFinancials(standalone, [], [], [], [], []),
            Charges: new DossierCharges(charges, open, satisfied, DossierComputations.LenderConcentration(charges), 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(null, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);

        var group = DossierComputations.ChargeRegisterMetrics(dossierModel);
        Assert.Equal("Charge register", group.Title);
        Assert.True(group.HasAny);

        var byLabel = group.Metrics.ToDictionary(m => m.Label);

        // B1: Total open charge amount
        var b1 = byLabel["Total open charge amount"];
        Assert.True(b1.HasValue);
        Assert.Equal(12591.87m, b1.Value);
        Assert.Equal(MetricUnit.Crore, b1.Unit);
        Assert.Equal("₹12,591.87 Cr", b1.DisplayValue());

        // B2: Total satisfied charge amount
        var b2 = byLabel["Total satisfied charge amount"];
        Assert.True(b2.HasValue);
        Assert.Equal(781.26m, b2.Value);
        Assert.Equal(MetricUnit.Crore, b2.Unit);
        Assert.Equal("₹781.26 Cr", b2.DisplayValue());

        // B3: Charge satisfaction rate
        var b3 = byLabel["Charge satisfaction rate"];
        Assert.True(b3.HasValue);
        Assert.Equal(23.5m, b3.Value);
        Assert.Equal(MetricUnit.Percent, b3.Unit);
        Assert.Equal("23.50%", b3.DisplayValue());

        // B4: Top-3 lender concentration
        var b4 = byLabel["Top-3 lender concentration"];
        Assert.True(b4.HasValue);
        Assert.Equal(55.2m, b4.Value);
        Assert.Equal(MetricUnit.Percent, b4.Unit);
        Assert.Equal("55.20%", b4.DisplayValue());

        // B4b: Lender concentration (HHI)
        var b4b = byLabel["Lender concentration (HHI)"];
        Assert.True(b4b.HasValue);
        Assert.Equal(0.2068m, b4b.Value);
        Assert.Equal(MetricUnit.Ratio, b4b.Unit);
        Assert.Equal("0.21", b4b.DisplayValue());

        // B5: Asset-type breakdown (unclassified bucket)
        var b5 = byLabel["Unclassified open charge amount"];
        Assert.True(b5.HasValue);
        Assert.Equal(730.53m, b5.Value);
        Assert.Equal(MetricUnit.Crore, b5.Unit);

        // B6: Oldest open charge age
        var b6 = byLabel["Oldest open charge age"];
        Assert.True(b6.HasValue);
        var expectedAge = DateOnly.FromDateTime(asOf).DayNumber - new DateOnly(2003, 2, 13).DayNumber;
        Assert.Equal((decimal)expectedAge, b6.Value);
        Assert.Equal(MetricUnit.Days, b6.Unit);

        // B8: Joint / consortium charge count
        var b8 = byLabel["Joint / consortium charge count"];
        Assert.True(b8.HasValue);
        Assert.Equal(2m, b8.Value);
        Assert.Equal(MetricUnit.Count, b8.Unit);

        // B9: Charge-to-paid-up-capital ratio
        var b9 = byLabel["Charge-to-paid-up-capital ratio"];
        Assert.True(b9.HasValue);
        Assert.Equal(1257.93m, b9.Value);
        Assert.Equal(MetricUnit.Times, b9.Unit);
        Assert.Equal("1,257.93x", b9.DisplayValue());

        // B10: Open charges vs balance-sheet borrowings
        var b10 = byLabel["Open charges vs balance-sheet borrowings"];
        Assert.True(b10.HasValue);
        Assert.Equal(3.06m, b10.Value);
        Assert.Equal(MetricUnit.Times, b10.Unit);
        Assert.Equal("3.06x", b10.DisplayValue());

        // B11: Charge filing lag
        var b11Median = byLabel["Median charge filing lag"];
        Assert.True(b11Median.HasValue);
        Assert.Equal(35m, b11Median.Value);
        Assert.Equal(MetricUnit.Days, b11Median.Unit);

        var b11_0_7 = byLabel["Charge filing lag 0-7 days"];
        Assert.True(b11_0_7.HasValue);
        Assert.Equal(MetricUnit.Count, b11_0_7.Unit);

        var b11_8_30 = byLabel["Charge filing lag 8-30 days"];
        Assert.True(b11_8_30.HasValue);
        Assert.Equal(MetricUnit.Count, b11_8_30.Unit);

        var b11_31_90 = byLabel["Charge filing lag 31-90 days"];
        Assert.True(b11_31_90.HasValue);
        Assert.Equal(MetricUnit.Count, b11_31_90.Unit);

        var b11_gt90 = byLabel["Charge filing lag >90 days"];
        Assert.True(b11_gt90.HasValue);
        Assert.Equal(MetricUnit.Count, b11_gt90.Unit);

        var b11Anom = byLabel["Negative filing-lag anomalies"];
        Assert.True(b11Anom.HasValue);
        Assert.Equal(0m, b11Anom.Value);
        Assert.Equal(MetricUnit.Count, b11Anom.Unit);
    }

    [Fact]
    public void Degenerate_cases_produce_fail_closed_insufficient_metrics()
    {
        var emptyModel = new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("Zero Charges Co", null, null, null, null, "Test", DateTime.UtcNow, null),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], []),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(null, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);

        var group = DossierComputations.ChargeRegisterMetrics(emptyModel);
        Assert.Equal("Charge register", group.Title);
        Assert.All(group.Metrics, m =>
        {
            if (m.Label is "Joint / consortium charge count" or "Negative filing-lag anomalies")
            {
                Assert.True(m.HasValue);
                Assert.Equal(0m, m.Value);
            }
            else
            {
                Assert.False(m.HasValue);
                Assert.NotNull(m.InsufficiencyReason);
            }
        });
    }

    private static DossierModel CreateMinimalDossierWithCharges(List<RocCharge> all, List<RocCharge>? open = null, List<RocCharge>? satisfied = null, DateTime? asOf = null, decimal? paidUpCapital = null)
    {
        var dt = asOf ?? new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var openList = open ?? all.Where(DossierComputations.IsOpenCharge).ToList();
        var satList = satisfied ?? all.Where(c => !DossierComputations.IsOpenCharge(c)).ToList();
        var concentration = DossierComputations.LenderConcentration(all);
        return new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("Test Co", "U12345AB2020PTC123456", "ABCDE1234F", new DateOnly(2020, 1, 1), "Active", "TestClient", dt, dt),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, paidUpCapital, [], []),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges(all, openList, satList, concentration, 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(null, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    [Fact]
    public void B2_missing_amount_is_insufficient_not_zero()
    {
        var c1 = new RocCharge { ChargeId = 1, ChargeStatus = "Satisfied", SatisfactionDate = new DateOnly(2022, 1, 1), CurrentAmount = 100m };
        var c2 = new RocCharge { ChargeId = 2, ChargeStatus = "Satisfied", SatisfactionDate = new DateOnly(2022, 2, 1), CurrentAmount = null };
        var model = CreateMinimalDossierWithCharges([c1, c2]);

        var group = DossierComputations.ChargeRegisterMetrics(model);
        var b2 = Assert.Single(group.Metrics, m => m.Label == "Total satisfied charge amount");

        Assert.False(b2.HasValue);
        Assert.NotNull(b2.InsufficiencyReason);
        Assert.Contains("1 of 2 satisfied charges missing CurrentAmount", b2.InsufficiencyReason);
        Assert.Equal(b2.InsufficiencyReason, b2.DisplayValue());
    }

    [Fact]
    public void B5_unclassified_missing_amount_is_insufficient()
    {
        var c1 = new RocCharge { ChargeId = 1, ChargeStatus = "Open", CurrentAmount = null };
        var model = CreateMinimalDossierWithCharges([c1]);

        var group = DossierComputations.ChargeRegisterMetrics(model);
        var b5 = Assert.Single(group.Metrics, m => m.Label == "Unclassified open charge amount");

        Assert.False(b5.HasValue);
        Assert.NotNull(b5.InsufficiencyReason);
        Assert.Contains("missing CurrentAmount", b5.InsufficiencyReason);
    }

    [Fact]
    public void B7_amount_is_insufficient_when_any_creation_event_missing_charge_amount()
    {
        var asOf = new DateTime(2023, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        var asOfDate = DateOnly.FromDateTime(asOf);

        var c1 = new RocCharge { ChargeId = 1, ChargeStatus = "Open", CurrentAmount = 50m };
        c1.Events.Add(new RocChargeEvent
        {
            EventType = ChargeEventType.Creation,
            EventDate = asOfDate.AddMonths(-3),
            ChargeAmount = 50m
        });

        var c2 = new RocCharge { ChargeId = 2, ChargeStatus = "Open", CurrentAmount = null };
        c2.Events.Add(new RocChargeEvent
        {
            EventType = ChargeEventType.Creation,
            EventDate = asOfDate.AddMonths(-6),
            ChargeAmount = null
        });

        var model = CreateMinimalDossierWithCharges([c1, c2], asOf: asOf);
        var group = DossierComputations.ChargeRegisterMetrics(model);

        var b7Count12 = Assert.Single(group.Metrics, m => m.Label == "Charges created in last 12 months");
        Assert.True(b7Count12.HasValue);
        Assert.Equal(2m, b7Count12.Value);

        var b7Amt12 = Assert.Single(group.Metrics, m => m.Label == "Amount created in last 12 months");
        Assert.False(b7Amt12.HasValue);
        Assert.NotNull(b7Amt12.InsufficiencyReason);
        Assert.Contains("missing CurrentAmount", b7Amt12.InsufficiencyReason);
    }

    [Fact]
    public void B7_zero_creation_events_includes_coverage_note()
    {
        var asOf = new DateTime(2023, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        var oldDate = new DateOnly(2015, 1, 1);

        // Charge created 8 years ago — outside trailing 12 and 24 months
        var c1 = new RocCharge { ChargeId = 1, ChargeStatus = "Open", CurrentAmount = 50m, CreationDate = oldDate };
        var model = CreateMinimalDossierWithCharges([c1], asOf: asOf);
        var group = DossierComputations.ChargeRegisterMetrics(model);

        var b7Count12 = Assert.Single(group.Metrics, m => m.Label == "Charges created in last 12 months");
        Assert.True(b7Count12.HasValue);
        Assert.Equal(0m, b7Count12.Value);
        Assert.Contains("(0 dated creation events)", b7Count12.Period);

        var b7Amt12 = Assert.Single(group.Metrics, m => m.Label == "Amount created in last 12 months");
        Assert.True(b7Amt12.HasValue);
        Assert.Equal(0m, b7Amt12.Value);
        Assert.Contains("(0 dated creation events)", b7Amt12.Period);

        var b7Count24 = Assert.Single(group.Metrics, m => m.Label == "Charges created in last 24 months");
        Assert.True(b7Count24.HasValue);
        Assert.Equal(0m, b7Count24.Value);
        Assert.Contains("(0 dated creation events)", b7Count24.Period);

        var b7Amt24 = Assert.Single(group.Metrics, m => m.Label == "Amount created in last 24 months");
        Assert.True(b7Amt24.HasValue);
        Assert.Equal(0m, b7Amt24.Value);
        Assert.Contains("(0 dated creation events)", b7Amt24.Period);
    }

    [Fact]
    public void B11_median_is_sorted_and_buckets_are_present()
    {
        var asOf = new DateTime(2023, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        var baseDate = new DateOnly(2023, 1, 1);

        var lags = new[] { 100, 5, 20, 50 };
        var charges = new List<RocCharge>();

        for (int i = 0; i < lags.Length; i++)
        {
            var c = new RocCharge { ChargeId = i + 1, ChargeStatus = "Open", CurrentAmount = 10m };
            c.Events.Add(new RocChargeEvent
            {
                EventType = ChargeEventType.Creation,
                EventDate = baseDate,
                FilingDate = baseDate.AddDays(lags[i]),
                ChargeAmount = 10m
            });
            charges.Add(c);
        }

        var model = CreateMinimalDossierWithCharges(charges, asOf: asOf);
        var group = DossierComputations.ChargeRegisterMetrics(model);

        // Sorted lags: [5, 20, 50, 100]; even count 4 -> median is average of middle two (20 + 50) / 2 = 35
        var median = Assert.Single(group.Metrics, m => m.Label == "Median charge filing lag");
        Assert.True(median.HasValue);
        Assert.Equal(35m, median.Value);

        var b0_7 = Assert.Single(group.Metrics, m => m.Label == "Charge filing lag 0-7 days");
        Assert.True(b0_7.HasValue);
        Assert.Equal(1m, b0_7.Value);

        var b8_30 = Assert.Single(group.Metrics, m => m.Label == "Charge filing lag 8-30 days");
        Assert.True(b8_30.HasValue);
        Assert.Equal(1m, b8_30.Value);

        var b31_90 = Assert.Single(group.Metrics, m => m.Label == "Charge filing lag 31-90 days");
        Assert.True(b31_90.HasValue);
        Assert.Equal(1m, b31_90.Value);

        var bGt90 = Assert.Single(group.Metrics, m => m.Label == "Charge filing lag >90 days");
        Assert.True(bGt90.HasValue);
        Assert.Equal(1m, bGt90.Value);
    }
}
