using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ShareholdingMetricsTests
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
        CompanyStructure? structure = null,
        List<Shareholding>? shareholders = null,
        List<ShareholdingPatternRow>? pattern = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], shareholders ?? [], [], [], [], [], structure, null, pattern ?? []),
            Financials: new DossierFinancials([], [], [], [], [], []),
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
    public void No_structure_or_shareholders_makes_every_metric_insufficient()
    {
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier());

        Assert.Equal("Shareholding", group.Title);
        Assert.True(group.HasAny);
        Assert.All(group.Metrics, m => Assert.False(m.HasValue));
        // Exactly 5 insufficiency placeholders: C1, C2, C3, C4, C5 (C6 only ever appears when C5 has data).
        Assert.Equal(5, group.Metrics.Count);
    }

    [Fact]
    public void C1_promoter_public_split_is_verbatim_from_structure()
    {
        var structure = new CompanyStructure { PromoterHoldingPercent = 60m, PublicHoldingPercent = 40m };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(structure));

        var c1 = M(group, "Promoter / public holding split");
        Assert.Equal(60m, c1.Value);
        Assert.Contains("Public 40%", c1.Period);
    }

    [Fact]
    public void C1_is_insufficient_when_promoter_percent_is_missing()
    {
        var structure = new CompanyStructure { PublicHoldingPercent = 40m };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(structure));

        Assert.False(M(group, "Promoter / public holding split").HasValue);
    }

    [Fact]
    public void C2_flags_a_single_digit_shareholder_base()
    {
        var structure = new CompanyStructure { TotalShareholders = 5, PromoterShareholders = 3 };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(structure));

        var c2 = M(group, "Shareholder count / concentration");
        Assert.Equal(5m, c2.Value);
        Assert.Contains("single-digit shareholder base", c2.Period);
    }

    [Fact]
    public void C2_does_not_flag_a_normal_sized_shareholder_base()
    {
        var structure = new CompanyStructure { TotalShareholders = 37, PromoterShareholders = 5 };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(structure));

        Assert.DoesNotContain("single-digit", M(group, "Shareholder count / concentration").Period);
    }

    [Fact]
    public void C3_excludes_director_only_rows_below_the_5_percent_disclosure_threshold()
    {
        var shareholders = new List<Shareholding>
        {
            new() { FinancialYear = 2017, HoldingPercentage = 40m, SourceType = ShareholdingSourceType.MajorShareholding, ShareholderType = "INDIVIDUALS" },
            // A director holding a small stake, never disclosed on the >5% sheet — must not count.
            new() { FinancialYear = 2017, HoldingPercentage = 1m, SourceType = ShareholdingSourceType.DirectorShareholding, ShareholderType = "Director" },
        };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(shareholders: shareholders));

        var c3 = M(group, "Top-5 >5%-shareholder concentration");
        Assert.Equal(40m, c3.Value); // the DirectorShareholding-only row is excluded
    }

    [Fact]
    public void C3_takes_only_the_top_5_of_a_larger_disclosed_set_in_the_latest_year()
    {
        var shareholders = Enumerable.Range(1, 7)
            .Select(i => new Shareholding
            {
                FinancialYear = 2017, HoldingPercentage = i, // 1..7
                SourceType = ShareholdingSourceType.MajorShareholding, ShareholderType = "INDIVIDUALS"
            })
            // an older year must not be pulled into the "latest year" pool
            .Append(new Shareholding { FinancialYear = 2016, HoldingPercentage = 99m, SourceType = ShareholdingSourceType.MajorShareholding })
            .ToList();
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(shareholders: shareholders));

        var c3 = M(group, "Top-5 >5%-shareholder concentration");
        Assert.Equal(25m, c3.Value); // top 5 of {1..7} = 7+6+5+4+3 = 25
        Assert.Contains("top 5 of 7", c3.Period);
        Assert.Contains("FY2017", c3.Period);
    }

    [Fact]
    public void C4_groups_by_shareholder_type_in_the_latest_year_only()
    {
        var shareholders = new List<Shareholding>
        {
            new() { FinancialYear = 2017, HoldingPercentage = 30m, SourceType = ShareholdingSourceType.MajorShareholding, ShareholderType = "INDIVIDUALS" },
            new() { FinancialYear = 2017, HoldingPercentage = 20m, SourceType = ShareholdingSourceType.MajorShareholding, ShareholderType = "BODIES CORPORATE" },
            new() { FinancialYear = 2017, HoldingPercentage = 10m, SourceType = ShareholdingSourceType.MajorShareholding, ShareholderType = "INDIVIDUALS" },
        };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(shareholders: shareholders));

        var individuals = M(group, "Corporate vs individual >5%-shareholder split (INDIVIDUALS)");
        Assert.Equal(40m, individuals.Value); // 30 + 10
        var corporate = M(group, "Corporate vs individual >5%-shareholder split (BODIES CORPORATE)");
        Assert.Equal(20m, corporate.Value);
    }

    [Fact]
    public void C4_buckets_a_blank_shareholder_type_as_unspecified()
    {
        var shareholders = new List<Shareholding>
        {
            new() { FinancialYear = 2017, HoldingPercentage = 15m, SourceType = ShareholdingSourceType.MajorShareholding, ShareholderType = null },
        };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(shareholders: shareholders));

        var unspecified = M(group, "Corporate vs individual >5%-shareholder split (Unspecified)");
        Assert.Equal(15m, unspecified.Value);
    }

    [Fact]
    public void C5_shows_a_single_point_when_only_one_AsOnDate_exists()
    {
        var pattern = new List<ShareholdingPatternRow>
        {
            new() { HolderClass = ShareholderClass.Promoter, AsOnDate = new DateOnly(2017, 3, 31), Category = "1. Individual / Hindu Undivided Family", EquityPercent = 11.44m },
            new() { HolderClass = ShareholderClass.Public, AsOnDate = new DateOnly(2017, 3, 31), Category = "4. Bank", EquityPercent = 52.88m },
        };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(pattern: pattern));

        var c5 = M(group, "Multi-year promoter-holding trend (FY2017)");
        Assert.Equal(11.44m, c5.Value); // promoter-only, public rows excluded from C5
        Assert.Contains("single point", c5.Period);
    }

    [Fact]
    public void C5_emits_one_point_per_distinct_AsOnDate_when_multiple_years_exist()
    {
        var pattern = new List<ShareholdingPatternRow>
        {
            new() { HolderClass = ShareholderClass.Promoter, AsOnDate = new DateOnly(2016, 3, 31), Category = "1. Individual / Hindu Undivided Family", EquityPercent = 20m },
            new() { HolderClass = ShareholderClass.Promoter, AsOnDate = new DateOnly(2017, 3, 31), Category = "1. Individual / Hindu Undivided Family", EquityPercent = 11.44m },
        };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(pattern: pattern));

        Assert.Equal(20m, M(group, "Multi-year promoter-holding trend (FY2016)").Value);
        Assert.Equal(11.44m, M(group, "Multi-year promoter-holding trend (FY2017)").Value);
        Assert.DoesNotContain("single point", M(group, "Multi-year promoter-holding trend (FY2017)").Period);
    }

    [Fact]
    public void C6_rolls_sub_rows_up_under_their_parent_category_without_double_counting()
    {
        var pattern = new List<ShareholdingPatternRow>
        {
            new() { HolderClass = ShareholderClass.Promoter, AsOnDate = new DateOnly(2017, 3, 31),
                Category = "(i) Indian", CategoryGroup = "1. Individual / Hindu Undivided Family", EquityPercent = 8m },
            new() { HolderClass = ShareholderClass.Promoter, AsOnDate = new DateOnly(2017, 3, 31),
                Category = "(ii) Non-resident Indian (others)", CategoryGroup = "1. Individual / Hindu Undivided Family", EquityPercent = 3.44m },
            new() { HolderClass = ShareholderClass.Public, AsOnDate = new DateOnly(2017, 3, 31),
                Category = "4. Bank", EquityPercent = 52.88m },
        };
        var group = DossierComputations.ShareholdingMetrics(CreateMinimalDossier(pattern: pattern));

        var rollup = M(group, "SEBI-category grid rollup (Promoter: 1. Individual / Hindu Undivided Family)");
        Assert.Equal(11.44m, rollup.Value); // 8 + 3.44, the two sub-rows summed under their shared parent
        var bank = M(group, "SEBI-category grid rollup (Public: 4. Bank)");
        Assert.Equal(52.88m, bank.Value);
    }

    [SkippableFact]
    public void Coastal_fixture_exact_shareholding_metrics()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);
        var directorSheet = sheets.Single(s => s.Name == "Director Shareholding");
        var majorSheet = sheets.Single(s => s.Name == "Shareholding More Than 5%");
        var shareholders = ShareholdingParser.Parse(directorSheet, majorSheet, 1, 1, 1, 1).Items.ToList();

        var structureSheet = sheets.Single(s => s.Name == "Structure");
        var structResult = StructureParser.Parse(structureSheet, 1, 1, 1, out var pattern);
        var structure = structResult.Items.Single();

        var group = DossierComputations.ShareholdingMetrics(
            CreateMinimalDossier(structure, shareholders, pattern));
        Assert.Equal("Shareholding", group.Title);

        // C1: verbatim from the SHARE HOLDING SUMMARY block.
        var c1 = M(group, "Promoter / public holding split");
        Assert.Equal(11.44m, c1.Value);
        Assert.Contains("Public 88.56%", c1.Period);

        // C2: 37 total, 5 promoter — well above the single-digit flag threshold.
        var c2 = M(group, "Shareholder count / concentration");
        Assert.Equal(37m, c2.Value);
        Assert.Contains("5 promoter", c2.Period);
        Assert.DoesNotContain("single-digit", c2.Period);

        // C3: only "SABBINENI SURENDRA" is disclosed on the >5% sheet for FY2017 (the Director
        // Shareholding-only "SURENDRA BABU SABBINENI" row for the same year is correctly excluded).
        var c3 = M(group, "Top-5 >5%-shareholder concentration");
        Assert.Equal(10.23m, c3.Value);
        Assert.Contains("FY2017", c3.Period);
        Assert.Contains("top 1 of 1", c3.Period);

        // C4: the sole FY2017 >5% holder is typed INDIVIDUALS.
        var c4 = M(group, "Corporate vs individual >5%-shareholder split (INDIVIDUALS)");
        Assert.Equal(10.23m, c4.Value);

        // C5: COASTAL's Structure grid reports a single AsOnDate (31 Mar 2017).
        var c5 = M(group, "Multi-year promoter-holding trend (FY2017)");
        Assert.Equal(11.44m, c5.Value);
        Assert.Contains("single point", c5.Period);

        // C6: the two populated public-side categories reconcile with the real workbook values used
        // elsewhere in this suite (SourceReconciliationTests.Structure_sheet_captures_...).
        var bank = M(group, "SEBI-category grid rollup (Public: 4. Bank)");
        Assert.Equal(52.88m, bank.Value);
        var bodyCorporate = M(group, "SEBI-category grid rollup (Public: 9. Body corporate (not mentioned above))");
        Assert.Equal(35.56m, bodyCorporate.Value);
        var promoterIndividual = M(group, "SEBI-category grid rollup (Promoter: 1. Individual / Hindu Undivided Family)");
        Assert.Equal(11.44m, promoterIndividual.Value);
        // Every other of the 20 (class x SEBI category) buckets is genuinely zero, not omitted.
        Assert.Equal(20, group.Metrics.Count(m => m.Label.StartsWith("SEBI-category grid rollup")));
    }
}
