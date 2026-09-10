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

    [Theory]
    [InlineData(DossierVariant.Executive)]
    [InlineData(DossierVariant.FullSource)]
    [InlineData(DossierVariant.SourceRecord)]
    public async Task Renders_the_dossier_with_no_risk_score(DossierVariant variant)
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

        var text = string.Concat(doc.GetPages().Select(p => p.Text));
        Assert.Contains("DUE DILIGENCE DOSSIER", text);
        Assert.Contains("Golden Master Ltd", text);
        Assert.Contains("Contents", text);
        Assert.Contains("Snapshot", text);

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

        // Source-record variant drops Section 1 (the synthesised analysis) entirely.
        if (variant == DossierVariant.SourceRecord)
            Assert.DoesNotContain("Executive Summary", text);
        else
            Assert.Contains("Executive Summary", text);

        // The hard rule: no score, no gauge, no document index.
        var lower = text.ToLowerInvariant();
        Assert.DoesNotContain("risk score", lower);
        Assert.DoesNotContain("/100", lower);
        Assert.DoesNotContain("documents index", lower);
    }
}
