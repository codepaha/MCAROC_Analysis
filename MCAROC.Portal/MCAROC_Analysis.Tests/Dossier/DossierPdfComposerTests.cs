using MCAROC_Analysis.Services.Dossier;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>Renders the dossier PDF for the seed graph and asserts the document shape — the sections are
/// present and ordered, and (the hard rule) there is no risk score anywhere.</summary>
public class DossierPdfComposerTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    private static string TextOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return string.Concat(doc.GetPages().Select(p => p.Text));
    }

    /// <summary>Cross-platform smoke: every variant renders to a non-empty multi-page PDF without a
    /// SkiaSharp native failure. The content assertions live in <see cref="Renders_the_dossier_with_no_risk_score"/>,
    /// which is Windows-only because PdfPig text extraction is unreliable against Linux subset fonts.</summary>
    [Theory]
    [InlineData(DossierVariant.Executive)]
    [InlineData(DossierVariant.FullSource)]
    [InlineData(DossierVariant.SourceRecord)]
    public async Task Every_variant_renders_a_multi_page_pdf(DossierVariant variant)
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, variant);

        Assert.NotEmpty(pdf);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.NumberOfPages >= 2);
    }

    [SkippableTheory]
    [InlineData(DossierVariant.Executive)]
    [InlineData(DossierVariant.FullSource)]
    [InlineData(DossierVariant.SourceRecord)]
    public async Task Renders_the_dossier_with_no_risk_score(DossierVariant variant)
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, variant);
        Assert.NotEmpty(pdf);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.NumberOfPages >= 2);

        var text = string.Concat(doc.GetPages().Select(p => p.Text));
        Assert.Contains("DUE DILIGENCE DOSSIER", text);
        Assert.Contains("Golden Master Ltd", text);
        Assert.Contains("Contents", text);
        Assert.Contains("Snapshot", text);

        // D12 — the "Not assessed" coverage block is deterministic and renders in EVERY variant,
        // including the no-AI SourceRecord, so "no flag" is never mistaken for "verified clean".
        Assert.Contains("Not assessed", text);
        Assert.Contains("leverage-trend checks were not run", text);

        // New attribution line (cover footer + "Prepared by").
        Assert.Contains("Gaba Projects Private Limited", text);

        if (variant == DossierVariant.Executive)
        {
            Assert.Contains("Annexure A", text);
            Assert.Contains("Annexure E", text);
            Assert.Contains("Executive Summary", text);
            Assert.DoesNotContain("Source Records", text);
        }
        else
        {
            // Full source / Source record render the verbatim Layer-0 rows, not the typed annexures.
            Assert.Contains("Source Records", text);
            Assert.Contains("Company master report", text);   // workbook label
            Assert.Contains("Secretary", text);               // a raw Directors-sheet cell value
            Assert.Contains("passu", text);                   // deep inside the long charge cell — rendered unclipped
            Assert.DoesNotContain("Directors register", text); // the typed Annexure-A table is Executive-only
        }

        // Source-record variant drops the synthesised Section 1 narrative entirely. It still opens with
        // the Snapshot (headline figures + the deterministic Review Priority) — that is the variant's
        // contract: "source record plus a factual cover page", not a zero-derivation dump.
        if (variant == DossierVariant.SourceRecord)
        {
            Assert.DoesNotContain("Executive Summary", text);
            Assert.Contains("Review priority", text, StringComparison.OrdinalIgnoreCase); // Snapshot tile retained
        }
        else
        {
            Assert.Contains("Executive Summary", text);
        }

        // The hard rule: no score, no gauge, no document index.
        var lower = text.ToLowerInvariant();
        Assert.DoesNotContain("risk score", lower);
        Assert.DoesNotContain("/100", lower);
        Assert.DoesNotContain("documents index", lower);
    }

    /// <summary>G18/#161: the Auditor's-comments Annexure B cell folds Section/Directors' Comments/
    /// Footnotes into the existing Comment column as inline text (deliberately no new dense-table
    /// columns — see the issue's plan for why). Real render-and-extract, not manual PDF inspection.</summary>
    [SkippableFact]
    public async Task Auditor_comment_cell_includes_section_directors_comments_and_footnote_inline()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);
        var text = TextOf(pdf);

        // PdfPig's raw glyph-concatenation extraction can drop a space at certain punctuation
        // boundaries (e.g. "700600(Disclosures)", "Footnote:See annexure") even though the rendered PDF
        // itself is correctly spaced — this file's own existing tests already work around the same
        // extraction quirk elsewhere. Assert on the pieces independently rather than one exact string.
        Assert.Contains("Section: 700600", text);
        Assert.Contains("Disclosures", text);
        Assert.Contains("Directors' comments: Self explanatory", text);
        Assert.Contains("Footnote:", text);
        Assert.Contains("See annexure", text);
    }
}
