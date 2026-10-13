using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LiveBatchPublisherTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        public DateTime Utc { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Utc, DateTimeKind.Utc));
    }

    private static readonly LiveBatchOptions Options = new() { MaxCases = 3, MaxWait = TimeSpan.FromMinutes(2) };

    private static async Task<long> NewRequestAsync()
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "LB" + Guid.NewGuid().ToString("N")[..8], ClientName = "Batch Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Batch Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"LB-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    /// <summary>A request case with a module case, a link with the given verdict, and a verified projection that differs from the stored columns (or equals them).</summary>
    private static async Task<long> CaseAsync(long requestId, TestClock clock, string no, PartyMatchOutcome verdict = PartyMatchOutcome.Verified,
        string projectedStatus = "DISPOSED", bool merged = false)
    {
        await using var db = Ctx();
        var legacy = new LitigationCase
        {
            RequestId = requestId, CaseNumber = no, CaseType = "OA", CaseYear = "2022", Court = "Vendor Court", CaseStatus = "PENDING",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(legacy);
        await db.SaveChangesAsync();
        var moduleCase = await new LitigationModuleStore(db).EnsureCaseForLegacyAsync(legacy.LitigationCaseId, default);
        await new RequestLinkStore(db).UpsertAsync(requestId, moduleCase.CaseId, new PartyMatchResult(verdict, "exact", "BATCH TEST COMPANY", null), "respondent", false, null, default);
        await new LiveProjectionStore(db, clock).PublishAsync(new ObservationInput(moduleCase.CaseId, ObservationSource.DrtPortal, ObservationOutcome.Found, true, clock.Utc,
            new ParsedCaseSnapshot(projectedStatus, null, "Vendor Court", null, null, null, null, null, null, 0)), default);
        if (merged)
        {
            var survivor = new LitigationCase { RequestId = requestId, CaseNumber = no + "S", CaseStatus = "PENDING", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
            db.LitigationCases.Add(survivor);
            await db.SaveChangesAsync();
            await db.LitigationCases.Where(c => c.LitigationCaseId == legacy.LitigationCaseId).ExecuteUpdateAsync(u => u.SetProperty(c => c.MergedIntoCaseId, survivor.LitigationCaseId));
        }
        return legacy.LitigationCaseId;
    }

    private static async Task<string?> StatusAsync(long legacyId) { await using var db = Ctx(); return (await db.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == legacyId)).CaseStatus; }

    private static async Task<long> VersionAsync(long requestId) { await using var db = Ctx(); return await RequestLiveData.ReadAsync(db, requestId, default); }

    private static async Task<LiveBatchResult?> PassAsync(TestClock clock, long requestId)
    {
        await using var db = Ctx();
        return (await new LiveBatchPublisher(db, Options, clock).PublishDueAsync(default)).SingleOrDefault(r => r.RequestId == requestId);
    }

    [Fact]
    public async Task Changes_wait_until_the_batch_is_due_then_go_together_with_one_version_bump()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var requestId = await NewRequestAsync();
        var a = await CaseAsync(requestId, clock, "101");
        var b = await CaseAsync(requestId, clock, "102");
        var before = await VersionAsync(requestId);

        Assert.Null(await PassAsync(clock, requestId));                                         // two waiting, just changed: not due yet
        Assert.Equal("PENDING", await StatusAsync(a));

        clock.Utc += TimeSpan.FromMinutes(2);
        var result = await PassAsync(clock, requestId);
        Assert.NotNull(result);
        Assert.Equal(2, result.Published);
        Assert.Equal("DISPOSED", await StatusAsync(a));
        Assert.Equal("DISPOSED", await StatusAsync(b));
        Assert.Equal(before + 1, await VersionAsync(requestId));                                // one bump for the whole batch

        Assert.Null(await PassAsync(clock, requestId));                                         // and nothing waits any more
        Assert.Equal(before + 1, await VersionAsync(requestId));
    }

    [Fact]
    public async Task A_full_batch_goes_at_once_and_takes_no_more_than_the_batch_size()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var requestId = await NewRequestAsync();
        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await CaseAsync(requestId, clock, "20" + i));
            clock.Utc += TimeSpan.FromSeconds(1);                                               // oldest change first
        }

        var result = await PassAsync(clock, requestId);                                         // 4 waiting >= 3: due without waiting
        Assert.Equal(3, result!.Considered);
        Assert.Equal(3, result.Published);
        Assert.Equal("PENDING", await StatusAsync(ids[3]));                                     // the newest waits for the next batch
        await using var db = Ctx();
        Assert.Equal(1, await new LiveBatchPublisher(db, Options, clock).PendingCountAsync(requestId, default));
    }

    [Fact]
    public async Task Only_verified_links_into_the_request_own_live_rows_are_published()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var requestId = await NewRequestAsync();
        var ambiguous = await CaseAsync(requestId, clock, "301", PartyMatchOutcome.Ambiguous);
        var rejected = await CaseAsync(requestId, clock, "302", PartyMatchOutcome.Rejected);
        var merged = await CaseAsync(requestId, clock, "303", merged: true);

        await using var db = Ctx();
        var publisher = new LiveBatchPublisher(db, Options, clock);
        Assert.Equal(0, await publisher.PendingCountAsync(requestId, default));
        var result = await publisher.PublishBatchAsync(requestId, default);
        Assert.Equal(0, result.Considered);
        Assert.Equal("PENDING", await StatusAsync(ambiguous));
        Assert.Equal("PENDING", await StatusAsync(rejected));
        Assert.Equal("PENDING", await StatusAsync(merged));
    }

    [Fact]
    public async Task A_projection_that_changes_nothing_moves_the_marker_without_a_bump()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var requestId = await NewRequestAsync();
        var same = await CaseAsync(requestId, clock, "401", projectedStatus: "PENDING");      // the portal agrees with the stored columns
        var before = await VersionAsync(requestId);

        await using var db = Ctx();
        var publisher = new LiveBatchPublisher(db, Options, clock);
        var result = await publisher.PublishBatchAsync(requestId, default);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, result.Published);
        Assert.Null(result.NewLiveDataVersion);
        Assert.Equal(before, await VersionAsync(requestId));
        Assert.Equal(0, await publisher.PendingCountAsync(requestId, default));                 // no longer waiting
        Assert.Equal("PENDING", await StatusAsync(same));
    }

    [Fact]
    public async Task A_newer_projection_after_publication_waits_again()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var requestId = await NewRequestAsync();
        var legacy = await CaseAsync(requestId, clock, "501");
        await using (var db = Ctx()) await new LiveBatchPublisher(db, Options, clock).PublishBatchAsync(requestId, default);
        Assert.Equal("DISPOSED", await StatusAsync(legacy));

        await using (var db = Ctx())
        {
            var caseId = await db.Set<ModuleCase>().Where(c => c.LegacyLitigationCaseId == legacy).Select(c => c.CaseId).SingleAsync();
            clock.Utc += TimeSpan.FromMinutes(1);
            await new LiveProjectionStore(db, clock).PublishAsync(new ObservationInput(caseId, ObservationSource.DrtPortal, ObservationOutcome.Found, true, clock.Utc,
                new ParsedCaseSnapshot("PENDING", "Hearing", "Vendor Court", null, null, null, null, null, null, 1)), default);
            Assert.Equal(1, await new LiveBatchPublisher(db, Options, clock).PendingCountAsync(requestId, default));
        }
    }
}
