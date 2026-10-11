using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LiveProjectionStoreTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime T0 = new(2026, 10, 11, 6, 0, 0, DateTimeKind.Utc);

    private static async Task<long> NewCaseAsync()
    {
        await using var db = Ctx();
        return (await new LitigationModuleStore(db).EnsureCaseForLegacyAsync(Math.Abs(Guid.NewGuid().GetHashCode()) + 2_000_000L, default)).CaseId;
    }

    private static ParsedCaseSnapshot Snap(string status = "PENDING", string? next = "20-10-2026", int hearings = 10, string? court = "DRT Mumbai 1") =>
        new(status, null, court, "05-12-2022", "01-09-2026", next, null, null, null, hearings);

    private static ObservationInput Found(long caseId, ParsedCaseSnapshot s, int minutes = 0, ObservationSource source = ObservationSource.DrtPortal, bool verified = true) =>
        new(caseId, source, ObservationOutcome.Found, verified, T0.AddMinutes(minutes), s);

    [Fact]
    public async Task A_first_verified_observation_creates_the_projection_and_an_observation_row()
    {
        var id = await NewCaseAsync();
        await using var db = Ctx();
        var r = await new LiveProjectionStore(db).PublishAsync(Found(id, Snap()), default);

        Assert.Equal(ProjectionOutcome.Applied, r.Outcome);
        await using var verify = Ctx();
        var live = await verify.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == id);
        Assert.Equal(("PENDING", "DRT Mumbai 1", "20-10-2026", 1L), (live.Status, live.Court, live.NextHearingDate, live.Version));
        Assert.Equal(r.ObservationId, live.LastObservationId);
        var obs = await verify.Set<ModuleCaseObservation>().AsNoTracking().SingleAsync(o => o.CaseId == id);
        Assert.Equal(("Applied", true), (obs.AppliedAs, obs.Verified));
        Assert.Equal("PENDING", JsonSerializer.Deserialize<ParsedCaseSnapshot>(obs.SnapshotJson!)!.Status);
    }

    [Theory]
    [InlineData(ObservationOutcome.NotFound)]
    [InlineData(ObservationOutcome.Error)]
    [InlineData(ObservationOutcome.IdentityRejected)]
    public async Task An_observation_that_cannot_change_the_projection_is_still_recorded(ObservationOutcome outcome)
    {
        var id = await NewCaseAsync();
        await using var db = Ctx();
        var store = new LiveProjectionStore(db);
        await store.PublishAsync(Found(id, Snap()), default);
        var r = await store.PublishAsync(new ObservationInput(id, ObservationSource.DrtPortal, outcome, false, T0.AddMinutes(5), null), default);

        Assert.Equal(ProjectionOutcome.IgnoredNotVerified, r.Outcome);
        await using var verify = Ctx();
        Assert.Equal(2, await verify.Set<ModuleCaseObservation>().CountAsync(o => o.CaseId == id));
        Assert.Equal("PENDING", (await verify.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == id)).Status);
        Assert.Null((await verify.Set<ModuleCaseObservation>().AsNoTracking().OrderBy(o => o.ObservationId).LastAsync(o => o.CaseId == id)).SnapshotJson);
    }

    [Fact]
    public async Task A_late_observation_is_recorded_and_ignored()
    {
        var id = await NewCaseAsync();
        await using var db = Ctx();
        var store = new LiveProjectionStore(db);
        await store.PublishAsync(Found(id, Snap(next: "25-10-2026"), minutes: 10), default);
        var r = await store.PublishAsync(Found(id, Snap(next: "20-10-2026"), minutes: 0), default);

        Assert.Equal(ProjectionOutcome.IgnoredLate, r.Outcome);
        await using var verify = Ctx();
        Assert.Equal("25-10-2026", (await verify.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == id)).NextHearingDate);
        Assert.Equal("IgnoredLate", (await verify.Set<ModuleCaseObservation>().AsNoTracking().OrderBy(o => o.ObservationId).LastAsync(o => o.CaseId == id)).AppliedAs);
    }

    [Fact]
    public async Task A_held_change_survives_in_the_row_and_the_second_agreeing_observation_applies_it()
    {
        var id = await NewCaseAsync();
        await using (var db = Ctx()) await new LiveProjectionStore(db).PublishAsync(Found(id, Snap("DISPOSAL", null)), default);
        await using (var db = Ctx()) Assert.Equal(ProjectionOutcome.HeldStatusRegression, (await new LiveProjectionStore(db).PublishAsync(Found(id, Snap("PENDING"), 5), default)).Outcome);

        await using var mid = Ctx();
        var held = await mid.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == id);
        Assert.Equal(("DISPOSAL", "status:PENDING"), (held.Status, held.HeldChange));

        await using (var db = Ctx()) Assert.Equal(ProjectionOutcome.Applied, (await new LiveProjectionStore(db).PublishAsync(Found(id, Snap("PENDING"), 10), default)).Outcome);
        await using var after = Ctx();
        var live = await after.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == id);
        Assert.Equal(("PENDING", null), (live.Status, live.HeldChange));
    }

    [Fact]
    public async Task Confirmed_empty_marks_round_trip_through_the_row_for_the_eligibility_rule()
    {
        var id = await NewCaseAsync();
        await using var db = Ctx();
        var store = new LiveProjectionStore(db);
        await store.PublishAsync(Found(id, Snap(next: "20-10-2026")), default);
        await store.PublishAsync(Found(id, Snap(next: null), 5), default);

        var state = await new LiveProjectionStore(Ctx()).GetStateAsync(id, default);
        var marks = LiveProjectionEngine.ConfirmedEmptyMarks(state!);
        Assert.Equal(T0.AddMinutes(5), marks[LiveProjectionEngine.FNextHearing]);
        Assert.Null(state!.NextHearingDate);
    }

    [Fact]
    public async Task Concurrent_publications_from_several_agents_all_land_and_the_projection_is_consistent()
    {
        var id = await NewCaseAsync();
        // Eight agents publish observations at increasing times at once. Each is recorded; the projection ends on the newest; nothing throws.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = Ctx();
            return await new LiveProjectionStore(db).PublishAsync(Found(id, Snap(next: $"{10 + i}-11-2026", hearings: 10 + i), minutes: i), default);
        }));

        await using var verify = Ctx();
        Assert.Equal(8, await verify.Set<ModuleCaseObservation>().CountAsync(o => o.CaseId == id));
        var live = await verify.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == id);
        Assert.Equal(T0.AddMinutes(7), live.LastRetrievedUtc);
        Assert.Equal("17-11-2026", live.NextHearingDate);
        Assert.All(results, r => Assert.True(r.Outcome is ProjectionOutcome.Applied or ProjectionOutcome.IgnoredLate));
    }

    [Fact]
    public async Task A_vendor_observation_after_a_portal_one_does_not_change_portal_owned_fields_in_the_stored_row()
    {
        var id = await NewCaseAsync();
        await using var db = Ctx();
        var store = new LiveProjectionStore(db);
        await store.PublishAsync(Found(id, Snap("DISPOSAL", null, court: "DRT Mumbai 1")), default);
        await store.PublishAsync(Found(id, Snap("PENDING", "30-12-2026", court: "Some Vendor Spelling"), 5, ObservationSource.Vendor), default);

        await using var verify = Ctx();
        var live = await verify.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == id);
        Assert.Equal(("DISPOSAL", "DRT Mumbai 1", null), (live.Status, live.Court, live.NextHearingDate));
    }

    [Fact]
    public async Task An_observation_is_written_once_complete_and_is_never_updated()
    {
        var id = await NewCaseAsync();
        await using var db = Ctx();
        var store = new LiveProjectionStore(db);
        var r = await store.PublishAsync(Found(id, Snap()), default);
        // A later publication for the same case must leave the earlier observation row byte-for-byte as it was.
        await using var read1 = Ctx();
        var before = await read1.Set<ModuleCaseObservation>().AsNoTracking().SingleAsync(o => o.ObservationId == r.ObservationId);
        await store.PublishAsync(Found(id, Snap("DISPOSAL", null), 5), default);
        await store.PublishAsync(Found(id, Snap(next: "01-12-2026"), 3), default);   // late: recorded, ignored
        await using var read2 = Ctx();
        var after = await read2.Set<ModuleCaseObservation>().AsNoTracking().SingleAsync(o => o.ObservationId == r.ObservationId);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal("Applied", after.AppliedAs);          // the outcome was known when the row was inserted
    }

    [Fact]
    public void The_store_never_issues_an_update_against_the_observation_table()
    {
        // Append-only is structural: the store's source has no update of an observation (guards against the pattern being reintroduced).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MCAROC_Analysis", "MCAROC_Analysis.csproj"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "MCAROC_Analysis", "Services", "LitigationModule", "Refresh", "LiveProjectionStore.cs"));
        Assert.DoesNotContain("ExecuteUpdateAsync", source);
        Assert.DoesNotContain("IsModified", source);
        Assert.DoesNotContain("Set<ModuleCaseObservation>().Where", source);
    }
}
