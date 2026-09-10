using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
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
        new(db, null!, null!, null!, null!, null!, Dossier.DossierGoldenMasterTests.CreateCache(), new FakeEnv(environment));

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

    [Fact]
    public async Task Details_does_not_show_a_prior_analysis_against_a_newer_ingestion_run()
    {
        long requestId;
        long ingestion1, ingestion2, analysis1Id, analysis2Id;
        await using (var db = CreateContext())
        {
            var request = new McaRequest
            {
                ClientId = 1, EntityType = EntityType.Company, CompanyName = "Reingest Analysis Co",
                RequestNumber = $"RIA-{Guid.NewGuid():N}", RequestStatus = RequestStatus.AnalysisCompleted,
                CreatedDate = DateTime.UtcNow
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.RequestId;

            var i1 = new IngestionRun { RequestId = requestId, RunNumber = 1, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
            db.IngestionRuns.Add(i1);
            await db.SaveChangesAsync();
            ingestion1 = i1.IngestionRunId;

            var a1 = new AnalysisRun { RequestId = requestId, IngestionRunId = ingestion1, RunNumber = 1, Status = AnalysisRunStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, OverallReviewPriority = ReviewPriority.High };
            db.AnalysisRuns.Add(a1);
            await db.SaveChangesAsync();
            analysis1Id = a1.AnalysisRunId;

            request.LatestCompletedIngestionRunId = ingestion1;
            await db.SaveChangesAsync();
        }

        // 1. Analysis A1 belongs to the current ingestion → it is shown.
        await using (var db = CreateContext())
        {
            var vm = await LoadDetailsVm(db, requestId);
            Assert.Equal(analysis1Id, vm.LatestAnalysisRun?.AnalysisRunId);
        }

        // 2. A re-ingest produces IngestionRun 2 with no analysis of its own yet.
        await using (var db = CreateContext())
        {
            var i2 = new IngestionRun { RequestId = requestId, RunNumber = 2, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
            db.IngestionRuns.Add(i2);
            await db.SaveChangesAsync();
            ingestion2 = i2.IngestionRunId;
            await db.Requests.Where(r => r.RequestId == requestId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LatestCompletedIngestionRunId, ingestion2));
        }

        // 3. The stale A1 is NOT shown against the new ingestion run.
        await using (var db = CreateContext())
        {
            var vm = await LoadDetailsVm(db, requestId);
            Assert.Null(vm.LatestAnalysisRun);
            Assert.Empty(vm.AnalysisFindings);
        }

        // 4. Once the matching analysis A2 completes, it is shown.
        await using (var db = CreateContext())
        {
            var a2 = new AnalysisRun { RequestId = requestId, IngestionRunId = ingestion2, RunNumber = 2, Status = AnalysisRunStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, OverallReviewPriority = ReviewPriority.Medium };
            db.AnalysisRuns.Add(a2);
            await db.SaveChangesAsync();
            analysis2Id = a2.AnalysisRunId;
        }
        await using (var db = CreateContext())
        {
            var vm = await LoadDetailsVm(db, requestId);
            Assert.Equal(analysis2Id, vm.LatestAnalysisRun?.AnalysisRunId);
        }
    }

    private static async Task<RequestDetailsViewModel> LoadDetailsVm(AppDbContext db, long requestId)
    {
        var result = await NewController(db, "Development").Details(requestId, charge: null);
        return Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(result).Model);
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
