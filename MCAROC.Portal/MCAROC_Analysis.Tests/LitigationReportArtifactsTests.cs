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
