using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Fetching;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class RefreshRunServiceTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the lookup for a keyless vendor row (pure)

    private static LitigationCase Row(string court, string number, string? state = null) =>
        new() { CaseNumber = number, Court = court, State = state, CaseStatus = "PENDING" };

    [Fact]
    public void A_vendor_row_at_a_drt_is_looked_up_at_that_tribunal()
    {
        var l = RefreshLookup.FromLegacy(Row("Debts Recovery Tribunal Delhi (DRT 1)", "OA/413/2022"), out _)!;
        Assert.Equal(FetchHost.Drt, l.Host);
        Assert.Equal("DRT", l.Forum);
        Assert.Equal(1, l.VenueId);
        Assert.Equal(("OA", "413", 2022), (l.Parts.TypeCode, l.Parts.No, l.Parts.Year));

        var drat = RefreshLookup.FromLegacy(Row("Debts Recovery Appellate Tribunal Delhi", "RA/12/2021"), out _);
        Assert.Equal("DRAT", drat?.Forum);
    }

    [Fact]
    public void A_vendor_row_at_a_consumer_commission_is_looked_up_on_the_consumer_portal()
    {
        var l = RefreshLookup.FromLegacy(Row("National Consumer Disputes Redressal Commission", "CC/599/2017"), out _)!;
        Assert.Equal(FetchHost.Consumer, l.Host);
        Assert.Equal(11000000, l.VenueId);
    }

    [Fact]
    public void Rows_no_portal_can_answer_are_reported_with_the_reason()
    {
        Assert.Null(RefreshLookup.FromLegacy(Row("High Court of Delhi", "WP(C)/100/2020"), out var hc));
        Assert.Contains("not a DRT", hc);
        Assert.Null(RefreshLookup.FromLegacy(Row("Debts Recovery Tribunal Delhi (DRT 1)", "413"), out var bare));       // no type or year to look up
        Assert.Contains("type, number and year", bare);
        Assert.Null(RefreshLookup.FromLegacy(Row("", "OA/413/2022"), out var noCourt));
        Assert.Contains("court", noCourt);
    }

    [Fact]
    public void The_payload_round_trips_and_carries_the_request()
    {
        var lookup = RefreshLookup.FromLegacy(Row("Debts Recovery Tribunal Delhi (DRT 1)", "OA/413/2022"), out _)!;
        var back = RefreshPayload.FromJson(RefreshPayload.From(lookup, 42).ToJson())!;
        Assert.Equal(42, back.RequestId);
        var again = back.ToLookup();
        Assert.Equal((lookup.Host, lookup.Forum, lookup.VenueId, lookup.Parts.Display), (again.Host, again.Forum, again.VenueId, again.Parts.Display));
        Assert.Null(RefreshPayload.FromJson("not json"));
    }

    // ---- starting and following a run

    private static async Task<long> RequestAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "RR" + Guid.NewGuid().ToString("N")[..8], ClientName = "Run Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Run Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"RR-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    private static LitigationCase Case(long requestId, string number, string court, string status, string? nextHearing, string? lastHearing = "01-09-2026") => new()
    {
        RequestId = requestId, CaseNumber = number, Court = court, CaseStatus = status, State = "Delhi", District = "New Delhi", FilingDate = "05-12-2022",
        LastHearingDate = lastHearing, NextHearingDate = nextHearing, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task A_run_queues_only_the_cases_worth_a_call_and_counts_the_rest()
    {
        await using var db = Ctx();
        var requestId = await RequestAsync(db);
        const string drt = "Debts Recovery Tribunal Delhi (DRT 1)";
        var due = Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2022", drt, "PENDING", nextHearing: "01-10-2026");     // hearing has passed
        var notDue = Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2022", drt, "PENDING", nextHearing: "01-12-2099");  // future hearing, verified before
        var disposed = Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2021", drt, "DISPOSED", nextHearing: null);
        var highCourt = Case(requestId, "WP(C)/100/2020", "High Court of Delhi", "PENDING", nextHearing: "01-10-2026");
        var merged = Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2022", drt, "PENDING", nextHearing: "01-10-2026");
        db.LitigationCases.AddRange(due, notDue, disposed, highCourt, merged);
        await db.SaveChangesAsync();
        merged.MergedIntoCaseId = due.LitigationCaseId;
        await db.SaveChangesAsync();
        var notDueModule = await new LitigationModuleStore(db).EnsureCaseForLegacyAsync(notDue.LitigationCaseId, default);
        await new LiveProjectionStore(db).PublishAsync(new ObservationInput(notDueModule.CaseId, ObservationSource.DrtPortal, ObservationOutcome.Found, true, DateTime.UtcNow,
            new ParsedCaseSnapshot("PENDING", null, drt, "5-12-2022", "1-9-2026", "1-12-2099", null, null, null, 3)), default);

        var queue = new FetchWorkQueue(db);
        var runs = new RefreshRunService(db, queue);
        var start = await runs.StartAsync(requestId, default);

        Assert.False(start.AlreadyRunning);
        Assert.Equal(1, start.Enqueued);
        Assert.Equal(1, start.NotDue);
        Assert.Equal(1, start.Disposed);
        Assert.Equal(1, start.NoLookup);
        Assert.Contains(start.NoLookupReasons.Keys, k => k.Contains("not a DRT"));

        var item = await db.Set<ModuleFetchWorkItem>().AsNoTracking().SingleAsync(w => w.RunId == start.RunId);
        Assert.Equal((int)FetchHost.Drt, item.Host);
        var payload = RefreshPayload.FromJson(item.PayloadJson)!;
        Assert.Equal(requestId, payload.RequestId);
        Assert.Equal(1, payload.VenueId);
        var progress = await runs.ProgressAsync(start.RunId, default);
        Assert.Equal(1, progress!.EligibleAtStart);                                                 // the bar's total: disposed and not-due are not in it
        Assert.Equal(1, progress.Queued);
    }

    [Fact]
    public async Task A_second_start_while_a_run_is_open_returns_that_run_and_the_run_finishes_once_its_items_are_done()
    {
        await using var db = Ctx();
        var requestId = await RequestAsync(db);
        db.LitigationCases.Add(Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2022", "Debts Recovery Tribunal Delhi (DRT 2)", "PENDING", nextHearing: "01-10-2026"));
        await db.SaveChangesAsync();
        var queue = new FetchWorkQueue(db);
        var runs = new RefreshRunService(db, queue);

        var first = await runs.StartAsync(requestId, default);
        var second = await runs.StartAsync(requestId, default);
        Assert.True(second.AlreadyRunning);
        Assert.Equal(first.RunId, second.RunId);
        Assert.False(await runs.FinishIfDoneAsync(first.RunId, default));                          // its item is still queued

        var itemId = await db.Set<ModuleFetchWorkItem>().AsNoTracking().Where(w => w.RunId == first.RunId).Select(w => w.WorkItemId).SingleAsync();
        await db.Set<ModuleFetchWorkItem>().Where(w => w.WorkItemId == itemId).ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, (int)FetchWorkStatus.Succeeded));
        Assert.True(await runs.FinishIfDoneAsync(first.RunId, default));
        Assert.False(await runs.FinishIfDoneAsync(first.RunId, default));                          // once only
        var progress = await runs.ProgressAsync(first.RunId, default);
        Assert.NotNull(progress!.FinishedUtc);
        Assert.Equal(MCAROC_Analysis.Services.LitigationModule.Freshness.RefreshPhase.RefreshingCases, progress.Phase);   // the banner's own phase values

        Assert.False((await runs.StartAsync(requestId, default)).AlreadyRunning);                  // and a new run may start
    }

    [Fact]
    public async Task A_run_with_nothing_to_fetch_is_finished_at_once()
    {
        await using var db = Ctx();
        var requestId = await RequestAsync(db);
        db.LitigationCases.Add(Case(requestId, "OA/1/2020", "Debts Recovery Tribunal Delhi (DRT 1)", "DISPOSED", nextHearing: null));
        await db.SaveChangesAsync();
        var runs = new RefreshRunService(db, new FetchWorkQueue(db));
        var start = await runs.StartAsync(requestId, default);
        Assert.Equal(0, start.Enqueued);
        Assert.NotNull((await runs.ProgressAsync(start.RunId, default))!.FinishedUtc);
    }

    [Fact]
    public async Task A_start_that_fails_part_way_leaves_no_open_run_and_the_next_start_works()
    {
        await using var db = Ctx();
        var requestId = await RequestAsync(db);
        db.LitigationCases.Add(Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2022", "Debts Recovery Tribunal Delhi (DRT 3)", "PENDING", nextHearing: "01-10-2026"));
        await db.SaveChangesAsync();

        var failing = new RefreshRunService(db, new FetchWorkQueue(db)) { BeforeCommit = _ => throw new InvalidOperationException("simulated failure after queueing") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.StartAsync(requestId, default));

        await using (var check = Ctx())
        {
            Assert.False(await check.Set<ModuleRefreshRun>().AnyAsync(r => r.RequestId == requestId));             // nothing half-started
            var caseIds = check.Set<ModuleCase>().Where(c => check.LitigationCases.Any(l => l.RequestId == requestId && l.LitigationCaseId == c.LegacyLitigationCaseId)).Select(c => c.CaseId);
            Assert.False(await check.Set<ModuleFetchWorkItem>().AnyAsync(w => w.CaseId != null && caseIds.Contains(w.CaseId.Value)));
        }

        await using var retry = Ctx();
        var start = await new RefreshRunService(retry, new FetchWorkQueue(retry)).StartAsync(requestId, default);
        Assert.False(start.AlreadyRunning);
        Assert.Equal(1, start.Enqueued);
    }

    [Fact]
    public async Task Two_starts_at_once_make_one_run()
    {
        long requestId;
        await using (var db = Ctx())
        {
            requestId = await RequestAsync(db);
            db.LitigationCases.Add(Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2022", "Debts Recovery Tribunal Delhi (DRT 1)", "PENDING", nextHearing: "01-10-2026"));
            await db.SaveChangesAsync();
        }
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = Ctx();
            return await new RefreshRunService(db, new FetchWorkQueue(db)).StartAsync(requestId, default);
        }));
        Assert.Single(starts.Select(s => s.RunId).Distinct());
        Assert.Equal(1, starts.Count(s => !s.AlreadyRunning));
    }

    [Fact]
    public async Task Completing_a_run_publishes_every_waiting_result_before_marking_it_finished()
    {
        await using var db = Ctx();
        var requestId = await RequestAsync(db);
        var legacy = Case(requestId, "OA/" + Random.Shared.Next(10_000, 99_999) + "/2022", "Debts Recovery Tribunal Delhi (DRT 1)", "PENDING", nextHearing: "01-10-2026");
        db.LitigationCases.Add(legacy);
        await db.SaveChangesAsync();
        var queue = new FetchWorkQueue(db);
        var runs = new RefreshRunService(db, queue);
        var start = await runs.StartAsync(requestId, default);

        // What an agent leaves behind: a verified projection that says "disposed", a verified link, and its item done.
        var caseId = await db.Set<ModuleCase>().Where(c => c.LegacyLitigationCaseId == legacy.LitigationCaseId).Select(c => c.CaseId).SingleAsync();
        await new LiveProjectionStore(db).PublishAsync(new ObservationInput(caseId, ObservationSource.DrtPortal, ObservationOutcome.Found, true, DateTime.UtcNow,
            new ParsedCaseSnapshot("DISPOSED", null, "Debts Recovery Tribunal Delhi (DRT 1)", null, null, null, null, null, null, 2)), default);
        await new RequestLinkStore(db).UpsertAsync(requestId, caseId, new PartyMatchResult(PartyMatchOutcome.Verified, "exact", "RUN TEST COMPANY", null), "respondent", false, null, default);
        await db.Set<ModuleFetchWorkItem>().Where(w => w.RunId == start.RunId).ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, (int)FetchWorkStatus.Succeeded));
        var versionBefore = await RequestLiveData.ReadAsync(db, requestId, default);

        Assert.True(await runs.CompleteIfDoneAsync(start.RunId, requestId, default));

        await using var check = Ctx();
        Assert.Equal("DISPOSED", (await check.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == legacy.LitigationCaseId)).CaseStatus);   // published, not left waiting for a batch
        Assert.True(await RequestLiveData.ReadAsync(check, requestId, default) > versionBefore);
        Assert.NotNull((await new RefreshRunService(check, new FetchWorkQueue(check)).ProgressAsync(start.RunId, default))!.FinishedUtc);
        Assert.False(await new RefreshRunService(check, new FetchWorkQueue(check)).CompleteIfDoneAsync(start.RunId, requestId, default));   // once only
    }
}
