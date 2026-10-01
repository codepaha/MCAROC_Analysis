using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>The dossier annexure prints the Gemini property-particulars reading when one exists. Those extractions
/// complete after ingestion, so — the same staleness class #335 fixed for litigation links — a dossier model or PDF
/// cached before an extraction completed must never be served after it.</summary>
public class DossierPropertyExtractionTests : IAsyncLifetime
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
    public async Task A_property_extraction_completing_after_a_dossier_was_rendered_regenerates_the_model_and_pdf()
    {
        var particulars = $"All that piece of land bearing CTS No. 51/B situated at Vikhroli, Mumbai (ref {Guid.NewGuid():N})";
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        var c1 = await seed.RocCharges.Include(c => c.Events).FirstAsync(c => c.RequestId == requestId && c.RocChargeNumber == "C1");
        foreach (var ev in c1.Events) { ev.PropertyType = "Immovable property"; ev.PropertyParticulars = particulars; }
        await seed.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "dossier-property-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = DossierGoldenMasterTests.CreateContext();
            var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
            var cache = new DossierCache(db, new DossierAssembler(db, new ChargeLitigationService(db, memory)), memory);
            var gate = new CalculationArtifactGateService(db, new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<CalculationArtifactGateService>.Instance);
            var artifacts = new DossierArtifactService(cache, new DossierPdfRenderer(WebRoot()), gate, new TestEnv(root));

            // 1. Rendered before any extraction exists: rules reading only.
            var before = await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None);
            Assert.True(before.Rendered);
            var firstModel = await cache.GetAsync(requestId);
            Assert.Empty(firstModel!.PropertyExtractions!);
            Assert.Same(firstModel, await cache.GetAsync(requestId)); // unchanged: served from cache

            // 2. The Gemini extraction for that text completes.
            await using (var writer = DossierGoldenMasterTests.CreateContext())
            {
                writer.PropertyParticularsExtractions.Add(new PropertyParticularsExtraction
                {
                    TextHash = PropertyParticularsAi.HashOf(particulars, "Immovable property"), PromptVersion = PropertyParticularsAi.PromptVersion,
                    ModelId = PropertyParticularsAi.ModelId, SourceText = particulars, PropertyType = "Immovable property",
                    Status = PropertyParticularsExtractionStatus.Completed, CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow,
                    ExtractionJson = PropertyParticularsAi.Serialize(new PropertyParticularsAiResult([]))
                });
                await writer.SaveChangesAsync();
            }

            // 3. Neither the cached model nor the earlier PDF may be reused.
            var afterModel = await cache.GetAsync(requestId);
            Assert.NotSame(firstModel, afterModel);
            Assert.Single(afterModel!.PropertyExtractions!);
            var after = await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None);
            Assert.True(after.Rendered);
            Assert.NotEqual(before.Path, after.Path);
            Assert.True(File.Exists(after.Path));

            // 4. Nothing further changed: the second file is reused.
            Assert.Equal(after.Path, (await artifacts.EnsureRenderedAsync(requestId, DossierVariant.Executive, CancellationToken.None)).Path);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
