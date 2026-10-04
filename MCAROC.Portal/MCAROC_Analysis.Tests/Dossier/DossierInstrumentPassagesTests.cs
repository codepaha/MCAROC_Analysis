using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MCAROC_Analysis.Services.CalculationAssurance;
using UglyToad.PdfPig;
using Xunit;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>#364: the dossier's charged-property annexure shows the property passages quoted from each charge's filed documents,
/// with the page they are on and the rules' reading of them, and a dossier cached before those quotes existed is never served after.</summary>
public class DossierInstrumentPassagesTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    /// <summary>PdfPig drops the space at a line wrap and renders ligatures as a NUL: compare squashed.</summary>
    private static string Squash(string s) => new string(s.Where(c => !char.IsWhiteSpace(c) && c != '\0').ToArray());

    private static string TextOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Squash(string.Concat(doc.GetPages().Select(p => p.Text)));
    }

    private static ChargeInstrumentPassageSet Set(params InstrumentPassage[] passages) =>
        new(901, "Memorandum of entry.pdf", "CHG-1", new ChargeInstrumentAiResult(passages));

    [SkippableFact]
    public async Task The_annexure_prints_the_quoted_passages_with_their_page_and_reading_under_the_charge()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = (await new DossierAssembler(db).BuildAsync(requestId))!;
        var charge = model.Charges.Open.First();
        var key = ChargeDocumentLinker.Normalize(charge.RocChargeNumber);

        var withPassages = model with
        {
            InstrumentPassagesByCharge = new Dictionary<string, IReadOnlyList<ChargeInstrumentPassageSet>>
            {
                [key] = [Set(new InstrumentPassage(InstrumentPassageKind.Schedule,
                    "All that piece of land bearing Survey No. 12/3, Village Baner, Taluka Haveli, District Pune", 14, 14))]
            }
        };
        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(withPassages, DossierVariant.Executive));

        Assert.Contains(Squash("Property as described in the"), text);   // (the "fi" of "filed" is a ligature PdfPig cannot read)
        Assert.Contains(Squash("ed documents"), text);
        Assert.Contains(Squash("Survey No. 12/3, Village Baner"), text);       // the quote, whole identifier
        Assert.Contains(Squash("CHG-1: Memorandum of entry.pdf, page 14"), text);
        Assert.Contains(Squash("Read as:"), text);                              // the rules' reading of the passage
    }

    [SkippableFact]
    public async Task A_charge_without_passages_prints_no_such_section()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = (await new DossierAssembler(db).BuildAsync(requestId))!;

        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(model, DossierVariant.Executive));

        Assert.DoesNotContain(Squash("Property as described in the"), text);
    }

    [Fact]
    public async Task A_passage_extraction_completing_after_a_dossier_was_cached_regenerates_the_model()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        var batch = new McaFilingBatch { RequestId = requestId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        seed.McaFilingBatches.Add(batch);
        await seed.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = requestId, Srn = "SRN-" + Guid.NewGuid().ToString("N")[..6], NestedZipName = "n.zip" };
        seed.McaFilings.Add(filing);
        await seed.SaveChangesAsync();
        var document = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = requestId, OriginalFileName = "Deed.pdf", StoragePath = "gone.pdf",
            FileHash = Guid.NewGuid().ToString("N"), ProcessingStatus = FilingDocumentProcessingStatus.Completed, Category = FilingCategory.Charge, UpdatedAt = DateTime.UtcNow
        };
        seed.McaFilingDocuments.Add(document);
        await seed.SaveChangesAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var cache = new DossierCache(db, new DossierAssembler(db), memory);
        var first = await cache.GetAsync(requestId);
        Assert.NotNull(first);
        Assert.Same(first, await cache.GetAsync(requestId));                 // unchanged: served from the cache

        await using (var writer = DossierGoldenMasterTests.CreateContext())
        {
            writer.ChargeInstrumentExtractions.Add(new ChargeInstrumentExtraction
            {
                RequestId = requestId, FilingDocumentId = document.FilingDocumentId, PromptVersion = ChargeInstrumentAi.PromptVersion, ModelId = "m",
                Status = ChargeInstrumentExtractionStatus.Completed, CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
                ResultJson = ChargeInstrumentAi.Serialize(new ChargeInstrumentAiResult([new InstrumentPassage(InstrumentPassageKind.Schedule, "Plot No. 5, Sector 9", 1, 1)]))
            });
            await writer.SaveChangesAsync();
        }

        Assert.NotSame(first, await cache.GetAsync(requestId));              // the quotes arrived: the earlier model is not reused
    }

    private sealed class TestEnv(string contentRoot) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
    }

    [Fact]
    public async Task A_passage_arriving_after_a_pdf_was_rendered_produces_a_new_file_and_an_unchanged_one_is_reused()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        var batch = new McaFilingBatch { RequestId = requestId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        seed.McaFilingBatches.Add(batch);
        await seed.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = requestId, Srn = "SRN-" + Guid.NewGuid().ToString("N")[..6], NestedZipName = "n.zip" };
        seed.McaFilings.Add(filing);
        await seed.SaveChangesAsync();
        var document = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = requestId, OriginalFileName = "Deed.pdf", StoragePath = "gone.pdf",
            FileHash = Guid.NewGuid().ToString("N"), ProcessingStatus = FilingDocumentProcessingStatus.Completed, Category = FilingCategory.Charge, UpdatedAt = DateTime.UtcNow
        };
        seed.McaFilingDocuments.Add(document);
        await seed.SaveChangesAsync();
        seed.ChargeDocumentLinks.Add(new ChargeDocumentLink
        {
            RequestId = requestId, BatchId = batch.BatchId, RocChargeNumber = "C1", FilingDocumentId = document.FilingDocumentId, Method = ChargeDocumentLinkMethod.EmbeddedAttachment
        });
        await seed.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "dossier-passages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = DossierGoldenMasterTests.CreateContext();
            var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
            var cache = new DossierCache(db, new DossierAssembler(db), memory);
            var gate = new CalculationArtifactGateService(db, new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<CalculationArtifactGateService>.Instance);
            var artifacts = new DossierArtifactService(cache, new DossierPdfRenderer(WebRoot()), gate, new TestEnv(root));

            var before = await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None);
            Assert.True(before.Rendered);
            Assert.Equal(before.Path, (await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None)).Path);   // nothing changed: reused

            await using (var writer = DossierGoldenMasterTests.CreateContext())
            {
                writer.ChargeInstrumentExtractions.Add(new ChargeInstrumentExtraction
                {
                    RequestId = requestId, FilingDocumentId = document.FilingDocumentId, PromptVersion = ChargeInstrumentAi.PromptVersion, ModelId = "m",
                    Status = ChargeInstrumentExtractionStatus.Completed, CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
                    ResultJson = ChargeInstrumentAi.Serialize(new ChargeInstrumentAiResult([new InstrumentPassage(InstrumentPassageKind.Schedule, "Plot No. 5, Sector 9, Navi Mumbai", 3, 3)]))
                });
                await writer.SaveChangesAsync();
            }

            var after = await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None);
            Assert.True(after.Rendered);
            Assert.NotEqual(before.Path, after.Path);                                        // the PDF built before the quotes existed is not served
            Assert.True(File.Exists(after.Path));
            Assert.Equal(after.Path, (await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None)).Path);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
