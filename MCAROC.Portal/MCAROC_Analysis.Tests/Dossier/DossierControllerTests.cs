using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace MCAROC_Analysis.Tests.Dossier;

public class DossierControllerTests : IAsyncLifetime
{
    private string _contentRoot = "";

    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
        _contentRoot = Path.Combine(Path.GetTempPath(), "mcaroc-dossier-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private DossierController NewController(AppDbContext db)
    {
        var cacheDb = DossierGoldenMasterTests.CreateContext();
        var cache = new DossierCache(cacheDb, new DossierAssembler(cacheDb),
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 }));
        var webRoot = Path.Combine(FindRepoRoot(), "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
        return new DossierController(db, cache, new DossierPdfRenderer(webRoot), new FakeEnv(_contentRoot, webRoot));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Theory]
    [InlineData("executive", "Executive")]
    [InlineData("full", "Full source")]
    public async Task Returns_a_pdf_with_a_client_filename(string variant, string label)
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db).Download(requestId, variant, CancellationToken.None);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Contains(label, file.FileDownloadName);
        Assert.True(new FileInfo(file.FileName).Length > 0);
    }

    [Fact]
    public async Task Unknown_request_is_NotFound()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        Assert.IsType<NotFoundResult>(await NewController(db).Download(-999, "executive", CancellationToken.None));
    }

    [Fact]
    public async Task Request_with_no_completed_ingestion_is_409()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var client = new Client { ClientCode = $"DC{Guid.NewGuid():N}"[..10], ClientName = "X", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Not Ready Co",
            RequestNumber = $"NR-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var result = await NewController(db).Download(request.RequestId, "executive", CancellationToken.None);
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    private sealed class FakeEnv(string contentRoot, string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(webRoot);
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
    }
}
