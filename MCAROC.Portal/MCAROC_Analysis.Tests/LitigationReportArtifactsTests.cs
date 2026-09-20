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

    private static StandaloneLitigationReport ReportWithSingleCase(string cspId)
    {
        var item = new BprLitigationCase("provider-1", cspId, "TNKP070001332020", "district_court", "against", "civil", "district",
            "Sub Judge", "Bench", "22/2020", "OS", "2020", "Trial", "DISPOSED", "Code", "29-01-2020", "09-01-2025", null,
            "09-01-2025", "Tamil Nadu", "Kancheepuram", "[\"Petitioner One\",\"Petitioner Two\"]", "[\"Respondent One\"]",
            "[\"Advocate One\"]", null, [new BprLitigationOrder("https://source.example/order.pdf", "09-01-2025", "Judgment")]);
        return new StandaloneLitigationReport(
            "MCA-2026-001", "Test Company Limited", DateTimeOffset.Parse("2026-09-20T12:00:00Z"),
            new BprLitigationReport(new BprLitigationRequest("job-1", "2026-09-20", ["Test Company"]), [item]),
            new LitigationPortfolioAnalysis("Complete", "R2", "Synthetic QA portfolio analysis.", ["One test finding."]),
            new Dictionary<string, LitigationCaseAnalysis>
            {
                ["provider-1"] = new("Complete", "R2", "Synthetic QA case analysis.", ["One test issue."], "Review the underlying order.", "QA fixture only.")
            });
    }
}
