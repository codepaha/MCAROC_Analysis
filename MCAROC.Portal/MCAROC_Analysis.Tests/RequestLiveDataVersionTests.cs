using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.RequestDetails;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class RequestLiveDataVersionTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<long> NewRequestAsync()
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "LV" + Guid.NewGuid().ToString("N")[..8], ClientName = "Live Version Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Live Version Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"LV-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    [Fact]
    public async Task A_new_request_starts_at_zero_and_a_bump_adds_one()
    {
        var id = await NewRequestAsync();
        await using var db = Ctx();
        Assert.Equal(0, await RequestLiveData.ReadAsync(db, id, default));
        Assert.Equal(1, await RequestLiveData.BumpAsync(db, id, default));
        Assert.Equal(2, await RequestLiveData.BumpAsync(db, id, default));
        Assert.Null(await RequestLiveData.BumpAsync(db, long.MaxValue - 3, default));
    }

    [Fact]
    public async Task Concurrent_bumps_are_never_lost()
    {
        var id = await NewRequestAsync();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = Ctx();
            await RequestLiveData.BumpAsync(db, id, default);
        }));
        await using var verify = Ctx();
        Assert.Equal(12, await RequestLiveData.ReadAsync(verify, id, default));
    }

    [Fact]
    public async Task A_bump_changes_the_request_page_fence()
    {
        var id = await NewRequestAsync();
        await using var db = Ctx();
        var before = await RequestPageVersion.ReadAsync(db, id, default);
        await RequestLiveData.BumpAsync(db, id, default);
        var after = await RequestPageVersion.ReadAsync(db, id, default);
        Assert.NotEqual(before!.Key, after!.Key);
        Assert.Equal(before.LiveData + 1, after.LiveData);
        Assert.Equal(after.Key, (await RequestPageVersion.ReadAsync(db, id, default))!.Key);   // and it is stable between bumps
    }

    [Fact]
    public async Task A_bump_changes_the_charge_litigation_version_the_dossier_caches_key_on()
    {
        var id = await NewRequestAsync();
        await using var db = Ctx();
        var before = await ChargeLitigationService.VersionAsync(db, id);
        await RequestLiveData.BumpAsync(db, id, default);
        var after = await ChargeLitigationService.VersionAsync(db, id);
        Assert.NotEqual(before, after);
        Assert.Equal(after, await ChargeLitigationService.VersionAsync(db, id));
    }

    [Fact]
    public async Task A_bump_inside_a_transaction_that_rolls_back_leaves_no_trace()
    {
        var id = await NewRequestAsync();
        await using var db = Ctx();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await RequestLiveData.BumpAsync(db, id, default);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await RequestLiveData.ReadAsync(Ctx(), id, default));
    }
}
