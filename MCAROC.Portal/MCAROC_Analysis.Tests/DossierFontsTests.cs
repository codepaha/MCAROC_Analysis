using MCAROC_Analysis.Services.Dossier;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests;

/// <summary>Proves the bundled Fraunces / IBM Plex fonts register with QuestPDF and actually render on
/// this machine (SkiaSharp native path included) — the risky part of wiring a new PDF engine.</summary>
public class DossierFontsTests
{
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    [Fact]
    public void All_three_families_register_and_render()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        DossierFonts.Register(WebRoot());

        var bytes = Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.Content().Column(col =>
                {
                    col.Item().Text("Kaveri Infra Projects Limited").FontFamily(DossierTheme.Display).FontSize(24);
                    col.Item().Text("Due diligence dossier body text.").FontFamily(DossierTheme.Sans).FontSize(10);
                    col.Item().Text("L45201KA1998PLC987654  ₹-42.8 Cr").FontFamily(DossierTheme.Mono).FontSize(10);
                });
            });
        }).GeneratePdf();

        Assert.NotEmpty(bytes);
        using var pdf = PdfDocument.Open(new MemoryStream(bytes));
        var text = string.Concat(pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Kaveri Infra Projects Limited", text);
        Assert.Contains("L45201KA1998PLC987654", text);
        Assert.Contains("₹", text); // ₹ survived the mono font
    }
}
