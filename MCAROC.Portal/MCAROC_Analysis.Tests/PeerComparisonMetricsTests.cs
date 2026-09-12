using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class PeerComparisonMetricsTests
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
        List<PeerComparisonMetric>? metrics = null,
        List<PeerCompany>? closestPeers = null,
        string companyName = "Test Company",
        string? coverCin = "U12345AB2020PTC123456",
        string? structureCin = null)
    {
        var rDate = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
        var structure = structureCin is not null
            ? new CompanyStructure { Cin = structureCin }
            : null;

        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover(
                companyName, coverCin, "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", rDate, null, null),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], structure, null, []),
            Financials: new DossierFinancials([], [], [], [], [], metrics ?? [], closestPeers),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    private static MetricResult M(MetricGroup group, string label) =>
        Assert.Single(group.Metrics, m => m.Label == label);

    private static MetricResult M(MetricGroup group, string label, string periodSubstring) =>
        Assert.Single(group.Metrics, m => m.Label == label && m.Period.Contains(periodSubstring));

    // ─────────────────────────────────────────────────────────────────────────
    // Real COASTAL Fixture Controls
    // ─────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void RealCoastalFixture_ExtractsExactControlTotalsAndMetrics()
    {
        var fixture = RocFixture();
        Skip.If(fixture is null, "Reconciliation workbooks not present");

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var peerSheet = new ExcelSheetReader().ReadWorkbook(fixture).Single(s => s.Name == "Peer Comparison");
        var r = PeerComparisonParser.Parse(peerSheet, requestId: 1, ingestionRunId: 1, sourceDocumentId: 1, out var peers);

        var model = new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover(
                "COASTAL PROJECTS LIMITED", "U45203OR1995PLC003982", "AACCC0000A",
                new DateOnly(1995, 1, 1), "Active", "Client", new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), null, null),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, []),
            Financials: new DossierFinancials([], [], [], [], [], r.Items, peers),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);

        var group = DossierComputations.PeerComparisonMetrics(model);
        Assert.Equal("Peer comparison", group.Title);
        Assert.True(group.HasAny);

        // J2: Rank in source closest-peer list (Coastal is rank 3 of 5, 1284.20 Cr, FY2017)
        var j2 = M(group, "Rank in source closest-peer list");
        Assert.True(j2.HasValue);
        Assert.Equal(3m, j2.Value);
        Assert.Equal(MetricUnit.Count, j2.Unit);
        Assert.Equal("3", j2.DisplayValue());
        Assert.Contains("Rank 3 of 5 closest peers by revenue", j2.Period);
        Assert.Contains("1284.2", j2.Period);
        Assert.Contains("FY2017", j2.Period);

        // J3: Count of peers in sample (30 in FY2017)
        var j3 = M(group, "Count of peers in sample");
        Assert.True(j3.HasValue);
        Assert.Equal(30m, j3.Value);
        Assert.Equal(MetricUnit.Count, j3.Unit);
        Assert.Contains("FY2017", j3.Period);
        Assert.Contains("Infrastructure", j3.Period);

        // J1: Multi-year metric deltas
        // FY2017 EBITDA Margin: Company 40.8% vs Median 8.9% -> +31.90%, favourable
        var ebitda2017 = M(group, "EBITDA Margin (%) vs peer median", "FY2017");
        Assert.True(ebitda2017.HasValue);
        Assert.Equal(31.9m, ebitda2017.Value);
        Assert.Equal(MetricUnit.Percent, ebitda2017.Unit);
        Assert.Contains("Company 40.8 vs Median 8.9", ebitda2017.Period);
        Assert.Contains("Above / favourable", ebitda2017.Period);

        // FY2017 Debtors / Sales: Company 480 vs Median 95 -> +385.00 days, adverse
        var debtors2017 = M(group, "Debtors / Sales (Days) vs peer median", "FY2017");
        Assert.True(debtors2017.HasValue);
        Assert.Equal(385m, debtors2017.Value);
        Assert.Equal(MetricUnit.Days, debtors2017.Unit);
        Assert.Contains("Company 480 vs Median 95", debtors2017.Period);
        Assert.Contains("Above / adverse", debtors2017.Period);

        // Output order: J2 first, J3 second, followed by J1 rows
        Assert.Equal("Rank in source closest-peer list", group.Metrics[0].Label);
        Assert.Equal("Count of peers in sample", group.Metrics[1].Label);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J2: Rank in source closest-peer list tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void J2_EmptyClosestPeers_FailsClosed()
    {
        var model = CreateMinimalDossier(closestPeers: []);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Equal(MetricUnit.Count, j2.Unit);
        Assert.Equal("Closest peers block not reported in workbook", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_MissingFinancialYearOnAnyPeer_FailsClosed()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Peer 1", Cin = "U001", RevenueCrore = 200m, FinancialYear = 2024 },
            new() { Rank = 2, LegalName = "This Co", Cin = "U12345AB2020PTC123456", RevenueCrore = 150m, FinancialYear = null }
        };
        var model = CreateMinimalDossier(closestPeers: peers);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Equal("Closest peers block contains missing financial year(s)", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_InconsistentFinancialYearsAcrossPeers_FailsClosed()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Peer 1", Cin = "U001", RevenueCrore = 200m, FinancialYear = 2023 },
            new() { Rank = 2, LegalName = "This Co", Cin = "U12345AB2020PTC123456", RevenueCrore = 150m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Contains("Closest peers block contains inconsistent financial years", j2.InsufficiencyReason);
    }

    [Theory]
    [InlineData(0, 1)]      // Rank 0 is invalid
    [InlineData(1, 3)]      // Rank 3 is out of range for N=2
    [InlineData(1, 1)]      // Duplicate rank 1
    public void J2_InvalidOrDuplicateRanks_FailsClosed(int rank1, int rank2)
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = rank1, LegalName = "Peer 1", Cin = "U001", RevenueCrore = 200m, FinancialYear = 2024 },
            new() { Rank = rank2, LegalName = "This Co", Cin = "U12345AB2020PTC123456", RevenueCrore = 150m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Contains("Closest peers block has invalid or duplicate ranks", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_IdentityConflictBetweenCoverAndStructureCin_FailsClosed()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "This Co", Cin = "U12345AB2020PTC123456", RevenueCrore = 150m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers, coverCin: "U12345AB2020PTC123456", structureCin: "U99999XX2020PTC999999");
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Equal("Identity conflict: Cover CIN and Structure CIN mismatch", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_CinMatching_IgnoresWhitespaceAndCasing()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Peer 1", Cin = "U001", RevenueCrore = 200m, FinancialYear = 2024 },
            new() { Rank = 2, LegalName = "This Co", Cin = "  u12345ab2020ptc123456  ", RevenueCrore = 150m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers, coverCin: "U12345AB2020PTC123456");
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.True(j2.HasValue);
        Assert.Equal(2m, j2.Value);
    }

    [Fact]
    public void J2_DuplicateCinMatches_FailsClosed()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Co A", Cin = "U12345AB2020PTC123456", RevenueCrore = 200m, FinancialYear = 2024 },
            new() { Rank = 2, LegalName = "Co B", Cin = "U12345AB2020PTC123456", RevenueCrore = 150m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers, coverCin: "U12345AB2020PTC123456");
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Contains("Duplicate CIN matches in closest peers list", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_CinAvailableButUnmatched_FailsClosedWithoutFallingBackToName()
    {
        // Company has CIN "U12345AB2020PTC123456" and Name "Alpha Beta Ltd".
        // Peer list has "Alpha Beta Ltd" with a DIFFERENT CIN "U99999XX2020PTC999999".
        // Must fail closed on CIN not found — do NOT match on name!
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Alpha Beta Ltd", Cin = "U99999XX2020PTC999999", RevenueCrore = 200m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers, companyName: "Alpha Beta Ltd", coverCin: "U12345AB2020PTC123456");
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Equal("Company CIN not found in closest peers list", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_CinUnavailable_MatchesExactLegalNameIgnoringWhitespaceAndCasing()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Other Corp", Cin = "U001", RevenueCrore = 300m, FinancialYear = 2024 },
            new() { Rank = 2, LegalName = "  ALPHA   BETA   LIMITED  ", Cin = null, RevenueCrore = 200m, FinancialYear = 2024 }
        };
        // CIN is null
        var model = CreateMinimalDossier(closestPeers: peers, companyName: "alpha beta limited", coverCin: null);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.True(j2.HasValue);
        Assert.Equal(2m, j2.Value);
    }

    [Fact]
    public void J2_CinUnavailable_NonMatchingNearNameFailsClosed()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Alpha Beta India Limited", Cin = null, RevenueCrore = 200m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers, companyName: "Alpha Beta Limited", coverCin: null);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Equal("Company name not found in closest peers list", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_CinUnavailable_DuplicateLegalNameMatches_FailsClosed()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "Alpha Beta Ltd", Cin = null, RevenueCrore = 200m, FinancialYear = 2024 },
            new() { Rank = 2, LegalName = "ALPHA BETA LTD", Cin = null, RevenueCrore = 150m, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers, companyName: "Alpha Beta Ltd", coverCin: null);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Contains("Duplicate company name matches in closest peers list", j2.InsufficiencyReason);
    }

    [Fact]
    public void J2_NullSelfRevenue_FailsClosed()
    {
        var peers = new List<PeerCompany>
        {
            new() { Rank = 1, LegalName = "This Co", Cin = "U12345AB2020PTC123456", RevenueCrore = null, FinancialYear = 2024 }
        };
        var model = CreateMinimalDossier(closestPeers: peers, coverCin: "U12345AB2020PTC123456");
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j2 = M(group, "Rank in source closest-peer list");

        Assert.False(j2.HasValue);
        Assert.Equal("Company revenue not reported in closest peers list", j2.InsufficiencyReason);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J3: Count of peers in sample tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void J3_EmptyPeerMetrics_FailsClosed()
    {
        var model = CreateMinimalDossier(metrics: []);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j3 = M(group, "Count of peers in sample");

        Assert.False(j3.HasValue);
        Assert.Equal("No peer comparison records on file", j3.InsufficiencyReason);
    }

    [Fact]
    public void J3_AllPeerCountsNullForReferenceYear_FailsClosed()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Current Ratio", FinancialYear = 2024, PeerCount = null }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j3 = M(group, "Count of peers in sample");

        Assert.False(j3.HasValue);
        Assert.Equal("Peer count metadata not reported for FY2024", j3.InsufficiencyReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void J3_NonPositivePeerCount_FailsClosed(int invalidCount)
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Current Ratio", FinancialYear = 2024, PeerCount = invalidCount }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j3 = M(group, "Count of peers in sample");

        Assert.False(j3.HasValue);
        Assert.Equal("Invalid non-positive peer count metadata for FY2024", j3.InsufficiencyReason);
    }

    [Fact]
    public void J3_InconsistentPeerCountsForReferenceYear_FailsClosed()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Current Ratio", FinancialYear = 2024, PeerCount = 25 },
            new() { MetricName = "Quick Ratio", FinancialYear = 2024, PeerCount = 30 }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j3 = M(group, "Count of peers in sample");

        Assert.False(j3.HasValue);
        Assert.Contains("Inconsistent peer sample counts reported for FY2024", j3.InsufficiencyReason);
        Assert.Contains("25", j3.InsufficiencyReason);
        Assert.Contains("30", j3.InsufficiencyReason);
    }

    [Fact]
    public void J3_ConsistentCount_EmitsOk()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Current Ratio", FinancialYear = 2024, PeerCount = 28, Industry = "Manufacturing", Segment = "Steel" },
            new() { MetricName = "Quick Ratio", FinancialYear = 2024, PeerCount = 28, Industry = "Manufacturing", Segment = "Steel" }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j3 = M(group, "Count of peers in sample");

        Assert.True(j3.HasValue);
        Assert.Equal(28m, j3.Value);
        Assert.Equal(MetricUnit.Count, j3.Unit);
        Assert.Equal("FY2024 (Manufacturing · Steel)", j3.Period);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // J1: Metric vs peer median tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void J1_EmptyPeerMetrics_EmitsInsufficientWithUnspecifiedUnit()
    {
        var model = CreateMinimalDossier(metrics: []);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var j1 = M(group, "Metric vs peer median");

        Assert.False(j1.HasValue);
        Assert.Equal(MetricUnit.Unspecified, j1.Unit);
        Assert.Equal("No peer comparison records on file", j1.InsufficiencyReason);
    }

    [Fact]
    public void J1_UnknownMetricName_EmitsInsufficientWithUnspecifiedUnitCitingAllowList()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Custom Non-Standard Metric", FinancialYear = 2024, CompanyValue = 10m, PeerMedianValue = 5m }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var m = M(group, "Custom Non-Standard Metric vs peer median");

        Assert.False(m.HasValue);
        Assert.Equal(MetricUnit.Unspecified, m.Unit);
        Assert.Contains("Unknown peer metric 'Custom Non-Standard Metric' — unit and direction not defined in catalogue allow-list", m.InsufficiencyReason);
    }

    [Fact]
    public void J1_DuplicateMetricAndYear_EmitsExactlyOneInsufficientPerGroup()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Current Ratio", FinancialYear = 2024, CompanyValue = 1.5m, PeerMedianValue = 1.2m },
            new() { MetricName = "  CURRENT   RATIO  ", FinancialYear = 2024, CompanyValue = 1.6m, PeerMedianValue = 1.2m }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);

        // Exactly one result for "Current Ratio vs peer median"
        var m = Assert.Single(group.Metrics, x => x.Label == "Current Ratio vs peer median");
        Assert.False(m.HasValue);
        Assert.Equal(MetricUnit.Ratio, m.Unit);
        Assert.Contains("Duplicate peer comparison records on file for 'Current Ratio' in FY2024", m.InsufficiencyReason);
    }

    [Fact]
    public void J1_NullCompanyValue_EmitsInsufficient()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Quick Ratio", FinancialYear = 2024, CompanyValue = null, PeerMedianValue = 1.0m }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var m = M(group, "Quick Ratio vs peer median");

        Assert.False(m.HasValue);
        Assert.Equal(MetricUnit.Ratio, m.Unit);
        Assert.Equal("Company value not reported for 'Quick Ratio' in FY2024", m.InsufficiencyReason);
    }

    [Fact]
    public void J1_NullPeerMedianValue_EmitsInsufficient()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Quick Ratio", FinancialYear = 2024, CompanyValue = 1.2m, PeerMedianValue = null }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var m = M(group, "Quick Ratio vs peer median");

        Assert.False(m.HasValue);
        Assert.Equal(MetricUnit.Ratio, m.Unit);
        Assert.Equal("Peer median not reported for 'Quick Ratio' in FY2024", m.InsufficiencyReason);
    }

    [Fact]
    public void J1_ZeroDelta_EmitsOkWithNeutralSentiment()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Current Ratio", FinancialYear = 2024, CompanyValue = 1.50m, PeerMedianValue = 1.50m }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var m = M(group, "Current Ratio vs peer median");

        Assert.True(m.HasValue);
        Assert.Equal(0m, m.Value);
        Assert.Equal(MetricUnit.Ratio, m.Unit);
        Assert.Contains("InLine / neutral", m.Period);
    }

    [Fact]
    public void J1_NegativeDelta_EmitsOkWithNegativeValue()
    {
        // Net Profit Margin: Company 5% vs Median 10% -> -5% (adverse since HigherIsBetter)
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Net Profit Margin (%)", FinancialYear = 2024, CompanyValue = 5.0m, PeerMedianValue = 10.0m }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var m = M(group, "Net Profit Margin (%) vs peer median");

        Assert.True(m.HasValue);
        Assert.Equal(-5.0m, m.Value);
        Assert.Equal(MetricUnit.Percent, m.Unit);
        Assert.Contains("Below / adverse", m.Period);
    }

    [Fact]
    public void J1_LowerIsBetterMetric_CorrectSentimentDerivation()
    {
        // Debt / Equity: LowerIsBetter. Company 0.5 vs Median 1.0 -> Below / favourable!
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Debt / Equity", FinancialYear = 2024, CompanyValue = 0.5m, PeerMedianValue = 1.0m }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);
        var m = M(group, "Debt / Equity vs peer median");

        Assert.True(m.HasValue);
        Assert.Equal(-0.5m, m.Value);
        Assert.Equal(MetricUnit.Ratio, m.Unit);
        Assert.Contains("Below / favourable", m.Period);
    }

    [Fact]
    public void J1_MultiYearPreservationAndDeterministicOrdering()
    {
        var metrics = new List<PeerComparisonMetric>
        {
            new() { MetricName = "Current Ratio", FinancialYear = 2023, CompanyValue = 1.2m, PeerMedianValue = 1.1m },
            new() { MetricName = "EBITDA Margin (%)", FinancialYear = 2023, CompanyValue = 12m, PeerMedianValue = 10m },
            new() { MetricName = "Current Ratio", FinancialYear = 2024, CompanyValue = 1.5m, PeerMedianValue = 1.2m },
            new() { MetricName = "EBITDA Margin (%)", FinancialYear = 2024, CompanyValue = 15m, PeerMedianValue = 10m }
        };
        var model = CreateMinimalDossier(metrics: metrics);
        var group = DossierComputations.PeerComparisonMetrics(model);

        // Group output order: J2, J3, then J1 rows (FY 2024 first by CanonicalName, then FY 2023 by CanonicalName)
        var j1Metrics = group.Metrics.Skip(2).ToList();
        Assert.Equal(4, j1Metrics.Count);

        Assert.Equal("Current Ratio vs peer median", j1Metrics[0].Label);
        Assert.Contains("FY2024", j1Metrics[0].Period);

        Assert.Equal("EBITDA Margin (%) vs peer median", j1Metrics[1].Label);
        Assert.Contains("FY2024", j1Metrics[1].Period);

        Assert.Equal("Current Ratio vs peer median", j1Metrics[2].Label);
        Assert.Contains("FY2023", j1Metrics[2].Period);

        Assert.Equal("EBITDA Margin (%) vs peer median", j1Metrics[3].Label);
        Assert.Contains("FY2023", j1Metrics[3].Period);
    }
}
