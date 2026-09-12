using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class RelatedPartyTransactionMetricsTests
{
    private static DossierModel CreateMinimalDossier(
        List<RelatedPartyTransaction>? rpts = null,
        List<FinancialYearData>? standalone = null,
        SheetCoverage? sourceCoverage = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], rpts ?? []),
            Financials: new DossierFinancials(standalone ?? [], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: sourceCoverage ?? SheetCoverage.Empty,
            Metrics: []);
    }

    private static SheetCoverage CoverageWithRptAbsent()
    {
        var run = new IngestionRun
        {
            AbsentOptionalSheetsJson = JsonSerializer.Serialize(new[]
            {
                SheetAliases.CanonicalName(SheetAliases.RelatedPartyTransactions)
            })
        };
        return SheetCoverage.From(run);
    }

    private static RelatedPartyTransaction Rpt(
        int? fyYear, string entity, string? type = null, string? relationship = null, decimal? amount = 1m) =>
        new()
        {
            FinancialYearEnding = fyYear is { } y ? new DateOnly(y, 3, 31) : null,
            EntityNameRaw = entity,
            EntityNameNormalized = entity.ToUpperInvariant(),
            TransactionType = type,
            RelationshipRaw = relationship,
            AmountCrore = amount
        };

    private static MetricResult Single(MetricGroup group, string label) =>
        Assert.Single(group.Metrics, m => m.Label == label);

    private static IEnumerable<MetricResult> Matching(MetricGroup group, string prefix) =>
        group.Metrics.Where(m => m.Label.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void Group_title_is_related_party_transactions()
    {
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier());
        Assert.Equal("Related-party transactions", group.Title);
    }

    [Fact]
    public void All_six_metrics_are_insufficient_with_sheet_absent_wording_when_no_rows_and_sheet_was_absent()
    {
        var model = CreateMinimalDossier(rpts: [], sourceCoverage: CoverageWithRptAbsent());
        var group = DossierComputations.RelatedPartyTransactionMetrics(model);

        Assert.Equal(6, group.Metrics.Count);
        Assert.All(group.Metrics, m =>
        {
            Assert.False(m.HasValue);
            Assert.Contains("not in this upload", m.InsufficiencyReason);
        });
    }

    [Fact]
    public void All_six_metrics_are_insufficient_with_no_rows_wording_when_sheet_was_present_but_empty()
    {
        var model = CreateMinimalDossier(rpts: []);
        var group = DossierComputations.RelatedPartyTransactionMetrics(model);

        Assert.Equal(6, group.Metrics.Count);
        Assert.All(group.Metrics, m =>
        {
            Assert.False(m.HasValue);
            Assert.DoesNotContain("not in this upload", m.InsufficiencyReason);
            Assert.Contains("No related-party-transaction rows reported", m.InsufficiencyReason);
        });
    }

    [Fact]
    public void E1_sums_amount_per_FY_and_excludes_undated_rows_from_the_bucket_but_notes_them()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2016, "A", amount: 10m),
            Rpt(2016, "B", amount: 5m),
            Rpt(2017, "C", amount: 20m),
            Rpt(null, "D", amount: 999m), // undated — must never leak into any FY bucket
        };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts));

        var fy2016 = Single(group, "Total RPT value per FY (FY2016)");
        Assert.True(fy2016.HasValue);
        Assert.Equal(15m, fy2016.Value);
        Assert.Contains("1 row(s) with no reported Financial Year Ending excluded", fy2016.Period);

        var fy2017 = Single(group, "Total RPT value per FY (FY2017)");
        Assert.True(fy2017.HasValue);
        Assert.Equal(20m, fy2017.Value);
    }

    [Fact]
    public void E1_fails_closed_for_a_FY_with_any_missing_amount_without_affecting_other_FYs()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2016, "A", amount: 10m),
            Rpt(2016, "B", amount: null),
            Rpt(2017, "C", amount: 20m),
        };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts));

        var fy2016 = Single(group, "Total RPT value per FY (FY2016)");
        Assert.False(fy2016.HasValue);
        Assert.Contains("1 of 2 transaction(s) have no reported Amount", fy2016.InsufficiencyReason);

        var fy2017 = Single(group, "Total RPT value per FY (FY2017)");
        Assert.True(fy2017.HasValue);
        Assert.Equal(20m, fy2017.Value);
    }

    [Fact]
    public void E2_computes_RPT_percent_of_revenue_for_matching_FY_only()
    {
        var rpts = new List<RelatedPartyTransaction> { Rpt(2017, "A", amount: 25m) };
        var standalone = new List<FinancialYearData> { new() { FinancialYear = 2017, Revenue = 500m } };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone));

        var m = Single(group, "RPT as % of revenue (FY2017)");
        Assert.True(m.HasValue);
        Assert.Equal(5.0m, m.Value); // 25 / 500 * 100
    }

    [Fact]
    public void E2_is_insufficient_when_revenue_for_that_FY_is_not_reported()
    {
        var rpts = new List<RelatedPartyTransaction> { Rpt(2017, "A", amount: 25m) };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone: []));

        var m = Single(group, "RPT as % of revenue (FY2017)");
        Assert.False(m.HasValue);
        Assert.Contains("Revenue not reported", m.InsufficiencyReason);
    }

    [Fact]
    public void E2_is_insufficient_when_revenue_for_that_FY_is_zero()
    {
        var rpts = new List<RelatedPartyTransaction> { Rpt(2017, "A", amount: 25m) };
        var standalone = new List<FinancialYearData> { new() { FinancialYear = 2017, Revenue = 0m } };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone)),
            "RPT as % of revenue (FY2017)");
        Assert.False(m.HasValue);
        Assert.Contains("Revenue is zero", m.InsufficiencyReason);
    }

    [Fact]
    public void E2_is_insufficient_for_a_FY_whose_E1_total_could_not_be_computed()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2017, "A", amount: 25m),
            Rpt(2017, "B", amount: null),
        };
        var standalone = new List<FinancialYearData> { new() { FinancialYear = 2017, Revenue = 500m } };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone)),
            "RPT as % of revenue (FY2017)");
        Assert.False(m.HasValue);
        Assert.Contains("total RPT value could not be computed", m.InsufficiencyReason);
    }

    [Fact]
    public void E3_groups_by_transaction_type_within_the_latest_FY_only()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2016, "A", type: "Sale of goods", amount: 999m), // older FY — must not leak into E3
            Rpt(2017, "B", type: "Sale of goods", amount: 10m),
            Rpt(2017, "C", type: "Sale of goods", amount: 5m),
            Rpt(2017, "D", type: "Loan given", amount: 20m),
        };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts));

        var byType = Matching(group, "RPT by transaction type (").ToList();
        Assert.Equal(2, byType.Count);
        var loan = Single(group, "RPT by transaction type (Loan given)");
        Assert.Equal(20m, loan.Value);
        Assert.Contains("FY2017", loan.Period);
        var sale = Single(group, "RPT by transaction type (Sale of goods)");
        Assert.Equal(15m, sale.Value); // only the two FY2017 rows, not the FY2016 999
    }

    [Fact]
    public void E3_excludes_untyped_rows_rather_than_inventing_an_unspecified_bucket()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2017, "A", type: "Loan given", amount: 20m),
            Rpt(2017, "B", type: null, amount: 5m),
        };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts));

        var byType = Matching(group, "RPT by transaction type (").ToList();
        Assert.Single(byType);
        Assert.Contains("1 with no reported type excluded", byType[0].Period);
    }

    [Fact]
    public void E3_is_insufficient_when_no_latest_FY_row_has_a_transaction_type()
    {
        var rpts = new List<RelatedPartyTransaction> { Rpt(2017, "A", type: null, amount: 5m) };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts)),
            "RPT by transaction type");
        Assert.False(m.HasValue);
        Assert.Contains("none with a reported TransactionType", m.InsufficiencyReason);
    }

    [Fact]
    public void E4_sums_only_subsidiary_labelled_relationships_in_the_latest_FY()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2016, "A", relationship: "Subsidiary", amount: 999m), // older FY
            Rpt(2017, "B", relationship: "Wholly-owned Subsidiary", amount: 30m),
            Rpt(2017, "C", relationship: "Key Managerial Personnel", amount: 8m),
        };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts)),
            "RPT to subsidiaries");
        Assert.True(m.HasValue);
        Assert.Equal(30m, m.Value);
        Assert.Contains("1 of 2 transaction(s)", m.Period);
    }

    [Fact]
    public void E4_is_a_valid_zero_not_insufficient_when_no_subsidiary_relationship_exists()
    {
        var rpts = new List<RelatedPartyTransaction> { Rpt(2017, "A", relationship: "Director", amount: 8m) };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts)),
            "RPT to subsidiaries");
        Assert.True(m.HasValue);
        Assert.Equal(0m, m.Value);
    }

    [Fact]
    public void E3_and_E4_fail_closed_when_the_latest_FY_has_any_missing_amount()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2017, "A", type: "Loan given", relationship: "Subsidiary", amount: null),
        };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts));
        Assert.False(Single(group, "RPT by transaction type").HasValue);
        Assert.False(Single(group, "RPT to subsidiaries").HasValue);
    }

    [Fact]
    public void E5_counts_distinct_normalized_entity_names_per_FY()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2017, "Alpha Pvt Ltd"),
            Rpt(2017, "Alpha Pvt Ltd"), // same entity twice in the same FY
            Rpt(2017, "Beta Pvt Ltd"),
            Rpt(2016, "Gamma Pvt Ltd"),
        };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts));

        var fy2017 = Single(group, "Distinct related entities transacted per FY (FY2017)");
        Assert.Equal(2m, fy2017.Value);
        var fy2016 = Single(group, "Distinct related entities transacted per FY (FY2016)");
        Assert.Equal(1m, fy2016.Value);
    }

    [Fact]
    public void E6_flags_true_when_RPT_CAGR_exceeds_revenue_CAGR()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2015, "A", amount: 10m),
            Rpt(2017, "B", amount: 40m), // RPT quadruples over 2 FYs
        };
        var standalone = new List<FinancialYearData>
        {
            new() { FinancialYear = 2015, Revenue = 100m },
            new() { FinancialYear = 2017, Revenue = 110m }, // revenue barely grows
        };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone)),
            "RPT growing faster than revenue");
        Assert.True(m.HasValue);
        Assert.Equal(1m, m.Value);
        Assert.Contains("RPT CAGR", m.Period);
        Assert.Contains("Revenue CAGR", m.Period);
    }

    [Fact]
    public void E6_flags_false_when_revenue_CAGR_exceeds_RPT_CAGR()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2015, "A", amount: 10m),
            Rpt(2017, "B", amount: 11m), // RPT barely grows
        };
        var standalone = new List<FinancialYearData>
        {
            new() { FinancialYear = 2015, Revenue = 100m },
            new() { FinancialYear = 2017, Revenue = 400m }, // revenue quadruples
        };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone)),
            "RPT growing faster than revenue");
        Assert.True(m.HasValue);
        Assert.Equal(0m, m.Value);
    }

    [Fact]
    public void E6_is_insufficient_when_fewer_than_2_FYs_of_RPT_totals_are_available()
    {
        var rpts = new List<RelatedPartyTransaction> { Rpt(2017, "A", amount: 10m) };
        var standalone = new List<FinancialYearData>
        {
            new() { FinancialYear = 2015, Revenue = 100m },
            new() { FinancialYear = 2017, Revenue = 200m },
        };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone)),
            "RPT growing faster than revenue");
        Assert.False(m.HasValue);
    }

    [Fact]
    public void E6_compares_both_CAGRs_over_the_same_shared_FY_window_not_each_series_own_full_history()
    {
        // PR #103 review counterexample: clean RPT totals exist only for FY2023/FY2024, but revenue is
        // also reported for FY2021. Computing each CAGR independently would compare RPT's FY23-24 100%
        // jump against revenue's FY21-24 ~44.2% CAGR and wrongly flag true — the comparable revenue
        // growth over the SAME FY23-24 window RPT actually has data for is 200%, so the flag must be false.
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2023, "A", amount: 10m),
            Rpt(2024, "B", amount: 20m),
        };
        var standalone = new List<FinancialYearData>
        {
            new() { FinancialYear = 2021, Revenue = 100m }, // no matching RPT year — must be excluded from the window
            new() { FinancialYear = 2023, Revenue = 100m },
            new() { FinancialYear = 2024, Revenue = 300m },
        };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone)),
            "RPT growing faster than revenue");
        Assert.True(m.HasValue);
        Assert.Equal(0m, m.Value); // RPT CAGR 100% (FY23-24) is NOT faster than Revenue CAGR 200% (FY23-24)
        Assert.Contains("RPT CAGR 100", m.Period);
        Assert.Contains("Revenue CAGR 200", m.Period);
        Assert.Contains("shared FY window", m.Period);
    }

    [Fact]
    public void E6_is_insufficient_when_RPT_and_revenue_share_no_common_FY()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2016, "A", amount: 10m),
            Rpt(2017, "B", amount: 20m),
        };
        var standalone = new List<FinancialYearData>
        {
            new() { FinancialYear = 2010, Revenue = 100m },
            new() { FinancialYear = 2020, Revenue = 200m },
        };
        var m = Single(DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone)),
            "RPT growing faster than revenue");
        Assert.False(m.HasValue);
        Assert.Contains("shared FY window", m.InsufficiencyReason);
    }

    [Fact]
    public void E6_does_not_mutate_the_published_group_with_its_internal_scratch_labels()
    {
        var rpts = new List<RelatedPartyTransaction>
        {
            Rpt(2015, "A", amount: 10m),
            Rpt(2017, "B", amount: 40m),
        };
        var standalone = new List<FinancialYearData>
        {
            new() { FinancialYear = 2015, Revenue = 100m },
            new() { FinancialYear = 2017, Revenue = 110m },
        };
        var group = DossierComputations.RelatedPartyTransactionMetrics(CreateMinimalDossier(rpts, standalone));
        Assert.DoesNotContain(group.Metrics, m => m.Label.Contains("internal"));
    }
}
