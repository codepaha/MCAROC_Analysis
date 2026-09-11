using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

public class PrrReportTemplateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Prr_preserves_reference_layout_and_replaces_all_sample_records(int count)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "MCAROC.Portal", "MCAROC_Analysis")))
            directory = directory.Parent;
        var root = Path.Combine(directory!.FullName, "MCAROC.Portal", "MCAROC_Analysis");
        using var http = new HttpClient();
        var service = new PreLoginReportService(new InstaFinancialsClient(http, Options.Create(new InstaFinancialsOptions())),
            new TestEnvironment(root));
        var company = new InstaCompany("Replacement Company", "ROC Mumbai", "654321", "Company limited by shares",
            "Non-government company", "Private", "200,000", "100,000", "0", "01-01-2020", "Test address",
            "test@example.com", "Unlisted", "01-01-2026", "31-03-2026", "Active");
        var charges = Enumerable.Range(1, count).Select(i => new InstaCharge($"CHARGE-{i}", $"Bank {i}",
            "01-01-2020", "-", "-", "1,000", i % 2 == 1)).ToArray();
        var directors = Enumerable.Range(1, count).Select(i => new InstaDirector($"Director {i}", $"DIN-{i}",
            "Director", "01-01-2020")).ToArray();
        var result = await service.GenerateFromDataAsync("U12345MH2020PTC654321", PreLoginReportFormat.Prr,
            new InstaReportData(company, charges, directors), CancellationToken.None);

        using var doc = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        using var reference = WordprocessingDocument.Open(Path.Combine(root, "ReportTemplates", "PRR", "prr-template.docx"), false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var tables = body.Elements<Table>().ToArray();
        Assert.Equal(5, tables.Length);
        Assert.Equal(12240U, body.Elements<SectionProperties>().Single().GetFirstChild<PageSize>()!.Width!.Value);
        Assert.Equal(20160U, body.Elements<SectionProperties>().Single().GetFirstChild<PageSize>()!.Height!.Value);
        var referenceTables = reference.MainDocumentPart!.Document.Body!.Elements<Table>().ToArray();
        for (var i = 0; i < tables.Length; i++)
        {
            Assert.Equal(referenceTables[i].GetFirstChild<TableGrid>()!.OuterXml, tables[i].GetFirstChild<TableGrid>()!.OuterXml);
            Assert.Equal("9800", tables[i].GetFirstChild<TableProperties>()!.GetFirstChild<TableWidth>()!.Width!.Value);
        }
        Assert.Equal(reference.MainDocumentPart.HeaderParts.Single().Header.OuterXml, doc.MainDocumentPart.HeaderParts.Single().Header.OuterXml);
        Assert.Equal(reference.MainDocumentPart.FooterParts.Single().Footer.OuterXml, doc.MainDocumentPart.FooterParts.Single().Footer.OuterXml);
        Assert.Equal(Math.Max(1, count) + 1, tables[3].Elements<TableRow>().Count());
        Assert.Equal(Math.Max(1, count) + 1, tables[4].Elements<TableRow>().Count());
        Assert.Equal("", tables[0].Elements<TableRow>().First().Elements<TableCell>().Last().InnerText);
        Assert.Equal("", tables[0].Elements<TableRow>().ElementAt(1).Elements<TableCell>().Last().InnerText);
        Assert.Equal(((count + 1) / 2 * 1000).ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            tables[2].Elements<TableRow>().Last().Elements<TableCell>().Last().InnerText);
        var text = body.InnerText;
        foreach (var stale in new[] { "UPPAL", "ASN_No_37312", "FI_CODE_37280", "101299498", "Mahinder", "Okesh", "07/09/2026", "07-09-2026" })
            Assert.DoesNotContain(stale, text);
        Assert.Contains("Replacement Company", text);
        Assert.Contains($"Total Directors / Signatories: {count}", text);
        if (count == 0)
        {
            Assert.Contains("No charges found.", text);
            Assert.Contains("No director records found.", text);
        }
        else
        {
            Assert.Contains($"CHARGE-{count}", text);
            Assert.Contains($"DIN-{count}", text);
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PrrTemplateTests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
