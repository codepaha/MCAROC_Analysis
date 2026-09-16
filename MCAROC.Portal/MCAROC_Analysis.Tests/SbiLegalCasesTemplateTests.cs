using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WP = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>The "Details of legal cases" section (#221) renders one bordered card per case (Sr No/Court,
/// Case No/Case Type, Case Year/Act, Date of Filing/Case Stage, State/District, Case Details, LDOH/NDOH,
/// Status), so a case with 40-50 parties has room in its full-width Case Details cell without truncation.
/// CNR No was dropped after reviewing a sample against the reference card — it pushed the card to 9 field
/// rows without earning its place.</summary>
public class SbiLegalCasesTemplateTests
{
    private static PreLoginReportService CreateService()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "MCAROC.Portal", "MCAROC_Analysis")))
            directory = directory.Parent;
        var root = Path.Combine(directory!.FullName, "MCAROC.Portal", "MCAROC_Analysis");
        return new PreLoginReportService(new InstaFinancialsClient(new HttpClient(), Options.Create(new InstaFinancialsOptions())), new TestEnvironment(root));
    }

    private static InstaCompany Company(bool isPartnership = false) => new(
        "Example Private Limited", "ROC Mumbai", "654321", "Company limited by shares", "Non-government company",
        "Private", "200,000", "100,000", "0", "01-01-2020", "Test address", "test@example.com", "Unlisted",
        "01-01-2026", "31-03-2026", "Active", IsPartnership: isPartnership);

    private static LegalCaseRecord Case(string category = "district", string court = "Civil Judge, Rohtak",
        string cnrNumber = "CNR1", string caseNo = "EXE/138/2026", string caseType = "EXE", string caseYear = "2026",
        string caseStage = "Appearance", string act = "Code of Civil Procedure/1", string dateOfFiling = "-",
        string state = "Haryana", string district = "Rohtak", string caseDetails = "SA Enterprises VS Northlay Foods Private Limited",
        string dateOfHearing = "15-09-2026", string status = "PENDING") =>
        new(category, court, cnrNumber, caseNo, caseType, caseYear, caseStage, act, dateOfFiling, state, district, caseDetails, dateOfHearing, status);

    [Fact]
    public async Task No_cases_leaves_the_placeholder_card_and_stays_portrait()
    {
        var service = CreateService();
        var result = await service.GenerateFromDataAsync("U12345MH2020PTC654321", PreLoginReportFormat.Sbi,
            new InstaReportData(Company(), [], []), CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        Assert.Equal(2, body.Descendants<SectionProperties>().Count()); // final disclaimer section reserves letterhead space
        Assert.Contains("No litigation cases on file.", body.InnerText);
    }

    [Fact]
    public async Task Disclaimer_starts_in_a_dedicated_section_below_the_default_letterhead()
    {
        var service = CreateService();
        var result = await service.GenerateFromDataAsync("U12345MH2020PTC654321", PreLoginReportFormat.Sbi,
            new InstaReportData(Company(), [], []), CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var paragraphs = body.Elements<Paragraph>().ToList();
        var disclaimerIndex = paragraphs.FindIndex(p => p.InnerText == "Disclaimer");
        Assert.True(disclaimerIndex > 0);

        var disclaimer = paragraphs[disclaimerIndex];
        Assert.Null(disclaimer.ParagraphProperties?.PageBreakBefore);
        Assert.Null(disclaimer.ParagraphProperties?.SpacingBetweenLines?.Before);
        Assert.True(string.IsNullOrWhiteSpace(paragraphs[disclaimerIndex - 1].InnerText));

        var firstSection = paragraphs[disclaimerIndex - 1].ParagraphProperties?.GetFirstChild<SectionProperties>();
        Assert.Equal(SectionMarkValues.NextPage, firstSection?.GetFirstChild<SectionType>()?.Val?.Value);
        Assert.NotNull(paragraphs[disclaimerIndex - 1].ParagraphProperties?.GetFirstChild<KeepNext>());

        var disclaimerSection = body.Elements<SectionProperties>().Single();
        Assert.Null(disclaimerSection.GetFirstChild<TitlePage>());
        Assert.Equal(1440, disclaimerSection.GetFirstChild<PageMargin>()?.Top?.Value);

        var addressBox = body.Descendants<WP.Anchor>().Single(a =>
            a.GetFirstChild<WP.DocProperties>()?.Name?.Value == "Text Box 2");
        var addressPosition = addressBox.GetFirstChild<WP.VerticalPosition>();
        Assert.Equal(WP.VerticalRelativePositionValues.Page, addressPosition?.RelativeFrom?.Value);
        Assert.Equal("5850000", addressPosition?.GetFirstChild<WP.PositionOffset>()?.Text);
    }

    [Fact]
    public async Task Cases_render_one_card_each_with_every_field_and_no_truncation()
    {
        var service = CreateService();
        var manyParties = string.Join(", ", Enumerable.Range(1, 45).Select(i => $"Respondent Party {i} Pvt Ltd"));
        var cases = new[]
        {
            Case(),
            Case(category: "highcourt", court: "Hyderabad High Court", cnrNumber: "CNR2", caseNo: "1704/2021",
                caseType: "WP", caseYear: "2021", caseStage: "Admission", act: "Constitution of India 226",
                state: "Telangana", district: "Hyderabad", caseDetails: $"Mr. Surendra VS {manyParties}",
                dateOfHearing: "04-12-2024", status: "PENDING"),
        };
        var data = new InstaReportData(Company(), [], [], LegalCaseFileParser.ToInstaLegalCases(cases));

        var result = await service.GenerateFromDataAsync("U12345MH2020PTC654321", PreLoginReportFormat.Sbi, data, CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        Assert.Equal(2, body.Descendants<SectionProperties>().Count());

        var text = body.InnerText;
        Assert.Contains("Sr No : 1", text);
        Assert.Contains("Court : Civil Judge, Rohtak", text);
        Assert.DoesNotContain("CNR No", text);
        Assert.Contains("Case no : EXE/138/2026", text);
        Assert.Contains("Case Type : EXE", text);
        Assert.Contains("Case Year : 2026", text);
        Assert.Contains("Case Stage : Appearance", text);
        Assert.Contains("Act : Code of Civil Procedure/1", text);
        Assert.Contains("State : Haryana", text);
        Assert.Contains("District : Rohtak", text);
        Assert.Contains("Case details : SA Enterprises VS Northlay Foods Private Limited", text);
        Assert.Contains("Last Date of Hearing : -", text);
        Assert.Contains("Next Date of Hearing : 15-09-2026", text);
        Assert.Contains("Status : PENDING", text);
        Assert.Contains("Sr No : 2", text);
        // The 45-party second case must render every party in its Case Details cell, not a truncated subset.
        Assert.Contains("Respondent Party 1 Pvt Ltd", text);
        Assert.Contains("Respondent Party 45 Pvt Ltd", text);
        Assert.DoesNotContain("No litigation cases on file.", text);

        var summary = body.Descendants<Table>().First(t => t.Elements<TableRow>().Skip(1).FirstOrDefault()?.Elements<TableCell>().Count() == 9);
        var counts = summary.Elements<TableRow>().ElementAt(1).Elements<TableCell>().Select(c => c.InnerText).ToArray();
        Assert.Equal(["0", "1", "1", "0", "0", "0", "0", "0", "0"], counts);
    }

    [Fact]
    public async Task Partnership_identity_relabeling_is_unaffected_by_whether_legal_cases_are_attached()
    {
        var service = CreateService();
        var cases = new[] { Case() };
        var data = new InstaReportData(Company(isPartnership: true), [], [], LegalCaseFileParser.ToInstaLegalCases(cases));

        var result = await service.GenerateFromDataAsync("REG123", PreLoginReportFormat.Sbi, data, CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("PAN / Registration Number", text);
        Assert.Contains("Partnership Name", text);
    }

    [Fact]
    public async Task Company_with_legal_cases_attached_keeps_the_cin_label_unrelabeled()
    {
        // Regression guard for the bug this feature exposed: LegalCases presence must never drive the
        // Partnership relabeling — that must come from Company.IsPartnership alone (#221).
        var service = CreateService();
        var cases = new[] { Case() };
        var data = new InstaReportData(Company(isPartnership: false), [], [], LegalCaseFileParser.ToInstaLegalCases(cases));

        var result = await service.GenerateFromDataAsync("U12345MH2020PTC654321", PreLoginReportFormat.Sbi, data, CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.DoesNotContain("PAN / Registration Number", text);
        Assert.DoesNotContain("Partnership Name", text);
        Assert.Contains("CIN", text);
    }

    [Fact]
    public async Task Charges_use_blue_zebra_while_case_cards_use_label_value_rows_and_stay_together()
    {
        var charges = new[]
        {
            new InstaCharge("STYLE-CHG-1", "First Bank", "01-01-2025", "-", "-", "100", true, "STYLE-SRN-1"),
            new InstaCharge("STYLE-CHG-2", "Second Bank", "02-01-2025", "-", "-", "200", true, "STYLE-SRN-2")
        };
        var cases = new[] { Case(), Case(category: "highcourt", court: "Bombay High Court") };
        var data = new InstaReportData(Company(), charges, [], LegalCaseFileParser.ToInstaLegalCases(cases));

        var result = await CreateService().GenerateFromDataAsync(
            "U12345MH2020PTC654321", PreLoginReportFormat.Sbi, data, CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var chargesTable = body.Elements<Table>().First(t => t.InnerText.Contains("STYLE-SRN-1", StringComparison.Ordinal));
        var chargeRows = chargesTable.Elements<TableRow>().ToList();
        Assert.Equal("D9EAF7", chargeRows[0].Elements<TableCell>().First().TableCellProperties?.Shading?.Fill?.Value);
        Assert.Equal("FFFFFF", chargeRows[1].Elements<TableCell>().First().TableCellProperties?.Shading?.Fill?.Value);
        Assert.Equal("EDF4FB", chargeRows[2].Elements<TableCell>().First().TableCellProperties?.Shading?.Fill?.Value);
        Assert.Equal(BorderValues.Nil, chargesTable.TableProperties?.TableBorders?.InsideVerticalBorder?.Val?.Value);

        var cards = body.Elements<Table>().First(t => t.InnerText.Contains("Sr No : 1", StringComparison.Ordinal));
        var cardRows = cards.Elements<TableRow>().ToList();
        Assert.Equal(30, cardRows.Count);
        Assert.Equal(BorderValues.Nil, cards.TableProperties?.TableBorders?.InsideHorizontalBorder?.Val?.Value);
        Assert.Equal(BorderValues.Nil, cards.TableProperties?.TableBorders?.InsideVerticalBorder?.Val?.Value);
        Assert.Equal(2, cardRows[0].Elements<TableCell>().Count());
        Assert.Equal("Court : ", cardRows[0].Elements<TableCell>().First().InnerText);
        Assert.Equal("Civil Judge, Rohtak", cardRows[0].Elements<TableCell>().Last().InnerText);
        Assert.Equal(BorderValues.Single, cardRows[0].Elements<TableCell>().First().TableCellProperties?.TableCellBorders?.TopBorder?.Val?.Value);
        Assert.Equal(BorderValues.Single, cardRows[13].Elements<TableCell>().First().TableCellProperties?.TableCellBorders?.BottomBorder?.Val?.Value);
        Assert.NotNull(cardRows[0].Descendants<KeepNext>().FirstOrDefault());
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SbiLegalCasesTemplateTests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
