using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class DirectorsMetricsTests
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
        List<Director> directors,
        DateTime? sourceSnapshotDate = null,
        DateTime? mcaDataAsOf = null,
        DateTime? reportDate = null)
    {
        for (var i = 0; i < directors.Count; i++)
        {
            if (directors[i].DirectorId == 0)
                directors[i].DirectorId = i + 1;
        }

        var rDate = reportDate ?? new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);

        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover(
                "Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", rDate, mcaDataAsOf, sourceSnapshotDate),
            Corporate: new DossierCorporate(directors, [], [], [], [], [], [], null),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    [Theory]
    [InlineData("Non-Executive Independent Director", "Independent Director")]
    [InlineData("Independent Director", "Independent Director")]
    [InlineData("Non-Executive Director", "Non-Executive Director")]
    [InlineData("Non Executive Director", "Non-Executive Director")]
    [InlineData("NonExecutive Director", "Non-Executive Director")]
    [InlineData("Executive Director", "Whole-time Director")]
    [InlineData("Whole-time Director", "Whole-time Director")]
    [InlineData("Wholetime Director", "Whole-time Director")]
    [InlineData("Whole Time Director", "Whole-time Director")]
    [InlineData("Managing Director", "Managing Director")]
    [InlineData("Nominee Director", "Nominee Director")]
    [InlineData("Alternate Director", "Alternate Director")]
    [InlineData("Additional Director", "Additional Director")]
    [InlineData("Director", "Director")]
    [InlineData("Special Director", "Director")]
    [InlineData(null, "Other")]
    [InlineData("", "Other")]
    [InlineData("   ", "Other")]
    [InlineData("Advisor", "Other")]
    public void I4_designation_precedence_and_non_executive_independent_regression(string? input, string expected)
    {
        var actual = DossierComputations.NormalizeDirectorDesignation(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void I5_flags_null_whitespace_and_hyphen_regression()
    {
        var directors = new List<Director>
        {
            new() { NameRaw = "D1", Flags = null },
            new() { NameRaw = "D2", Flags = "" },
            new() { NameRaw = "D3", Flags = "   " },
            new() { NameRaw = "D4", Flags = "-" },
            new() { NameRaw = "D5", Flags = " - " },
            new() { NameRaw = "D6", Flags = "--" },
            new() { NameRaw = "D7", Flags = "DIR-3 KYC Deactivated" },
            new() { NameRaw = "D8", Flags = "Disqualified under Section 164(2)" }
        };

        var model = CreateMinimalDossier(directors);
        var group = DossierComputations.DirectorsMetrics(model);

        var i5 = Assert.Single(group.Metrics, m => m.Label == "Flagged director count");
        Assert.True(i5.HasValue);
        Assert.Equal(2m, i5.Value);
        Assert.Equal(MetricUnit.Count, i5.Unit);
        Assert.Equal("2 of 8 directors on record flagged", i5.Period);
    }

    [Fact]
    public void Future_appointment_dates_excluded_never_negative()
    {
        var anchor = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);
        var directors = new List<Director>
        {
            // Valid active director: 1.0 year tenure
            new() { NameRaw = "Past Director", CessationDate = null, OriginalAppointmentDate = new DateOnly(2025, 9, 9), Designation = "Director" },
            // Future active director: appointed in 2027
            new() { NameRaw = "Future Director", CessationDate = null, OriginalAppointmentDate = new DateOnly(2027, 1, 1), Designation = "Director" }
        };

        var model = CreateMinimalDossier(directors, sourceSnapshotDate: anchor);
        var group = DossierComputations.DirectorsMetrics(model);

        var i1 = Assert.Single(group.Metrics, m => m.Label == "Active director count");
        Assert.Equal(2m, i1.Value);

        var i2 = Assert.Single(group.Metrics, m => m.Label == "Average board tenure");
        Assert.True(i2.HasValue);
        Assert.Equal(1.0m, i2.Value);
        Assert.Contains("1 future appointment date(s) excluded", i2.Period);

        var i6 = Assert.Single(group.Metrics, m => m.Label == "Longest-serving director");
        Assert.True(i6.HasValue);
        Assert.Equal(1.0m, i6.Value);
        Assert.Equal("Past Director (appointed 9 Sep 2025) (1 future appointment date(s) excluded)", i6.Period);
    }

    [Fact]
    public void I4_designation_buckets_omitted_when_zero_active_directors()
    {
        var anchor = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);
        var directors = new List<Director>
        {
            new() { NameRaw = "Ceased 1", CessationDate = new DateOnly(2025, 1, 1), Designation = "Director" },
            new() { NameRaw = "Ceased 2", CessationDate = new DateOnly(2024, 6, 1), Designation = "Managing Director" }
        };

        var model = CreateMinimalDossier(directors, sourceSnapshotDate: anchor);
        var group = DossierComputations.DirectorsMetrics(model);

        // I1 is 0
        var i1 = Assert.Single(group.Metrics, m => m.Label == "Active director count");
        Assert.Equal(0m, i1.Value);

        // Designation buckets MUST be omitted completely (contract: buckets only emitted for populated active groups)
        Assert.DoesNotContain(group.Metrics, m => m.Label.StartsWith("Board composition by designation"));

        // I2 and I6 are Insufficient
        var i2 = Assert.Single(group.Metrics, m => m.Label == "Average board tenure");
        Assert.False(i2.HasValue);
        Assert.Equal("0 active directors on record", i2.InsufficiencyReason);

        var i6 = Assert.Single(group.Metrics, m => m.Label == "Longest-serving director");
        Assert.False(i6.HasValue);
        Assert.Equal("0 active directors on record", i6.InsufficiencyReason);
    }

    [Fact]
    public void All_future_appointment_dates_fails_closed_as_insufficient()
    {
        var anchor = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc);
        var directors = new List<Director>
        {
            new() { NameRaw = "Future Director 1", CessationDate = null, OriginalAppointmentDate = new DateOnly(2027, 1, 1) },
            new() { NameRaw = "Future Director 2", CessationDate = null, OriginalAppointmentDate = new DateOnly(2027, 6, 1) }
        };

        var model = CreateMinimalDossier(directors, sourceSnapshotDate: anchor);
        var group = DossierComputations.DirectorsMetrics(model);

        var i2 = Assert.Single(group.Metrics, m => m.Label == "Average board tenure");
        Assert.False(i2.HasValue);
        Assert.Contains("future relative to source snapshot", i2.InsufficiencyReason);

        var i6 = Assert.Single(group.Metrics, m => m.Label == "Longest-serving director");
        Assert.False(i6.HasValue);
        Assert.Contains("future relative to source snapshot", i6.InsufficiencyReason);
    }

    [Fact]
    public void Strict_fail_closed_when_source_snapshot_date_null()
    {
        var directors = new List<Director>
        {
            new() { NameRaw = "Active Dir", CessationDate = null, OriginalAppointmentDate = new DateOnly(2020, 1, 1), Designation = "Director" },
            new() { NameRaw = "Ceased Dir", CessationDate = new DateOnly(2025, 1, 1), OriginalAppointmentDate = new DateOnly(2018, 1, 1) }
        };

        // SourceSnapshotDate is null, even though McaDataAsOf and ReportDate are populated
        var model = CreateMinimalDossier(
            directors,
            sourceSnapshotDate: null,
            mcaDataAsOf: new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc),
            reportDate: new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc));

        var group = DossierComputations.DirectorsMetrics(model);

        // I1, I4, I5 still compute
        var i1 = Assert.Single(group.Metrics, m => m.Label == "Active director count");
        Assert.True(i1.HasValue);
        Assert.Equal(1m, i1.Value);

        var i4 = Assert.Single(group.Metrics, m => m.Label == "Board composition by designation (Director)");
        Assert.True(i4.HasValue);
        Assert.Equal(1m, i4.Value);

        var i5 = Assert.Single(group.Metrics, m => m.Label == "Flagged director count");
        Assert.True(i5.HasValue);
        Assert.Equal(0m, i5.Value);

        // I2, I3, I6 MUST fail closed as Insufficient (zero fallback to McaDataAsOf)
        var i2 = Assert.Single(group.Metrics, m => m.Label == "Average board tenure");
        Assert.False(i2.HasValue);
        Assert.Equal("No trustworthy source snapshot date on file", i2.InsufficiencyReason);

        var i3 = Assert.Single(group.Metrics, m => m.Label == "Directors ceased in the trailing 3 years");
        Assert.False(i3.HasValue);
        Assert.Equal("No trustworthy source snapshot date on file", i3.InsufficiencyReason);

        var i6 = Assert.Single(group.Metrics, m => m.Label == "Longest-serving director");
        Assert.False(i6.HasValue);
        Assert.Equal("No trustworthy source snapshot date on file", i6.InsufficiencyReason);
    }

    [Fact]
    public void CompanyProfileParser_when_printed_at_missing_emits_warning_persists_null()
    {
        var sheet = new SheetData("About the Company",
        [
            new List<object?> { "Legal Name", "ACME CORP" },
            new List<object?> { "CIN", "U12345MH2020PTC123456" }
        ]);

        var result = CompanyProfileParser.Parse(sheet, 1, 1, 1, out _, out var snapshotDate);

        Assert.Null(snapshotDate);
        var warning = Assert.Single(result.Warnings, w => w.IssueCode == "MISSING_PRINTED_AT");
        Assert.Equal("SourceSnapshotDate", warning.FieldName);
    }

    [Fact]
    public void CompanyProfileParser_when_printed_at_malformed_emits_warning_with_provenance_and_persists_null()
    {
        var sheet = new SheetData("About the Company",
        [
            new List<object?> { "Legal Name", "ACME CORP" },
            new List<object?> { "CIN", "U12345MH2020PTC123456" },
            new List<object?> { "Printed at", "invalid date text" }
        ]);

        var result = CompanyProfileParser.Parse(sheet, 1, 1, 1, out _, out var snapshotDate);

        Assert.Null(snapshotDate);
        var warning = Assert.Single(result.Warnings, w => w.IssueCode == "BAD_DATETIME");
        Assert.Equal("SourceSnapshotDate", warning.FieldName);
        Assert.Equal("invalid date text", warning.RawValue);
        Assert.Equal(3, warning.RowNumber);
    }

    [Fact]
    public void CompanyProfileParser_when_printed_at_conflicting_emits_warning_and_persists_null()
    {
        var sheet = new SheetData("About the Company",
        [
            new List<object?> { "Legal Name", "ACME CORP" },
            new List<object?> { "CIN", "U12345MH2020PTC123456" },
            new List<object?> { "Printed at", "9 Sep, 2026 09:32 Hours" },
            new List<object?> { "Printed at", "10 Sep, 2026 10:00 Hours" }
        ]);

        var result = CompanyProfileParser.Parse(sheet, 1, 1, 1, out _, out var snapshotDate);

        Assert.Null(snapshotDate);
        var warning = Assert.Single(result.Warnings, w => w.IssueCode == "CONFLICTING_PRINTED_AT");
        Assert.Equal("SourceSnapshotDate", warning.FieldName);
        Assert.Equal(4, warning.RowNumber);
    }

    [Fact]
    public void Source_snapshot_date_differs_from_request_created_date_integration()
    {
        var sheet = new SheetData("About the Company",
        [
            new List<object?> { "Legal Name", "ACME CORP" },
            new List<object?> { "CIN", "U12345MH2020PTC123456" },
            new List<object?> { "Printed at", "9 Sep, 2026 09:32 Hours" }
        ]);

        var result = CompanyProfileParser.Parse(sheet, 1, 1, 1, out _, out var snapshotDate);
        Assert.NotNull(snapshotDate);
        Assert.Equal(new DateTime(2026, 9, 9, 9, 32, 0), snapshotDate.Value);

        var directors = new List<Director>
        {
            new() { NameRaw = "Director 1", CessationDate = null, OriginalAppointmentDate = new DateOnly(2024, 9, 9), Designation = "Director" }
        };

        // request CreatedDate / ReportDate is far in the future (2028), but SourceSnapshotDate is 2026-09-09
        var model = CreateMinimalDossier(
            directors,
            sourceSnapshotDate: snapshotDate.Value,
            reportDate: new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var group = DossierComputations.DirectorsMetrics(model);
        var i2 = Assert.Single(group.Metrics, m => m.Label == "Average board tenure");

        // 2026-09-09 - 2024-09-09 = 2.0 years (NOT 2028 - 2024 = 3.3+ years!)
        Assert.Equal(2.0m, i2.Value);
        Assert.Contains("as at 9 Sep 2026", i2.Period);
    }

    [SkippableFact]
    public void Coastal_fixture_exact_directors_metrics()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var roc = RocFixture();
        Skip.If(roc is null, "roc.xls not found");

        var sheets = new ExcelSheetReader().ReadWorkbook(roc);
        var aboutSheet = sheets.Single(s => s.Name == "About the Company");
        var directorsSheet = sheets.Single(s => s.Name == "Directors");

        var profileResult = CompanyProfileParser.Parse(aboutSheet, 1, 1, 1, out _, out var snapshotDate);
        Assert.NotNull(snapshotDate);
        Assert.Equal(new DateTime(2026, 9, 9, 9, 32, 0), snapshotDate.Value);

        var directorsResult = DirectorsParser.Parse(directorsSheet, 1, 1, 1, out var officers);
        var directors = directorsResult.Items;

        // COASTAL fixture totals: 24 directors + 3 non-DIN officers
        Assert.Equal(24, directors.Count);
        Assert.Equal(3, officers.Count);

        var model = CreateMinimalDossier(directors, sourceSnapshotDate: snapshotDate.Value);
        var group = DossierComputations.DirectorsMetrics(model);

        Assert.Equal("Directors", group.Title);
        Assert.True(group.HasAny);

        // I1: Active director count = 3
        var i1 = Assert.Single(group.Metrics, m => m.Label == "Active director count");
        Assert.True(i1.HasValue);
        Assert.Equal(3m, i1.Value);
        Assert.Equal(MetricUnit.Count, i1.Unit);
        Assert.Equal("as at 9 Sep 2026 (3 active of 24 directors on record)", i1.Period);

        // I2: Average board tenure = 1.0 yrs
        var i2 = Assert.Single(group.Metrics, m => m.Label == "Average board tenure");
        Assert.True(i2.HasValue);
        Assert.Equal(1.0m, i2.Value);
        Assert.Equal(MetricUnit.Years, i2.Unit);
        Assert.Equal("mean over 3 active directors as at 9 Sep 2026", i2.Period);

        // I3: Directors ceased in the trailing 3 years = 6
        var i3 = Assert.Single(group.Metrics, m => m.Label == "Directors ceased in the trailing 3 years");
        Assert.True(i3.HasValue);
        Assert.Equal(6m, i3.Value);
        Assert.Equal(MetricUnit.Count, i3.Unit);
        Assert.Equal("6 ceased between 9 Sep 2023 and 9 Sep 2026", i3.Period);

        // I4: Board composition by designation — Director = 2, Additional Director = 1
        var i4Director = Assert.Single(group.Metrics, m => m.Label == "Board composition by designation (Director)");
        Assert.True(i4Director.HasValue);
        Assert.Equal(2m, i4Director.Value);
        Assert.Equal("2 of 3 active directors", i4Director.Period);

        var i4AddDirector = Assert.Single(group.Metrics, m => m.Label == "Board composition by designation (Additional Director)");
        Assert.True(i4AddDirector.HasValue);
        Assert.Equal(1m, i4AddDirector.Value);
        Assert.Equal("1 of 3 active directors", i4AddDirector.Period);

        // I5: Flagged director count = 7
        var i5 = Assert.Single(group.Metrics, m => m.Label == "Flagged director count");
        Assert.True(i5.HasValue);
        Assert.Equal(7m, i5.Value);
        Assert.Equal(MetricUnit.Count, i5.Unit);
        Assert.Equal("7 of 24 directors on record flagged", i5.Period);

        // I6: Longest-serving director = 2.0 yrs
        var i6 = Assert.Single(group.Metrics, m => m.Label == "Longest-serving director");
        Assert.True(i6.HasValue);
        Assert.Equal(2.0m, i6.Value);
        Assert.Equal(MetricUnit.Years, i6.Unit);
        Assert.Equal("YENNETI RAJA RAMESWARA KRISHNA (appointed 5 Sep 2024)", i6.Period);
    }
}

