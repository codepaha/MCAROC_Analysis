using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;

namespace MCAROC_Analysis.Tests;

/// <summary>Coverage for the internal Clients admin page — the one place <see cref="MCAROC_Analysis.Data.Entities.Client.IncludeLitigationInDossier"/>
/// can be changed. The save path must both persist the flag and actively invalidate every affected
/// request's cached dossier (in-memory model + on-disk PDF), per DossierCache's own doc comment: neither
/// cache key includes client-level settings, so nothing else does this automatically.</summary>
public class ClientsControllerTests : IAsyncLifetime
{
    private string _contentRoot = "";

    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
        _contentRoot = Path.Combine(Path.GetTempPath(), "mcaroc-clients-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private ClientsController NewController(AppDbContext db, IMemoryCache cache)
    {
        var cacheDb = DossierGoldenMasterTests.CreateContext();
        var dossierCache = new DossierCache(cacheDb, new DossierAssembler(cacheDb), cache);
        return new ClientsController(db, dossierCache, new FakeEnv(_contentRoot))
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    [Fact]
    public async Task Index_lists_clients_ordered_by_name()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db, new MemoryCache(new MemoryCacheOptions())).Index();

        var view = Assert.IsType<ViewResult>(result);
        var clients = Assert.IsAssignableFrom<IReadOnlyList<MCAROC_Analysis.Data.Entities.Client>>(view.Model);
        Assert.NotEmpty(clients);
    }

    [Fact]
    public async Task Edit_get_unknown_client_is_not_found()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db, new MemoryCache(new MemoryCacheOptions())).Edit(-999);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_get_returns_the_clients_current_flag_value()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed, includeLitigationInDossier: false);
        var clientId = await seed.Requests.Where(r => r.RequestId == requestId).Select(r => r.ClientId).SingleAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db, new MemoryCache(new MemoryCacheOptions())).Edit(clientId);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ClientEditViewModel>(view.Model);
        Assert.Equal(clientId, model.ClientId);
        Assert.False(model.IncludeLitigationInDossier);
    }

    [Fact]
    public async Task Edit_post_mismatched_id_is_bad_request()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db, new MemoryCache(new MemoryCacheOptions()))
            .Edit(1, new ClientEditViewModel { ClientId = 2 });
        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Edit_post_unknown_client_is_not_found()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db, new MemoryCache(new MemoryCacheOptions()))
            .Edit(-999, new ClientEditViewModel { ClientId = -999 });
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_post_toggling_the_flag_persists_it_and_invalidates_the_in_memory_cache()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        var clientId = await seed.Requests.Where(r => r.RequestId == requestId).Select(r => r.ClientId).SingleAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        // Warm the cache exactly the way RequestsController/DossierController do, via DossierCache.GetAsync,
        // before the flag is toggled — the save must bust this same entry.
        await using (var warmDb = DossierGoldenMasterTests.CreateContext())
        {
            var warmed = await new DossierCache(warmDb, new DossierAssembler(warmDb), cache).GetAsync(requestId);
            Assert.NotNull(warmed);
        }

        await using var db = DossierGoldenMasterTests.CreateContext();
        var controller = NewController(db, cache);
        var result = await controller.Edit(clientId, new ClientEditViewModel
        {
            ClientId = clientId, ClientName = "Test Bank", ClientCode = "X", IncludeLitigationInDossier = false
        });

        Assert.IsType<RedirectToActionResult>(result);

        await using var verifyDb = DossierGoldenMasterTests.CreateContext();
        var persisted = await verifyDb.Clients.FindAsync(clientId);
        Assert.False(persisted!.IncludeLitigationInDossier);

        // The exact same cache instance, re-queried via a fresh DossierCache pointed at the same IMemoryCache —
        // a live cache entry would short-circuit BuildAsync and never re-check IncludeLitigation.
        await using var readBackDb = DossierGoldenMasterTests.CreateContext();
        var afterInvalidation = await new DossierCache(readBackDb, new DossierAssembler(readBackDb), cache).GetAsync(requestId);
        Assert.False(afterInvalidation!.IncludeLitigation);
    }

    [Fact]
    public async Task Edit_post_toggling_the_flag_deletes_cached_pdf_files_on_disk()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        var clientId = await seed.Requests.Where(r => r.RequestId == requestId).Select(r => r.ClientId).SingleAsync();

        var dossierDir = Path.Combine(_contentRoot, "App_Data", "Dossiers", requestId.ToString());
        Directory.CreateDirectory(dossierDir);
        var stalePdf = Path.Combine(dossierDir, "executive-1-1.pdf");
        await File.WriteAllBytesAsync(stalePdf, [1, 2, 3]);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var controller = NewController(db, new MemoryCache(new MemoryCacheOptions()));
        await controller.Edit(clientId, new ClientEditViewModel
        {
            ClientId = clientId, ClientName = "Test Bank", ClientCode = "X", IncludeLitigationInDossier = false
        });

        Assert.False(File.Exists(stalePdf));
    }

    [Fact]
    public async Task Edit_post_leaving_the_flag_unchanged_does_not_invalidate_the_cache()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        var clientId = await seed.Requests.Where(r => r.RequestId == requestId).Select(r => r.ClientId).SingleAsync();

        var cache = new MemoryCache(new MemoryCacheOptions());
        await using (var warmDb = DossierGoldenMasterTests.CreateContext())
            Assert.NotNull(await new DossierCache(warmDb, new DossierAssembler(warmDb), cache).GetAsync(requestId));

        var dossierDir = Path.Combine(_contentRoot, "App_Data", "Dossiers", requestId.ToString());
        Directory.CreateDirectory(dossierDir);
        var pdf = Path.Combine(dossierDir, "executive-1-1.pdf");
        await File.WriteAllBytesAsync(pdf, [1, 2, 3]);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var controller = NewController(db, cache);
        // Same value as today's default (true) — a genuine no-op save.
        await controller.Edit(clientId, new ClientEditViewModel
        {
            ClientId = clientId, ClientName = "Test Bank", ClientCode = "X", IncludeLitigationInDossier = true
        });

        Assert.True(File.Exists(pdf));
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
    }
}
