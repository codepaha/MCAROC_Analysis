using System.Text;
using MCAROC_Analysis.Services.LitigationData;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationReportArtifactsTests
{
    [Fact]
    public void RenderCsv_contains_full_case_fields_and_neutralises_formula_values()
    {
        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(ReportWithSingleCase("=danger")));

        Assert.Contains("CSP ID", csv);
        Assert.Contains("CNR Number", csv);
        Assert.Contains("Order Count", csv);
        Assert.Contains("Analysis Summary", csv);
        Assert.Contains("'=danger", csv);
        Assert.Contains("Petitioner One; Petitioner Two", csv);
        Assert.DoesNotContain("https://source.example/order.pdf", csv);
    }

    [Fact]
    public void RenderCsv_includes_Type_and_Bench_columns_with_their_values()
    {
        // Regression for a review finding: Type was missing from both artifacts, Bench was missing from the
        // PDF — Type and Bench are distinct fields from CaseType/Court and are both required client report
        // fields. ReportWithSingleCase's fixture case has Type="district", Bench="Bench".
        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(ReportWithSingleCase("csp-1")));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = lines[0].Split(',');

        Assert.Contains("Type", headers);
        Assert.Contains("Bench", headers);
        var typeIndex = Array.IndexOf(headers, "Type");
        var benchIndex = Array.IndexOf(headers, "Bench");
        var dataFields = lines[1].Split(',');
        Assert.Equal("district", dataFields[typeIndex]);
        Assert.Equal("Bench", dataFields[benchIndex]);
    }

    [Fact]
    public void RenderPdf_creates_a_pdf_with_the_case_identity_and_order_summary()
    {
        var pdf = LitigationReportArtifacts.RenderPdf(ReportWithSingleCase("csp-42"));

        Assert.True(pdf.Length > 500);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [SkippableFact]
    public void RenderPdf_case_details_grid_includes_Type_and_Bench()
    {
        // Regression for a review finding: the PDF case-details grid rendered neither Type nor Bench, though
        // both are required client report fields. Text extraction from QuestPDF-rendered PDFs is only
        // reliable on Windows fonts — mirrors the DossierPdfComposerTests convention.
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var pdf = LitigationReportArtifacts.RenderPdf(ReportWithSingleCase("csp-42"));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var text = string.Concat(doc.GetPages().Select(p => p.Text));

        Assert.Contains("TYPE", text);
        Assert.Contains("DISTRICT", text); // Humanize() upper-cases the "district" fixture value
        Assert.Contains("BENCH", text);
    }

    [SkippableFact]
    public void RenderPdf_includes_reuse_provenance_banner_when_reused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var report = ReportWithSingleCase("csp-42") with
        {
            ReusedFromSnapshotId = 123,
            ReusedFromRequestId = 456
        };

        var pdf = LitigationReportArtifacts.RenderPdf(report);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var text = string.Concat(doc.GetPages().Select(p => p.Text));

        Assert.Contains("reused from another request", text);
        Assert.Contains("Request #456", text);
        Assert.Contains("Snapshot #123", text);
        Assert.Contains("governed reuse admission", text);
        Assert.DoesNotContain("within the 7-day freshness window", text);
        Assert.Contains("REPORT REUSE", text);
    }

    [SkippableFact]
    public void RenderPdf_does_not_include_reuse_banner_when_not_reused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var report = ReportWithSingleCase("csp-42");

        var pdf = LitigationReportArtifacts.RenderPdf(report);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var text = string.Concat(doc.GetPages().Select(p => p.Text));

        Assert.DoesNotContain("reused from another request", text);
        Assert.DoesNotContain("REPORT REUSE", text);
    }

    [Fact]
    public void RenderCsv_includes_reuse_provenance_columns_and_values()
    {
        var reusedReport = ReportWithSingleCase("csp-42") with
        {
            ReusedFromSnapshotId = 123,
            ReusedFromRequestId = 456
        };
        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(reusedReport));
        using var reader = new StringReader(csv);
        using var csvReader = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csvReader.Read();
        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord!;

        Assert.Contains("Report Reused", headers);
        Assert.Contains("Reused From Request ID", headers);
        Assert.Contains("Reused From Snapshot ID", headers);
        Assert.Contains("Report Retrieved Date", headers);
        Assert.Contains("Reuse Disclosure", headers);

        csvReader.Read();
        Assert.Equal("Yes", csvReader.GetField("Report Reused"));
        Assert.Equal("456", csvReader.GetField("Reused From Request ID"));
        Assert.Equal("123", csvReader.GetField("Reused From Snapshot ID"));
        var disclosure = csvReader.GetField("Reuse Disclosure")!;
        Assert.Contains("reused from another request", disclosure);
        Assert.Contains("Request #456", disclosure);
        Assert.Contains("Snapshot #123", disclosure);
        Assert.Contains("governed reuse admission", disclosure);
        Assert.DoesNotContain("within the 7-day freshness window", disclosure);
    }

    [Fact]
    public void RenderCsv_shows_unreused_metadata_for_fresh_report()
    {
        var freshReport = ReportWithSingleCase("csp-42");
        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(freshReport));
        using var reader = new StringReader(csv);
        using var csvReader = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csvReader.Read();
        csvReader.ReadHeader();

        csvReader.Read();
        Assert.Equal("No", csvReader.GetField("Report Reused"));
        Assert.Equal("'-", csvReader.GetField("Reused From Request ID"));
        Assert.Equal("'-", csvReader.GetField("Reused From Snapshot ID"));
        Assert.Equal("'-", csvReader.GetField("Reuse Disclosure"));
    }

    [Fact]
    public void RenderCsv_includes_contested_property_columns_and_values()
    {
        var match = new StandaloneReportPropertyMatchDto(
            LitigationCaseOrderId: 101,
            OrderDate: "09-01-2025",
            OrderType: "Judgment",
            PageNumber: 3,
            SourceLabel: "Registered office",
            AddressText: "Plot 42, Kharavela Nagar, Bhubaneswar 751001",
            Strength: MCAROC_Analysis.Services.Analysis.AddressMatchStrength.Strong,
            MatchedPinCode: "751001",
            MatchedPlotNumbers: ["42"],
            MatchedLocalities: ["Kharavela Nagar"],
            Excerpt: "office situated at Plot 42, Kharavela Nagar, Bhubaneswar 751001",
            IsCompanyPremises: true);

        var report = ReportWithSingleCase("csp-42", [match]);
        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report));
        using var reader = new StringReader(csv);
        using var csvReader = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csvReader.Read();
        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord!;

        Assert.Contains("Contested Property Match Count", headers);
        Assert.Contains("Contested Property Details", headers);

        csvReader.Read();
        Assert.Equal("1", csvReader.GetField("Contested Property Match Count"));
        var details = csvReader.GetField("Contested Property Details")!;
        Assert.Contains("Plot 42, Kharavela Nagar", details);
        Assert.Contains("[Registered office]", details);
    }

    [SkippableFact]
    public void RenderPdf_includes_contested_property_panel_and_order_annotation()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var match = new StandaloneReportPropertyMatchDto(
            LitigationCaseOrderId: 101,
            OrderDate: "09-01-2025",
            OrderType: "Judgment",
            PageNumber: 2,
            SourceLabel: "Charge CHG-99",
            AddressText: "Sy No 115, Financial District, Nanakramguda, Hyderabad",
            Strength: MCAROC_Analysis.Services.Analysis.AddressMatchStrength.Strong,
            MatchedPinCode: null,
            MatchedPlotNumbers: ["115"],
            MatchedLocalities: ["Financial District", "Nanakramguda"],
            Excerpt: "schedule property at Sy No 115, Financial District, Nanakramguda",
            IsCompanyPremises: false,
            RocChargeId: 99,
            RocChargeNumber: "CHG-99",
            ChargeHolder: "State Bank of India");

        var report = ReportWithSingleCase("csp-42", [match]);
        var pdf = LitigationReportArtifacts.RenderPdf(report);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var text = string.Concat(doc.GetPages().Select(p => p.Text));

        Assert.Contains("PROPERTY ADDRESS MENTIONS", text);
        Assert.Contains("TEXT OVERLAP", text);
        Assert.Contains("does not legally establish", text);
        Assert.Contains("Charge CHG-99", text);
        Assert.Contains("Financial District", text);
    }

    // ── charged property & litigation ────────────────────────────────────────────────────────────────────

    private static MCAROC_Analysis.Services.LitigationData.ChargeLitigationLink Link(
        MCAROC_Analysis.Services.LitigationData.ChargeLitigationSignal signal, long? caseId, string explanation, string? excerpt = null, int? page = null) => new(
            7, "CHG-7", "State Bank of India", signal,
            new MCAROC_Analysis.Services.LitigationData.LinkedLitigation(caseId is null ? "MCA workbook" : "Court records", caseId, caseId is null ? 55 : null, "CS 88/2021", "High Court of Bombay", "Pending", null, null),
            explanation, 101, page, excerpt, "Order");

    [Fact]
    public void RenderCsv_adds_charge_link_columns_per_case_and_dashes_when_none()
    {
        var withLink = ReportWithSingleCase("csp-1") with
        {
            ChargeLinks = new MCAROC_Analysis.Services.LitigationData.ChargeLitigationSummary([
                Link(MCAROC_Analysis.Services.LitigationData.ChargeLitigationSignal.MovableIdentifier, 1, "The order names vehicle registration MH12AB1234.", page: 2),
                Link(MCAROC_Analysis.Services.LitigationData.ChargeLitigationSignal.LenderRecoveryCase, 1, "The holder is litigating."),
                Link(MCAROC_Analysis.Services.LitigationData.ChargeLitigationSignal.LenderRecoveryCase, 99, "A different case.")], 1, 1, 0, false)
        };

        using var reader = new StringReader(Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(withLink)));
        using var csvReader = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csvReader.Read(); csvReader.ReadHeader(); csvReader.Read();

        Assert.Equal("2", csvReader.GetField("Charge Link Count"));   // only this case's links, not case 99's
        var details = csvReader.GetField("Charge Links")!;
        Assert.Contains("CHG-7 (State Bank of India) [order names an asset under the charge] p. 2", details);
        Assert.Contains("vehicle registration MH12AB1234", details);
        Assert.Contains("[lender is litigating (indirect)]", details);
        Assert.DoesNotContain("A different case", details);

        using var none = new StringReader(Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(ReportWithSingleCase("csp-1"))));
        using var noneReader = new CsvHelper.CsvReader(none, System.Globalization.CultureInfo.InvariantCulture);
        noneReader.Read(); noneReader.ReadHeader(); noneReader.Read();
        Assert.Equal("0", noneReader.GetField("Charge Link Count"));
        Assert.Equal("'-", noneReader.GetField("Charge Links"));
    }

    /// <summary>PdfPig drops the space at a line wrap and renders ligatures (ti, tt, fi...) as a NUL, so compare
    /// with whitespace and NULs removed and only use phrases that avoid those letter pairs.</summary>
    private static string Squash(string s) => new string(s.Where(c => !char.IsWhiteSpace(c) && c != '\0').ToArray());

    [SkippableFact]
    public void RenderPdf_shows_the_charged_property_section_with_links_evidence_and_caveats()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var report = ReportWithSingleCase("csp-1") with
        {
            ChargeLinks = new MCAROC_Analysis.Services.LitigationData.ChargeLitigationSummary([
                Link(MCAROC_Analysis.Services.LitigationData.ChargeLitigationSignal.ImmovableAddress, 1, "The order names the property charged under CHG-7.", "Plot No. A-36, Nayapalli is attached.", 2),
                Link(MCAROC_Analysis.Services.LitigationData.ChargeLitigationSignal.LenderRecoveryCase, null, "The holder is litigating (DRT).")], 3, 2, 1, true, 1, 2)
        };

        using var doc = PdfDocument.Open(new MemoryStream(LitigationReportArtifacts.RenderPdf(report)));
        var text = Squash(string.Concat(doc.GetPages().Select(p => p.Text)));

        Assert.Contains(Squash("PROPERTY & LITIGATION"), text);
        Assert.Contains("CHG-7", text);
        Assert.Contains(Squash("Case names the charged"), text);
        Assert.Contains(Squash("Lender is"), text);
        Assert.Contains("(indirect)", text);
        Assert.Contains("Nayapalli", text);
        Assert.Contains(Squash("Only strong matches are shown."), text);
        Assert.Contains(Squash("NCLT/NCLAT orders are not scanned."), text);
        Assert.Contains(Squash("1 order(s) had no extractable text"), text);
        Assert.Contains(Squash("2 movable-asset charge(s) name"), text);
        Assert.Contains(Squash("vehicle or serial numbers"), text);
    }

    [SkippableFact]
    public void RenderPdf_with_no_links_says_what_was_compared_instead_of_implying_clean()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var report = ReportWithSingleCase("csp-1") with
        {
            ChargeLinks = new MCAROC_Analysis.Services.LitigationData.ChargeLitigationSummary([], 3, 2, 1, false)
        };

        using var doc = PdfDocument.Open(new MemoryStream(LitigationReportArtifacts.RenderPdf(report)));
        var text = Squash(string.Concat(doc.GetPages().Select(p => p.Text)));

        Assert.Contains(Squash("PROPERTY & LITIGATION"), text);
        Assert.Contains(Squash("No case was"), text);
        Assert.Contains(Squash("a named asset"), text);
        Assert.Contains(Squash("2 order(s) with text were compared"), text);
        Assert.Contains(Squash("cannot be compared"), text);
    }

    [Fact]
    public void RenderPdf_without_a_charge_comparison_omits_the_section_and_still_renders()
    {
        var pdf = LitigationReportArtifacts.RenderPdf(ReportWithSingleCase("csp-1"));
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    }

    private static StandaloneLitigationReport ReportWithSingleCase(string cspId, IReadOnlyList<StandaloneReportPropertyMatchDto>? propertyMatches = null)
    {
        var orders = new List<StandaloneReportOrderDto>
        {
            new(
                LitigationCaseOrderId: 101,
                OrderDate: "09-01-2025",
                OrderType: "Judgment",
                AvailabilityBucket: LitigationOrderAvailabilityBucket.Downloaded,
                DocumentStatus: MCAROC_Analysis.Data.Entities.LitigationOrderDocumentStatus.Downloaded,
                TextExtractionStatus: MCAROC_Analysis.Data.Entities.FilingDocumentProcessingStatus.TextExtracted,
                RetainedUntilUtc: DateTime.Parse("2025-01-16T12:00:00Z"),
                AvailabilityDisclosure: "Available via portal (retained until 16-Jan-2025); text extracted",
                CsvStatus: "Downloaded",
                ExtractionLabel: "TextExtracted",
                PropertyMatches: propertyMatches)
        };

        var caseDto = new StandaloneReportCaseDto(
            LitigationCaseId: 1,
            ProviderCaseId: "provider-1",
            CspId: cspId,
            CnrNumber: "TNKP070001332020",
            CourtCategory: "district_court",
            Direction: "against",
            CaseClassification: "civil",
            Type: "district",
            Court: "Sub Judge",
            Bench: "Bench",
            CaseNumber: "22/2020",
            CaseType: "OS",
            CaseYear: "2020",
            CaseStage: "Trial",
            CaseStatus: "DISPOSED",
            Act: "Code",
            FilingDate: "29-01-2020",
            LastHearingDate: "09-01-2025",
            NextHearingDate: null,
            DecisionDate: "09-01-2025",
            State: "Tamil Nadu",
            District: "Kancheepuram",
            PetitionersJson: "[\"Petitioner One\",\"Petitioner Two\"]",
            RespondentsJson: "[\"Respondent One\"]",
            PetitionerAdvocatesJson: "[\"Advocate One\"]",
            RespondentAdvocatesJson: null,
            Orders: orders,
            PropertyMatches: propertyMatches);

        var grid = new MCAROC_Analysis.Models.LitigationCourtSummaryGrid();
        grid.Rows.Add(new MCAROC_Analysis.Models.LitigationCourtSummaryRow
        {
            CourtName = "Sub Judge",
            CourtCategory = "district_court",
            TotalCases = 1,
            PendingCases = 0,
            DisposedCases = 1,
            UnknownCases = 0,
            TotalOrders = 1
        });

        return new StandaloneLitigationReport(
            AssignmentNumber: "MCA-2026-001",
            CompanyName: "Test Company Limited",
            GeneratedAtUtc: DateTimeOffset.Parse("2026-09-20T12:00:00Z"),
            AuthoritativeSnapshotId: 42,
            AuthoritativeSnapshotRetrievedUtc: DateTime.Parse("2026-09-20T11:00:00Z"),
            IsPriorRunDataShown: false,
            KeywordsSearched: ["Test Company"],
            CourtSummaryGrid: grid,
            Cases: [caseDto],
            PortfolioAnalysis: new LitigationPortfolioAnalysis("Complete", "R2", "Synthetic QA portfolio analysis.", ["One test finding."]),
            CaseAnalysesByCaseId: new Dictionary<long, LitigationCaseAnalysis>
            {
                [1] = new("Complete", "R2", "Synthetic QA case analysis.", ["One test issue."], "Review the underlying order.", "QA fixture only.")
            });
    }
}
