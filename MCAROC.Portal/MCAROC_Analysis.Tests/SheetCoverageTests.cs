using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Excel;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>A11 — <see cref="SheetCoverage"/> turns an ingestion run's recorded absent-sheet list into
/// the conditional empty-state text ("not in this upload" vs "present, reported no records") and the
/// "N of M optional sheets present" count.</summary>
public class SheetCoverageTests
{
    private static IngestionRun Run(string absentJson, bool chargeReportMissing = false) => new()
    {
        AbsentOptionalSheetsJson = absentJson,
        ChargeReportMissing = chargeReportMissing,
    };

    [Fact]
    public void Null_run_is_the_empty_coverage()
    {
        var cov = SheetCoverage.From(null);

        Assert.False(cov.AnySheetAbsent);
        Assert.False(cov.ChargeReportMissing);
        Assert.Equal(cov.TotalOptionalSheets, cov.PresentOptionalSheets);
        Assert.Empty(cov.AbsentSheets);
    }

    [Fact]
    public void Absent_sheet_flips_the_empty_state_text_and_the_count()
    {
        var legal = SheetAliases.CanonicalName(SheetAliases.LegalHistory);
        var cov = SheetCoverage.From(Run($"[\"{legal}\"]"));

        Assert.True(cov.WasAbsent(SheetAliases.LegalHistory));
        Assert.False(cov.WasAbsent(SheetAliases.Directors));
        Assert.Equal(cov.TotalOptionalSheets - 1, cov.PresentOptionalSheets);

        Assert.Equal(
            $"This workbook did not include a “{legal}” sheet.",
            cov.EmptyState("present-but-empty", SheetAliases.LegalHistory));
        Assert.Equal(
            "present-but-empty",
            cov.EmptyState("present-but-empty", SheetAliases.Directors));
    }

    [Fact]
    public void Multi_sheet_section_only_reads_as_absent_when_every_feeding_sheet_is_absent()
    {
        var dirSh = SheetAliases.CanonicalName(SheetAliases.DirectorShareholding);
        var onlyOneAbsent = SheetCoverage.From(Run($"[\"{dirSh}\"]"));

        // "Shareholding More Than 5%" was still present → the section was present, keep the plain text.
        Assert.False(onlyOneAbsent.AllAbsent(SheetAliases.DirectorShareholding, SheetAliases.MajorShareholding));
        Assert.Equal("present-but-empty",
            onlyOneAbsent.EmptyState("present-but-empty", SheetAliases.DirectorShareholding, SheetAliases.MajorShareholding));

        var bothAbsent = SheetCoverage.From(Run(
            $"[\"{dirSh}\",\"{SheetAliases.CanonicalName(SheetAliases.MajorShareholding)}\"]"));
        Assert.True(bothAbsent.AllAbsent(SheetAliases.DirectorShareholding, SheetAliases.MajorShareholding));
        Assert.Contains("did not include", bothAbsent.EmptyState("x", SheetAliases.DirectorShareholding, SheetAliases.MajorShareholding));
        Assert.Contains(" or ", bothAbsent.EmptyState("x", SheetAliases.DirectorShareholding, SheetAliases.MajorShareholding));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("")]
    public void Malformed_or_non_array_json_is_treated_as_full_coverage(string json)
    {
        var cov = SheetCoverage.From(Run(json));

        Assert.False(cov.AnySheetAbsent);
        Assert.Equal(cov.TotalOptionalSheets, cov.PresentOptionalSheets);
    }

    [Fact]
    public void Unrecognised_sheet_names_in_the_stored_list_are_ignored()
    {
        // A run recorded under an older tracked-sheet list must not inflate "M" or surface a stale name.
        var cov = SheetCoverage.From(Run("[\"Some Sheet We No Longer Track\",\"Another Ghost\"]"));

        Assert.False(cov.AnySheetAbsent);
        Assert.Equal(cov.TotalOptionalSheets, cov.PresentOptionalSheets);
    }

    [Fact]
    public void Charge_report_missing_is_carried_through()
    {
        Assert.True(SheetCoverage.From(Run("[]", chargeReportMissing: true)).ChargeReportMissing);
        Assert.False(SheetCoverage.From(Run("[]")).ChargeReportMissing);
    }
}
