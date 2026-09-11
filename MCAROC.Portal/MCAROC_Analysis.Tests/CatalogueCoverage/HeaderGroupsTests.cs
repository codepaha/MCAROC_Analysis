using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests.CatalogueCoverage;

/// <summary>Synthetic-data pins for the A7 (#38) matching algorithm and the trickier header-location
/// rules, independent of the real (git-ignored, self-hosted-only) fixtures — these run on every CI job.</summary>
public class HeaderGroupsTests
{
    [Theory]
    [InlineData("GSTIN STATUS", "GSTIN STATUS")]
    [InlineData("  Paid Up Capital  ", "PAID UP CAPITAL")]
    [InlineData("Amount (Rs. Crore)", "AMOUNT RS CRORE")]
    [InlineData("Number Of\nHolders", "NUMBER OF HOLDERS")]
    [InlineData("Litigant(s)", "LITIGANT S")]
    [InlineData("", "")]
    public void Norm_uppercases_and_collapses_punctuation_and_whitespace(string input, string expected) =>
        Assert.Equal(expected, CatalogueCoverageTests.Norm(input));

    [Fact]
    public void Chunks_splits_on_bare_or_spaced_slash()
    {
        var spaced = new CatalogueField("A / B / C", "live", null);
        var bare = new CatalogueField("A/B/C", "live", null);
        Assert.Equal(["A", "B", "C"], spaced.Chunks.ToArray());
        Assert.Equal(["A", "B", "C"], bare.Chunks.ToArray());
    }

    [Fact]
    public void Structure_grid_title_ends_the_summary_block_without_leaking_grid_content()
    {
        var sheet = Sheet("Structure",
            Row("SHARE HOLDING SUMMARY"),
            Row("Promoter %", 60.0),
            Row("Public %", 40.0),
            Row(""),
            Row("PROMOTERS - 31 Mar, 2020"),
            Row("CATEGORY", "EQUITY", "", "PREFERENCE", ""),
            Row("", "Number of Shares", "Percentage", "Number of Shares", "Percentage"),
            Row("(i) Indian", 600.0, 60.0, 0.0, "-"));

        var groups = HeaderGroups.Extract("RocReport", sheet);

        var labels = Assert.Single(groups).Cells;
        Assert.Equal(["Promoter %", "Public %"], labels);
        Assert.DoesNotContain("CATEGORY", labels);
        Assert.DoesNotContain("SHARE HOLDING SUMMARY", labels);
        Assert.DoesNotContain("(i) Indian", labels);
    }

    [Fact]
    public void Legal_history_extracts_all_three_stacked_sub_table_headers()
    {
        var sheet = Sheet("Legal History",
            Row("LEGAL HISTORY"),
            Row("Case Type", "Case Status", "Case Category", "Court", "Litigant(s)", "Case No.", "Date"),
            Row("Filed Against this Corporate", "Pending", "Insolvency", "NCLT", "Bank", "CP123", "1 Jan, 2020"),
            Row(""),
            Row("PROBABLE CASES"),
            Row("Case Status", "Case Category", "Court", "Petitioner(s)", "Respondent(s)", "Case No.", "Date"),
            Row("Pending", "Insolvency", "NCLAT", "A", "B", "CA1", "2 Feb, 2020"),
            Row(""),
            Row("UNVERIFIED COURT RECORDS"),
            Row("Court", "Petitioner(s)", "Respondent(s)", "Case No.", "Date of Last Activity"),
            Row("District Court", "C", "D", "AA1", "3 Mar, 2020"));

        var groups = HeaderGroups.Extract("RocReport", sheet);

        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.Label == "Confirmed header" && g.Cells.Contains("Litigant(s)"));
        Assert.Contains(groups, g => g.Label == "PROBABLE CASES header" && g.Cells.Contains("Petitioner(s)") && g.Cells.Contains("Respondent(s)"));
        Assert.Contains(groups, g => g.Label == "UNVERIFIED COURT RECORDS header" && g.Cells.Contains("Date of Last Activity"));
    }

    [Fact]
    public void Financial_data_matrix_is_excluded_from_header_matching()
    {
        var sheet = Sheet("Standalone Financial Data",
            Row("BALANCE SHEET - AOC-4 (Rs. Crore)", "", "31 Mar, 2016", "31 Mar, 2017"),
            Row("Share Capital", "", 100.0, 110.0));

        Assert.Empty(HeaderGroups.Extract("RocReport", sheet));
    }
}
