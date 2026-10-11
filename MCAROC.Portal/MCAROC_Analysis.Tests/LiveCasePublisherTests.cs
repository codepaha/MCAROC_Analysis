using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LiveCasePublisherTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime T0 = new(2026, 10, 11, 6, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(long RequestId, long LegacyCaseId, long ModuleCaseId);

    private static async Task<Fixture> SeedAsync(string status = "PENDING", string? next = "01-12-2026", string court = "Vendor Court Name")
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "PB" + Guid.NewGuid().ToString("N")[..8], ClientName = "Publisher Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Publisher Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"PB-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var legacy = new LitigationCase
        {
            RequestId = request.RequestId, CaseNumber = "413", CaseType = "OA", CaseYear = "2022", Court = court, CaseStatus = status, FilingDate = "05-12-2022",
            LastHearingDate = "01-08-2026", NextHearingDate = next, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(legacy);
        await db.SaveChangesAsync();
        var moduleCase = await new LitigationModuleStore(db).EnsureCaseForLegacyAsync(legacy.LitigationCaseId, default);
        return new Fixture(request.RequestId, legacy.LitigationCaseId, moduleCase.CaseId);
    }

    private static ParsedCaseSnapshot Snap(string status = "PENDING", string? next = "20-10-2026", string court = "DRT Mumbai 1", string? last = "15-9-2026", string? decision = null) =>
        new(status, null, court, "5-12-2022", last, next, decision, null, null, 6);

    private static async Task ObserveAsync(Fixture f, ParsedCaseSnapshot snapshot, int minutes = 0)
    {
        await using var db = Ctx();
        await new LiveProjectionStore(db).PublishAsync(
            new ObservationInput(f.ModuleCaseId, ObservationSource.DrtPortal, ObservationOutcome.Found, true, T0.AddMinutes(minutes), snapshot), default);
    }

    private static async Task LinkAsync(Fixture f, PartyMatchOutcome outcome)
    {
        await using var db = Ctx();
        await new RequestLinkStore(db).UpsertAsync(f.RequestId, f.ModuleCaseId, new PartyMatchResult(outcome, outcome == PartyMatchOutcome.Verified ? "exact" : "extra-words", "PUBLISHER TEST COMPANY LIMITED", null), "respondent", false, null, default);
    }

    private static async Task<LitigationCase> ReadLegacyAsync(Fixture f)
    {
        await using var db = Ctx();
        return await db.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == f.LegacyCaseId);
    }

    private static async Task<long> VersionAsync(Fixture f) { await using var db = Ctx(); return await RequestLiveData.ReadAsync(db, f.RequestId, default); }

    [Fact]
    public async Task A_verified_link_publishes_the_portals_values_into_the_requests_columns_and_bumps_the_version()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap());
        await LinkAsync(f, PartyMatchOutcome.Verified);

        await using var db = Ctx();
        var result = await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default);

        Assert.Equal(PublishToRequestOutcome.Published, result.Outcome);
        var legacy = await ReadLegacyAsync(f);
        Assert.Equal(("DRT Mumbai 1", "20-10-2026", "15-09-2026", "05-12-2022"), (legacy.Court, legacy.NextHearingDate, legacy.LastHearingDate, legacy.FilingDate));   // vendor date convention dd-MM-yyyy
        Assert.Equal(1, await VersionAsync(f));
    }

    [Theory]
    [InlineData(PartyMatchOutcome.Rejected)]
    [InlineData(PartyMatchOutcome.Ambiguous)]
    public async Task A_link_that_is_not_verified_publishes_nothing_and_does_not_bump_the_version(PartyMatchOutcome outcome)
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap());
        await LinkAsync(f, outcome);

        await using var db = Ctx();
        Assert.Equal(PublishToRequestOutcome.LinkNotVerified, (await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default)).Outcome);
        Assert.Equal("Vendor Court Name", (await ReadLegacyAsync(f)).Court);
        Assert.Equal(0, await VersionAsync(f));
    }

    [Fact]
    public async Task A_case_with_no_link_publishes_nothing()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap());
        await using var db = Ctx();
        Assert.Equal(PublishToRequestOutcome.NotLinked, (await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default)).Outcome);
        Assert.Equal(0, await VersionAsync(f));
    }

    [Fact]
    public async Task A_link_for_one_request_does_not_publish_into_another_request()
    {
        var f = await SeedAsync();
        var other = await SeedAsync();
        await ObserveAsync(f, Snap());
        await LinkAsync(f, PartyMatchOutcome.Verified);
        await using var db = Ctx();
        // The other request has its own legacy row and no link to this module case.
        Assert.Equal(PublishToRequestOutcome.NotLinked, (await LiveCasePublisher.PublishAsync(db, other.RequestId, other.LegacyCaseId, default)).Outcome);
    }

    [Fact]
    public async Task A_second_publication_with_nothing_new_changes_nothing_and_does_not_bump_again()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap());
        await LinkAsync(f, PartyMatchOutcome.Verified);
        await using (var db = Ctx()) await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default);
        await using (var db = Ctx()) Assert.Equal(PublishToRequestOutcome.NothingToChange, (await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default)).Outcome);
        Assert.Equal(1, await VersionAsync(f));
    }

    [Fact]
    public async Task A_portal_confirmed_empty_next_hearing_replaces_a_stale_vendor_date()
    {
        var f = await SeedAsync(next: "01-12-2026");
        await ObserveAsync(f, Snap(next: null));
        await LinkAsync(f, PartyMatchOutcome.Verified);
        await using var db = Ctx();
        var result = await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default);
        Assert.Contains(LivePortalOwnership.NextHearingDate, result.ChangedColumns);
        Assert.Null((await ReadLegacyAsync(f)).NextHearingDate);
    }

    [Fact]
    public async Task A_missing_portal_value_never_blanks_a_column()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap(last: null));            // the portal gave no last hearing
        await LinkAsync(f, PartyMatchOutcome.Verified);
        await using var db = Ctx();
        await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default);
        Assert.Equal("01-08-2026", (await ReadLegacyAsync(f)).LastHearingDate);
    }

    [Fact]
    public async Task Ownership_lists_the_portal_sourced_columns_only_for_a_verified_link()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap());
        await using (var db = Ctx()) Assert.Empty(await LivePortalOwnership.OwnedColumnsAsync(db, f.RequestId, f.LegacyCaseId, default));   // no link yet
        await LinkAsync(f, PartyMatchOutcome.Rejected);
        await using (var db = Ctx()) Assert.Empty(await LivePortalOwnership.OwnedColumnsAsync(db, f.RequestId, f.LegacyCaseId, default));
        await LinkAsync(f, PartyMatchOutcome.Verified);
        await using (var db = Ctx())
        {
            var owned = await LivePortalOwnership.OwnedColumnsAsync(db, f.RequestId, f.LegacyCaseId, default);
            Assert.Contains(LivePortalOwnership.CaseStatus, owned);
            Assert.Contains(LivePortalOwnership.NextHearingDate, owned);
            Assert.Contains(LivePortalOwnership.Court, owned);
        }
    }

    [Fact]
    public async Task The_link_store_upserts_one_row_per_request_and_case_and_the_latest_verdict_wins()
    {
        var f = await SeedAsync();
        await LinkAsync(f, PartyMatchOutcome.Ambiguous);
        await LinkAsync(f, PartyMatchOutcome.Verified);
        await using var db = Ctx();
        Assert.Equal(1, await db.Set<ModuleRequestLink>().CountAsync(l => l.RequestId == f.RequestId && l.CaseId == f.ModuleCaseId));
        Assert.Equal(PartyMatchOutcome.Verified, await new RequestLinkStore(db).GetVerdictAsync(f.RequestId, f.ModuleCaseId, default));
    }

    // ---- concurrency: the decision and the write happen under the per-case lock ----

    [Fact]
    public async Task A_publisher_that_waits_for_the_lock_publishes_the_projection_as_it_is_when_it_gets_the_lock_not_as_it_was_when_it_started()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap(court: "Old Portal Court"));
        await LinkAsync(f, PartyMatchOutcome.Verified);

        await using var holderDb = Ctx();
        await using var holder = await holderDb.Database.BeginTransactionAsync();
        await CaseLock.AcquireAsync(holderDb, f.LegacyCaseId, default);

        var waiting = Task.Run(async () => { await using var db = Ctx(); return await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default); });
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);                                        // blocked on the lock

        await ObserveAsync(f, Snap(court: "Newer Portal Court"), minutes: 5);     // a newer observation arrives while it waits
        await holder.CommitAsync();
        var result = await waiting;

        Assert.Equal(PublishToRequestOutcome.Published, result.Outcome);
        Assert.Equal("Newer Portal Court", (await ReadLegacyAsync(f)).Court);     // never the older court it could have read first
    }

    [Fact]
    public async Task A_link_rejected_while_a_publisher_waits_stops_the_publication()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap(court: "DRT Mumbai 1"));
        await LinkAsync(f, PartyMatchOutcome.Verified);

        await using var holderDb = Ctx();
        await using var holder = await holderDb.Database.BeginTransactionAsync();
        await CaseLock.AcquireAsync(holderDb, f.LegacyCaseId, default);

        var waiting = Task.Run(async () => { await using var db = Ctx(); return await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default); });
        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);

        await new RequestLinkStore(holderDb).UpsertAsync(f.RequestId, f.ModuleCaseId, new PartyMatchResult(PartyMatchOutcome.Rejected, "extra-words", "SOME OTHER SOCIETY", null), null, false, null, default);
        await holder.CommitAsync();

        Assert.Equal(PublishToRequestOutcome.LinkNotVerified, (await waiting).Outcome);
        Assert.Equal("Vendor Court Name", (await ReadLegacyAsync(f)).Court);
        Assert.Equal(0, await VersionAsync(f));
    }

    [Fact]
    public async Task A_link_change_waits_for_a_publication_in_progress_instead_of_interleaving_with_it()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap());
        await LinkAsync(f, PartyMatchOutcome.Verified);

        await using var holderDb = Ctx();
        await using var holder = await holderDb.Database.BeginTransactionAsync();
        await CaseLock.AcquireAsync(holderDb, f.LegacyCaseId, default);

        var changing = Task.Run(async () => { await using var db = Ctx(); await new RequestLinkStore(db).UpsertAsync(f.RequestId, f.ModuleCaseId, new PartyMatchResult(PartyMatchOutcome.Rejected, "no-match", null, null), null, false, null, default); });
        await Task.Delay(500);
        Assert.False(changing.IsCompleted);
        await holder.CommitAsync();
        await changing;
        await using var db2 = Ctx();
        Assert.Equal(PartyMatchOutcome.Rejected, await new RequestLinkStore(db2).GetVerdictAsync(f.RequestId, f.ModuleCaseId, default));
    }

    [Fact]
    public async Task A_projection_older_than_what_the_request_already_received_is_not_published_over_it()
    {
        var f = await SeedAsync();
        await ObserveAsync(f, Snap(court: "Court Two"), minutes: 5);
        await LinkAsync(f, PartyMatchOutcome.Verified);
        await using (var db = Ctx()) await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default);

        // Pretend the request had already received a later observation than the projection now holds.
        await using (var db = Ctx())
            await db.Set<ModuleRequestLink>().Where(l => l.RequestId == f.RequestId).ExecuteUpdateAsync(s => s.SetProperty(l => l.PublishedObservationId, long.MaxValue));
        await using (var db = Ctx())
        {
            await db.LitigationCases.Where(c => c.LitigationCaseId == f.LegacyCaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Court, "Hand Edited"));
            Assert.Equal(PublishToRequestOutcome.NothingToChange, (await LiveCasePublisher.PublishAsync(db, f.RequestId, f.LegacyCaseId, default)).Outcome);
        }
        Assert.Equal("Hand Edited", (await ReadLegacyAsync(f)).Court);
    }
}
