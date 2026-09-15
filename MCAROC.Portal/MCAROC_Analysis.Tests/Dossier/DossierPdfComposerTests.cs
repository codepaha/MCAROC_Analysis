using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
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

    /// <summary>Smoke test: the dossier renders to a non-empty multi-page PDF without a SkiaSharp
    /// native failure. The content assertions live in <see cref="Renders_the_dossier_with_no_risk_score"/>,
    /// which is Windows-only because PdfPig text extraction is unreliable against Linux subset fonts.</summary>
    [Fact]
    public async Task Renders_a_multi_page_pdf()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);

        Assert.NotEmpty(pdf);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.NumberOfPages >= 2);
    }

    [SkippableFact]
    public async Task Renders_the_dossier_with_no_risk_score()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);
        Assert.NotEmpty(pdf);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.NumberOfPages >= 2);

        var text = string.Concat(doc.GetPages().Select(p => p.Text));
        Assert.Contains("DUE DILIGENCE DOSSIER", text);
        Assert.Contains("Golden Master Ltd", text);
        Assert.Contains("Contents", text);
        Assert.Contains("Snapshot", text);

        // D12 — the "Not assessed" coverage block is deterministic, so "no flag" is never mistaken for "verified clean".
        Assert.Contains("Not assessed", text);
        Assert.Contains("leverage-trend checks were not run", text);

        // New attribution line (cover footer + "Prepared by").
        Assert.Contains("Gaba Projects Private Limited", text);

        Assert.Contains("Annexure A", text);
        Assert.Contains("Annexure E", text);
        Assert.Contains("Executive Summary", text);

        // The hard rule: no score, no gauge, no document index.
        var lower = text.ToLowerInvariant();
        Assert.DoesNotContain("risk score", lower);
        Assert.DoesNotContain("/100", lower);
        Assert.DoesNotContain("documents index", lower);
    }

    /// <summary>#197: a cross-section finding (CrossSectionRules) lists its component finding codes in
    /// SupportingSignalsJson; the component must render nested as "Evidence" under the cross-section
    /// card, not also as its own separate flat sibling card — that duplication is exactly the "flag
    /// inflation" #197 was raised to fix. Overrides just ExecSummary on the real seeded model (via
    /// record `with`) rather than hand-building a full DossierModel.</summary>
    [SkippableFact]
    public async Task Cross_section_finding_nests_its_component_as_evidence_not_a_separate_card()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var component = new AnalysisFinding
        {
            AnalysisRunId = model!.AnalysisRunId ?? 0, RequestId = requestId,
            Section = FindingSection.Charges, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
            Code = "TEST_COMPONENT", Title = "Test Component Finding", SummaryText = "Component summary text.",
            DisplayPriority = 100, ObservationDate = new DateOnly(2026, 1, 1)
        };
        var parent = new AnalysisFinding
        {
            AnalysisRunId = model.AnalysisRunId ?? 0, RequestId = requestId,
            Section = FindingSection.CrossSection, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
            Code = "TEST_CROSS_SECTION", Title = "Test Cross-Section Finding", SummaryText = "Parent summary text.",
            DisplayPriority = 101, ObservationDate = new DateOnly(2026, 1, 1),
            SupportingSignalsJson = JsonSerializer.Serialize(new[] { "TEST_COMPONENT" })
        };
        var testModel = model with
        {
            ExecSummary = model.ExecSummary with { FindingsInDisplayOrder = [parent, component] }
        };

        var pdf = new DossierPdfRenderer(WebRoot()).Render(testModel, DossierVariant.Executive);
        var text = TextOf(pdf);

        Assert.Contains("Test Cross-Section Finding", text);
        Assert.Contains("Evidence", text);
        Assert.Contains("Test Component Finding", text);
        Assert.Contains("Component summary text.", text);
        Assert.Contains("Annexure C", text); // the component's own section cited on its evidence line

        // The whole point: the component appears exactly once (nested), never as a second, separate card.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(text, "Test Component Finding").Count;
        Assert.Equal(1, occurrences);
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
