using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

namespace MCAROC_Analysis.Tests;

/// <summary>The dev-only re-ingest maintenance action must be invisible outside Development, and must
/// 404 an unknown request even inside it. The upload/orchestrate happy path is exercised by the manual
/// demo run, not here (it would re-run the whole ingestion pipeline).</summary>
public class RequestsControllerReingestTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static RequestsController NewController(AppDbContext db, string environment) =>
        new(db, null!, null!, null!, null!, null!, new FakeEnv(environment));

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<long> SeedRequestAsync()
    {
        await using var db = CreateContext();
        var request = new McaRequest
        {
            ClientId = 1, EntityType = EntityType.Company, CompanyName = "Reingest Co",
            RequestNumber = $"RI-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    [Fact]
    public async Task Get_is_NotFound_outside_Development()
    {
        var id = await SeedRequestAsync();
        await using var db = CreateContext();
        Assert.IsType<NotFoundResult>(await NewController(db, "Production").Reingest(id));
    }

    [Fact]
    public async Task Post_is_NotFound_outside_Development()
    {
        var id = await SeedRequestAsync();
        await using var db = CreateContext();
        Assert.IsType<NotFoundResult>(
            await NewController(db, "Staging").Reingest(id, rocFile: null, chargeFile: null));
    }

    [Fact]
    public async Task Get_is_NotFound_for_an_unknown_request_in_Development()
    {
        await using var db = CreateContext();
        Assert.IsType<NotFoundResult>(await NewController(db, "Development").Reingest(-9999));
    }

    [Fact]
    public async Task Get_renders_for_a_real_request_in_Development()
    {
        var id = await SeedRequestAsync();
        await using var db = CreateContext();
        var result = await NewController(db, "Development").Reingest(id);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(id, Assert.IsType<McaRequest>(view.Model).RequestId);
    }

    private sealed class FakeEnv(string environment) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
