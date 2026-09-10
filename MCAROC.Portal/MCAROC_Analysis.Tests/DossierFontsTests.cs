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

    private static byte[] RenderSample()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        DossierFonts.Register(WebRoot());

        return Document.Create(doc =>
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
    }

    /// <summary>Cross-platform: the SkiaSharp native render path loads and the bundled fonts produce a
    /// non-empty PDF. Runs on the Linux CI job (proves libfontconfig + the font registration work).</summary>
    [Fact]
    public void The_bundled_fonts_render_without_a_native_failure()
    {
        var bytes = RenderSample();

        Assert.NotEmpty(bytes);
        using var pdf = PdfDocument.Open(new MemoryStream(bytes));
        Assert.Equal(1, pdf.NumberOfPages);
    }

    /// <summary>Windows-only: asserts the rendered glyphs round-trip through PdfPig text extraction.
    /// SkiaSharp's font subsetting on Linux drops the ToUnicode data PdfPig needs for a few Fraunces
    /// glyphs (letters come back as NUL), so this assertion is only meaningful on the Windows job —
    /// the `windows-tests` CI job runs it there.</summary>
    [SkippableFact]
    public void All_three_families_register_and_render()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        var bytes = RenderSample();

        Assert.NotEmpty(bytes);
        using var pdf = PdfDocument.Open(new MemoryStream(bytes));
        var text = string.Concat(pdf.GetPages().Select(p => p.Text));
        Assert.Contains("Kaveri Infra Projects Limited", text);
        Assert.Contains("L45201KA1998PLC987654", text);
        Assert.Contains("₹", text); // ₹ survived the mono font
    }
}
