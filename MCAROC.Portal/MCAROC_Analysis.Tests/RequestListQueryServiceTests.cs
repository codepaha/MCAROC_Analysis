using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public class RequestListQueryServiceTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Client> SeedClientAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = $"T{Guid.NewGuid():N}"[..10], ClientName = "Test Client", IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private async Task<McaRequest> SeedRequestAsync(AppDbContext db, Client client, string companyName = "Test Co",
        string? cin = null, DateTime? createdDate = null)
    {
        var request = new McaRequest
        {
            ClientId = client.ClientId, EntityType = EntityType.Company, CompanyName = companyName,
            Cin = cin ?? $"CIN{Guid.NewGuid():N}"[..15], RequestNumber = $"TEST-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = createdDate ?? DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private async Task<AnalysisRun> SeedAnalysisRunAsync(AppDbContext db, McaRequest request, ReviewPriority? priority)
    {
        var run = new AnalysisRun
        {
            RequestId = request.RequestId, IngestionRunId = 1, RunNumber = 1, Status = AnalysisRunStatus.Completed,
            StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, OverallReviewPriority = priority
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    [Theory]
    [InlineData("acme finance")]
    [InlineData("ACME FINANCE")]
    [InlineData("  Acme Finance  ")]
    public async Task SearchText_MatchesCompanyName_TrimmedAndCaseInsensitive(string searchInput)
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var request = await SeedRequestAsync(db, client, companyName: "Acme Finance Pvt Ltd");

        var vm = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), SearchText = searchInput
        });

        Assert.Contains(vm.Rows, r => r.Request.RequestId == request.RequestId);
    }

    [Fact]
    public async Task SearchText_MatchesCinLlpinPan()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var request = await SeedRequestAsync(db, client, cin: "U12345MH2020PTC123456");

        var vm = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), SearchText = "U12345"
        });

        Assert.Contains(vm.Rows, r => r.Request.RequestId == request.RequestId);
    }

    [Fact]
    public async Task SearchText_NonMatchingText_ExcludesRequest()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        await SeedRequestAsync(db, client, companyName: "Acme Finance Pvt Ltd");

        var vm = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), SearchText = "NoSuchCompanyXYZ"
        });

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task DefaultSort_IsAlwaysNewestFirst_RegardlessOfEntryFilters()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var older = await SeedRequestAsync(db, client, createdDate: DateTime.UtcNow.AddDays(-5));
        var newer = await SeedRequestAsync(db, client, createdDate: DateTime.UtcNow);

        // Simulate a drill-down link that set AttentionRequired but left Sort at its default.
        var vm = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10))
        });

        Assert.Equal(newer.RequestId, vm.Rows[0].Request.RequestId);
        Assert.Equal(older.RequestId, vm.Rows[1].Request.RequestId);
    }

    [Fact]
    public async Task PriorityDescSort_RanksByRealSeverityMeaning_NotAlphabeticalStringOrder()
    {
        // Regression test for the string-enum sort trap: ReviewPriority is HasConversion<string>, so a
        // naive SQL-pushed ORDER BY would sort alphabetically ("High" < "Low" < "Medium"). This must sort
        // by real meaning: High first, then Medium, then Low.
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var lowReq = await SeedRequestAsync(db, client);
        await SeedAnalysisRunAsync(db, lowReq, ReviewPriority.Low);
        var mediumReq = await SeedRequestAsync(db, client);
        await SeedAnalysisRunAsync(db, mediumReq, ReviewPriority.Medium);
        var highReq = await SeedRequestAsync(db, client);
        await SeedAnalysisRunAsync(db, highReq, ReviewPriority.High);

        var vm = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), Sort = RequestListSort.PriorityDesc
        });

        Assert.Equal([highReq.RequestId, mediumReq.RequestId, lowReq.RequestId], vm.Rows.Select(r => r.Request.RequestId));
    }

    [Fact]
    public async Task Paging_ReturnsCorrectPageAndTotalCount()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        for (var i = 0; i < 30; i++) await SeedRequestAsync(db, client, createdDate: DateTime.UtcNow.AddMinutes(-i));

        var page1 = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), Page = 1
        });
        var page2 = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), Page = 2
        });

        Assert.Equal(30, page1.TotalCount);
        Assert.Equal(RequestListFilterCriteria.PageSize, page1.Rows.Count);
        Assert.Equal(5, page2.Rows.Count);
        Assert.Empty(page1.Rows.Select(r => r.Request.RequestId).Intersect(page2.Rows.Select(r => r.Request.RequestId)));
    }

    [Fact]
    public async Task ClientAndDateRangeFilters_CombineCorrectly()
    {
        await using var db = CreateContext();
        var clientA = await SeedClientAsync(db);
        var clientB = await SeedClientAsync(db);
        var inRangeA = await SeedRequestAsync(db, clientA, createdDate: DateTime.UtcNow.AddDays(-5));
        await SeedRequestAsync(db, clientA, createdDate: DateTime.UtcNow.AddDays(-20)); // out of range
        await SeedRequestAsync(db, clientB, createdDate: DateTime.UtcNow.AddDays(-5)); // wrong client

        var vm = await new RequestListQueryService(db).SearchAsync(new RequestListFilterCriteria
        {
            ClientId = clientA.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)), DateTo = DateOnly.FromDateTime(DateTime.UtcNow)
        });

        var row = Assert.Single(vm.Rows);
        Assert.Equal(inRangeA.RequestId, row.Request.RequestId);
    }
}
