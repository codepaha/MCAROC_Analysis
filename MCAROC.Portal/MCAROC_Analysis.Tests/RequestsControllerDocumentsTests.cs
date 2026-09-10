using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>RequestsController.Documents paging guards (real .\SQLEXPRESS test DB). Only the DbContext
/// collaborator is exercised; the rest of the constructor graph is never reached by this action.</summary>
public class RequestsControllerDocumentsTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static RequestsController NewController(AppDbContext db) =>
        new(db, null!, null!, null!, null!, null!, Dossier.DossierGoldenMasterTests.CreateCache(), null!);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<long> SeedRequestWithBatchAsync(int filingCount)
    {
        await using var db = CreateContext();
        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Docs Co",
            RequestNumber = $"RCD-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        for (var i = 0; i < filingCount; i++)
        {
            var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = $"S{i:D4}" };
            db.McaFilings.Add(filing);
            await db.SaveChangesAsync();
            db.McaFilingDocuments.Add(new McaFilingDocument
            {
                FilingId = filing.FilingId,
                BatchId = batch.BatchId,
                RequestId = request.RequestId,
                OriginalFileName = $"doc{i}.pdf",
                Category = FilingCategory.Charge
            });
        }
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    [Fact]
    public async Task Page_far_beyond_the_end_is_clamped_and_does_not_overflow_or_throw()
    {
        var requestId = await SeedRequestWithBatchAsync(filingCount: 3);
        await using var db = CreateContext();

        var result = await NewController(db).Documents(requestId, category: null, page: int.MaxValue);

        var vm = Assert.IsType<DocumentsPageViewModel>(Assert.IsType<PartialViewResult>(result).Model);
        Assert.Equal(1, vm.TotalPages);       // 3 filings, 25/page
        Assert.Equal(1, vm.Page);             // int.MaxValue clamped into range
        Assert.Equal(3, vm.TotalFilings);
        Assert.Equal(3, vm.Filings.Count);
    }

    [Fact]
    public async Task Negative_or_zero_page_is_clamped_to_one()
    {
        var requestId = await SeedRequestWithBatchAsync(filingCount: 2);
        await using var db = CreateContext();

        var result = await NewController(db).Documents(requestId, category: null, page: -5);

        var vm = Assert.IsType<DocumentsPageViewModel>(Assert.IsType<PartialViewResult>(result).Model);
        Assert.Equal(1, vm.Page);
        Assert.Equal(2, vm.Filings.Count);
    }

    [Fact]
    public async Task Unknown_request_returns_NotFound()
    {
        await using var db = CreateContext();
        var result = await NewController(db).Documents(-9999, null, 1);
        Assert.IsType<NotFoundResult>(result);
    }
}
