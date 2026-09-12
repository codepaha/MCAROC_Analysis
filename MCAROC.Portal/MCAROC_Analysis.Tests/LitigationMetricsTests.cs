using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationMetricsTests
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
        List<Litigation> litigations,
        Dictionary<long, LitigationRole>? roles = null,
        DateTime? asOf = null)
    {
        for (var i = 0; i < litigations.Count; i++)
        {
            if (litigations[i].LitigationId == 0)
                litigations[i].LitigationId = i + 1;
        }

        var reportDate = asOf ?? new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        var roleDict = roles ?? litigations.ToDictionary(l => l.LitigationId, _ => LitigationRole.NotDetermined);

        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, []),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], [], []),
            Litigation: new DossierLitigation(litigations, [], roleDict),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    [Fact]
    public void NormalizeCourtType_matches_precedence_and_patterns()
    {
        // 1. NCLT
        Assert.Equal("NCLT", DossierComputations.NormalizeCourtType("NCLT Bengaluru"));
        Assert.Equal("NCLT", DossierComputations.NormalizeCourtType("NATIONAL COMPANY LAW TRIBUNAL"));
        Assert.Equal("NCLT", DossierComputations.NormalizeCourtType("NATIONAL COMPANY LAW APPELLATE TRIBUNAL"));

        // 2. DRT
        Assert.Equal("DRT", DossierComputations.NormalizeCourtType("DEBTS RECOVERY TRIBUNAL"));
        Assert.Equal("DRT", DossierComputations.NormalizeCourtType("DEBT RECOVERY APPELLATE TRIBUNAL"));
        Assert.Equal("DRT", DossierComputations.NormalizeCourtType("DRT-1 Mumbai"));
        Assert.Equal("DRT", DossierComputations.NormalizeCourtType("DRAT Delhi"));

        // 3. High Court
        Assert.Equal("High Court", DossierComputations.NormalizeCourtType("HIGH COURT OF BOMBAY"));
        Assert.Equal("High Court", DossierComputations.NormalizeCourtType("High Court of Calcutta - Appellate Side"));

        // 4. Consumer Court (evaluated before District Court)
        Assert.Equal("Consumer Court", DossierComputations.NormalizeCourtType("DISTRICT CONSUMER DISPUTES REDRESSAL FORUM"));
        Assert.Equal("Consumer Court", DossierComputations.NormalizeCourtType("State Consumer Commission"));

        // 5. District Court
        Assert.Equal("District Court", DossierComputations.NormalizeCourtType("DISTRICT AND SESSIONS COURT, FARIDABAD"));
        Assert.Equal("District Court", DossierComputations.NormalizeCourtType("CITY CIVIL COURT, CHENNAI"));
        Assert.Equal("District Court", DossierComputations.NormalizeCourtType("PRL. CITY CIVIL AND SESSIONS JUDGE"));

        // 6. Other fallback
        Assert.Equal("Other", DossierComputations.NormalizeCourtType("LABOUR COURT, NAGPUR"));
        Assert.Equal("Other", DossierComputations.NormalizeCourtType("CHIEF JUDGE, COURT OF SMALL CAUSES"));
        Assert.Equal("Other", DossierComputations.NormalizeCourtType("METROPOLITAN MAGISTRATE, MUMBAI"));
        Assert.Equal("Other", DossierComputations.NormalizeCourtType(null));
        Assert.Equal("Other", DossierComputations.NormalizeCourtType("   "));
    }

    [Fact]
    public void D5_insolvency_in_non_nclt_court_triggers_or_condition()
    {
        // Non-NCLT court with Insolvency category must be counted by D5 via the OR branch
        var cases = new List<Litigation>
        {
            new()
            {
                LitigationId = 1,
                MatchStatus = LitigationMatchStatus.Confirmed,
                CaseCategory = "Insolvency",
                Court = "City Civil Court, Chennai",
                CaseStatus = "Pending"
            },
            new()
            {
                LitigationId = 2,
                MatchStatus = LitigationMatchStatus.Confirmed,
                CaseCategory = "Civil Cases",
                Court = "City Civil Court, Chennai",
                CaseStatus = "Pending"
            }
        };

        var model = CreateMinimalDossier(cases);
        var group = DossierComputations.LitigationMetrics(model);

        var d5 = Assert.Single(group.Metrics, m => m.Label == "NCLT / insolvency case count");
        Assert.True(d5.HasValue);
        Assert.Equal(1m, d5.Value);
        Assert.Equal(MetricUnit.Count, d5.Unit);
        Assert.Contains("1 of 2 confirmed/probable cases", d5.Period);
    }

    [Fact]
    public void D7_indeterminate_case_status_excluded_from_denominator()
    {
        var cases = new List<Litigation>
        {
            new() { LitigationId = 1, MatchStatus = LitigationMatchStatus.Confirmed, CaseStatus = "Pending" },
            new() { LitigationId = 2, MatchStatus = LitigationMatchStatus.Confirmed, CaseStatus = "Disposed" },
            new() { LitigationId = 3, MatchStatus = LitigationMatchStatus.Confirmed, CaseStatus = null },
            new() { LitigationId = 4, MatchStatus = LitigationMatchStatus.Confirmed, CaseStatus = "   " }
        };

        var model = CreateMinimalDossier(cases);
        var group = DossierComputations.LitigationMetrics(model);

        var d7 = Assert.Single(group.Metrics, m => m.Label == "Pending vs disposed ratio");
        Assert.True(d7.HasValue);
        Assert.Equal(50.0m, d7.Value); // 1 pending of 2 assessed = 50.0%
        Assert.Equal(MetricUnit.Percent, d7.Unit);
        Assert.Contains("1 pending, 1 disposed of 2 assessed (2 indeterminate)", d7.Period);

        // Degenerate: all confirmed cases have blank status (0 assessed)
        var allBlank = new List<Litigation>
        {
            new() { LitigationId = 1, MatchStatus = LitigationMatchStatus.Confirmed, CaseStatus = null },
            new() { LitigationId = 2, MatchStatus = LitigationMatchStatus.Confirmed, CaseStatus = "" }
        };
        var modelBlank = CreateMinimalDossier(allBlank);
        var groupBlank = DossierComputations.LitigationMetrics(modelBlank);
        var d7Blank = Assert.Single(groupBlank.Metrics, m => m.Label == "Pending vs disposed ratio");
        Assert.False(d7Blank.HasValue);
        Assert.Contains("2 confirmed cases indeterminate (missing CaseStatus)", d7Blank.InsufficiencyReason);
    }

    [Fact]
    public void Empty_dataset_matches_exact_degenerate_contracts()
    {
        var model = CreateMinimalDossier([], asOf: new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc));
        var group = DossierComputations.LitigationMetrics(model);

        Assert.Equal("Legal history", group.Title);
        Assert.True(group.HasAny);

        // D1: Ok 0 with note
        var d1 = Assert.Single(group.Metrics, m => m.Label == "Confirmed pending case count");
        Assert.True(d1.HasValue);
        Assert.Equal(0m, d1.Value);
        Assert.Equal(MetricUnit.Count, d1.Unit);
        Assert.Equal("as at 28 Oct 2022 (0 confirmed cases on record)", d1.Period);
        Assert.Equal(new[] { "Litigation.MatchStatus", "Litigation.CaseStatus" }, d1.Inputs);

        // D2: Ok 0 with note
        var d2 = Assert.Single(group.Metrics, m => m.Label == "Cases filed against vs by the company");
        Assert.True(d2.HasValue);
        Assert.Equal(0m, d2.Value);
        Assert.Equal(MetricUnit.Count, d2.Unit);
        Assert.Equal("0 filed against, 0 filed by, 0 role not determined", d2.Period);
        Assert.Equal(new[] { "DossierComputations.LitigationRoles" }, d2.Inputs);

        // D3 & D4: No bucket metrics emitted
        Assert.DoesNotContain(group.Metrics, m => m.Label.StartsWith("Cases by category"));
        Assert.DoesNotContain(group.Metrics, m => m.Label.StartsWith("Cases by court type"));

        // D5: Ok 0 with note
        var d5 = Assert.Single(group.Metrics, m => m.Label == "NCLT / insolvency case count");
        Assert.True(d5.HasValue);
        Assert.Equal(0m, d5.Value);
        Assert.Equal(MetricUnit.Count, d5.Unit);
        Assert.Equal("as at 28 Oct 2022 (0 NCLT/insolvency cases on file)", d5.Period);
        Assert.Equal(new[] { "Litigation.CaseCategory", "Litigation.Court" }, d5.Inputs);

        // D6: Ok 0 with note
        var d6 = Assert.Single(group.Metrics, m => m.Label == "DRT case count");
        Assert.True(d6.HasValue);
        Assert.Equal(0m, d6.Value);
        Assert.Equal(MetricUnit.Count, d6.Unit);
        Assert.Equal("as at 28 Oct 2022 (0 DRT cases on file)", d6.Period);
        Assert.Equal(new[] { "Litigation.Court" }, d6.Inputs);

        // D7: Insufficient
        var d7 = Assert.Single(group.Metrics, m => m.Label == "Pending vs disposed ratio");
        Assert.False(d7.HasValue);
        Assert.Equal(MetricUnit.Percent, d7.Unit);
        Assert.Equal("0 confirmed litigation cases on record", d7.InsufficiencyReason);
        Assert.Equal(new[] { "Litigation.CaseStatus" }, d7.Inputs);

        // D8: Ok 0
        var d8 = Assert.Single(group.Metrics, m => m.Label == "Probable + Uncertain exposure count");
        Assert.True(d8.HasValue);
        Assert.Equal(0m, d8.Value);
        Assert.Equal(MetricUnit.Count, d8.Unit);
        Assert.Equal("0 probable, 0 unverified (name-match only / court not reached)", d8.Period);
        Assert.Equal(new[] { "Litigation.MatchStatus" }, d8.Inputs);
    }

    [Fact]
    public void Uncertain_only_dataset_matches_exact_degenerate_contracts()
    {
        var cases = new List<Litigation>
        {
            new() { LitigationId = 1, MatchStatus = LitigationMatchStatus.Uncertain, Court = "City Civil Court" },
            new() { LitigationId = 2, MatchStatus = LitigationMatchStatus.Uncertain, Court = "High Court" },
            new() { LitigationId = 3, MatchStatus = LitigationMatchStatus.Uncertain, Court = "NCLT" }
        };

        var model = CreateMinimalDossier(cases, asOf: new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc));
        var group = DossierComputations.LitigationMetrics(model);

        // D1: 0 confirmed cases
        var d1 = Assert.Single(group.Metrics, m => m.Label == "Confirmed pending case count");
        Assert.Equal(0m, d1.Value);
        Assert.Equal("as at 28 Oct 2022 (0 confirmed cases on record)", d1.Period);

        // D2: 0 against, 0 by, 3 not determined
        var d2 = Assert.Single(group.Metrics, m => m.Label == "Cases filed against vs by the company");
        Assert.Equal(0m, d2.Value);
        Assert.Equal("0 filed against, 0 filed by, 3 role not determined", d2.Period);

        // D3 & D4: No bucket metrics emitted (Uncertain has no category/court breakdowns)
        Assert.DoesNotContain(group.Metrics, m => m.Label.StartsWith("Cases by category"));
        Assert.DoesNotContain(group.Metrics, m => m.Label.StartsWith("Cases by court type"));

        // D5 & D6: 0 with note
        var d5 = Assert.Single(group.Metrics, m => m.Label == "NCLT / insolvency case count");
        Assert.Equal(0m, d5.Value);
        Assert.Equal("as at 28 Oct 2022 (0 Confirmed/Probable cases on file)", d5.Period);

        var d6 = Assert.Single(group.Metrics, m => m.Label == "DRT case count");
        Assert.Equal(0m, d6.Value);
        Assert.Equal("as at 28 Oct 2022 (0 Confirmed/Probable cases on file)", d6.Period);

        // D7: Insufficient
        var d7 = Assert.Single(group.Metrics, m => m.Label == "Pending vs disposed ratio");
        Assert.False(d7.HasValue);
        Assert.Equal("0 confirmed litigation cases on record", d7.InsufficiencyReason);

        // D8: 3 unverified
        var d8 = Assert.Single(group.Metrics, m => m.Label == "Probable + Uncertain exposure count");
        Assert.Equal(3m, d8.Value);
        Assert.Equal("0 probable, 3 unverified (name-match only / court not reached)", d8.Period);
    }

    [Fact]
    public void D2_role_attribution_deterministic()
    {
        var cases = new List<Litigation>
        {
            new() { LitigationId = 101, MatchStatus = LitigationMatchStatus.Confirmed },
            new() { LitigationId = 102, MatchStatus = LitigationMatchStatus.Confirmed },
            new() { LitigationId = 103, MatchStatus = LitigationMatchStatus.Confirmed },
            new() { LitigationId = 104, MatchStatus = LitigationMatchStatus.Confirmed },
            new() { LitigationId = 105, MatchStatus = LitigationMatchStatus.Confirmed }
        };

        var fixedRoles = new Dictionary<long, LitigationRole>
        {
            [101] = LitigationRole.FiledAgainst,
            [102] = LitigationRole.FiledAgainst,
            [103] = LitigationRole.FiledBy,
            [104] = LitigationRole.NotDetermined,
            [105] = LitigationRole.NotDetermined
        };

        var model = CreateMinimalDossier(cases, fixedRoles);
        var group = DossierComputations.LitigationMetrics(model);

        var d2 = Assert.Single(group.Metrics, m => m.Label == "Cases filed against vs by the company");
        Assert.True(d2.HasValue);
        Assert.Equal(2m, d2.Value);
        Assert.Equal(MetricUnit.Count, d2.Unit);
        Assert.Equal("2 filed against, 1 filed by, 2 role not determined", d2.Period);
        Assert.Equal(new[] { "DossierComputations.LitigationRoles" }, d2.Inputs);
    }

    [Fact]
    public void D3_blank_category_mapped_to_uncategorised_bucket()
    {
        var cases = new List<Litigation>
        {
            new() { MatchStatus = LitigationMatchStatus.Confirmed, CaseCategory = null },
            new() { MatchStatus = LitigationMatchStatus.Confirmed, CaseCategory = "" },
            new() { MatchStatus = LitigationMatchStatus.Probable, CaseCategory = "   " },
            new() { MatchStatus = LitigationMatchStatus.Confirmed, CaseCategory = "Civil Cases" },
            new() { MatchStatus = LitigationMatchStatus.Uncertain, CaseCategory = null } // Uncertain excluded from D3
        };

        var model = CreateMinimalDossier(cases);
        var group = DossierComputations.LitigationMetrics(model);

        var uncategorised = Assert.Single(group.Metrics, m => m.Label == "Cases by category (Uncategorised)");
        Assert.True(uncategorised.HasValue);
        Assert.Equal(3m, uncategorised.Value);
        Assert.Equal("3 of 4 confirmed/probable cases", uncategorised.Period);

        var civil = Assert.Single(group.Metrics, m => m.Label == "Cases by category (Civil Cases)");
        Assert.True(civil.HasValue);
        Assert.Equal(1m, civil.Value);
        Assert.Equal("1 of 4 confirmed/probable cases", civil.Period);
    }

    [SkippableFact]
    public void Coastal_fixture_exact_legal_history_metrics()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);
        var legalSheet = sheets.Single(s => s.Name == "Legal History");
        var parsed = LegalHistoryParser.Parse(legalSheet, 1, 1, 1);
        var items = parsed.Items;

        Assert.Equal(952, items.Count);
        Assert.Equal(592, items.Count(l => l.MatchStatus == LitigationMatchStatus.Confirmed));
        Assert.Equal(68, items.Count(l => l.MatchStatus == LitigationMatchStatus.Probable));
        Assert.Equal(292, items.Count(l => l.MatchStatus == LitigationMatchStatus.Uncertain));

        var model = CreateMinimalDossier(items, asOf: new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc));
        var group = DossierComputations.LitigationMetrics(model);

        Assert.Equal("Legal history", group.Title);
        Assert.True(group.HasAny);

        // D1: Confirmed pending case count = 127
        var d1 = Assert.Single(group.Metrics, m => m.Label == "Confirmed pending case count");
        Assert.True(d1.HasValue);
        Assert.Equal(127m, d1.Value);
        Assert.Equal(MetricUnit.Count, d1.Unit);
        Assert.Equal("as at 28 Oct 2022 (127 pending of 592 confirmed cases)", d1.Period);

        // D2: Role attribution from un-analyzed workbook parser has 0 roles attributed -> 952 not determined
        var d2 = Assert.Single(group.Metrics, m => m.Label == "Cases filed against vs by the company");
        Assert.True(d2.HasValue);
        Assert.Equal(0m, d2.Value);
        Assert.Equal(MetricUnit.Count, d2.Unit);
        Assert.Equal("0 filed against, 0 filed by, 952 role not determined", d2.Period);

        // D3: Category breakdown — 19 distinct categories across Confirmed + Probable (660 cases)
        var d3Metrics = group.Metrics.Where(m => m.Label.StartsWith("Cases by category (")).ToList();
        Assert.Equal(19, d3Metrics.Count);
        Assert.Equal(175m, Assert.Single(d3Metrics, m => m.Label == "Cases by category (Civil Cases)").Value);
        Assert.Equal(107m, Assert.Single(d3Metrics, m => m.Label == "Cases by category (Writs and Regulatory Matters)").Value);
        Assert.Equal(71m, Assert.Single(d3Metrics, m => m.Label == "Cases by category (Insolvency)").Value);
        Assert.Equal(69m, Assert.Single(d3Metrics, m => m.Label == "Cases by category (NI Act / Company Offences)").Value);
        Assert.Equal(57m, Assert.Single(d3Metrics, m => m.Label == "Cases by category (Others)").Value);
        Assert.Equal(55m, Assert.Single(d3Metrics, m => m.Label == "Cases by category (Arbitration Matters)").Value);
        Assert.Equal(660m, d3Metrics.Sum(m => m.Value!.Value));

        // D4: Court breakdown — 5 non-empty court types in Confirmed + Probable (660 cases)
        // High Court = 342, Other = 167, NCLT = 96, District Court = 35, DRT = 20 (Consumer Court = 0, omitted)
        var d4Metrics = group.Metrics.Where(m => m.Label.StartsWith("Cases by court type (")).ToList();
        Assert.Equal(5, d4Metrics.Count);
        Assert.Equal(342m, Assert.Single(d4Metrics, m => m.Label == "Cases by court type (High Court)").Value);
        Assert.Equal(167m, Assert.Single(d4Metrics, m => m.Label == "Cases by court type (Other)").Value);
        Assert.Equal(96m, Assert.Single(d4Metrics, m => m.Label == "Cases by court type (NCLT)").Value);
        Assert.Equal(35m, Assert.Single(d4Metrics, m => m.Label == "Cases by court type (District Court)").Value);
        Assert.Equal(20m, Assert.Single(d4Metrics, m => m.Label == "Cases by court type (DRT)").Value);
        Assert.DoesNotContain(d4Metrics, m => m.Label == "Cases by court type (Consumer Court)");
        Assert.Equal(660m, d4Metrics.Sum(m => m.Value!.Value));

        // D5: NCLT / insolvency case count = 96
        var d5 = Assert.Single(group.Metrics, m => m.Label == "NCLT / insolvency case count");
        Assert.True(d5.HasValue);
        Assert.Equal(96m, d5.Value);
        Assert.Equal(MetricUnit.Count, d5.Unit);
        Assert.Equal("as at 28 Oct 2022 (96 of 660 confirmed/probable cases)", d5.Period);

        // D6: DRT case count = 20
        var d6 = Assert.Single(group.Metrics, m => m.Label == "DRT case count");
        Assert.True(d6.HasValue);
        Assert.Equal(20m, d6.Value);
        Assert.Equal(MetricUnit.Count, d6.Unit);
        Assert.Equal("as at 28 Oct 2022 (20 of 660 confirmed/probable cases)", d6.Period);

        // D7: Pending vs disposed ratio = 127 / 592 = 21.5%
        var d7 = Assert.Single(group.Metrics, m => m.Label == "Pending vs disposed ratio");
        Assert.True(d7.HasValue);
        Assert.Equal(21.5m, d7.Value);
        Assert.Equal(MetricUnit.Percent, d7.Unit);
        Assert.Equal("127 pending, 465 disposed of 592 assessed", d7.Period);

        // D8: Probable + Uncertain exposure count = 68 + 292 = 360
        var d8 = Assert.Single(group.Metrics, m => m.Label == "Probable + Uncertain exposure count");
        Assert.True(d8.HasValue);
        Assert.Equal(360m, d8.Value);
        Assert.Equal(MetricUnit.Count, d8.Unit);
        Assert.Equal("68 probable, 292 unverified (name-match only / court not reached)", d8.Period);
    }
}
