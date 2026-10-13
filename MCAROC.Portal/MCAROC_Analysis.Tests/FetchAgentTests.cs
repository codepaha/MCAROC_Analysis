using System.Net;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Fetching;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class FetchAgentTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
        // These tests own the queue, the host traffic state and the hosts' permits.
        await db.Set<ModuleFetchWorkItem>().ExecuteDeleteAsync();
        await db.Set<ModuleHostTrafficState>().ExecuteDeleteAsync();
        await db.OperationalSlotLeases.Where(s => s.SlotType.StartsWith("LitigationHost_")).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        public DateTime Utc { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Utc, DateTimeKind.Utc));
    }

    /// <summary>A DRT portal that answers from a script, one response per call, and counts the calls.</summary>
    private sealed class ScriptedDrt : IDrtClient
    {
        public Queue<DrtCaseLookupResult> Script { get; } = new();
        public int Calls { get; private set; }
        /// <summary>Runs during the call, before the scripted answer comes back: what other agents do meanwhile.</summary>
        public Func<CancellationToken, Task>? During { get; set; }
        public string CurrentExit => "direct";

        public async Task<DrtCaseLookupResult> GetCaseByNumberAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default)
        {
            Calls++;
            if (During is not null) await During(ct);
            return Script.Dequeue();
        }
        public Task<DrtCaseLookupResult> GetRcTrcCaseAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            GetCaseByNumberAsync(tribunalId, caseTypeId, caseNo, caseYear, ct);
        public Task<IReadOnlyList<DrtTribunal>> GetTribunalsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DrtTribunal>>([]);
        public Task<IReadOnlyList<DrtCaseType>> GetCaseTypesAsync(int tribunalId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DrtCaseType>>([]);
        public Task<IReadOnlyList<DrtPartySearchResult>> SearchByPartyNameAsync(int tribunalId, string partyName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DrtPartySearchResult>>([]);
        public Task<DrtCaseLookupResult> GetCaseByDiaryAsync(int tribunalId, string diaryNo, int diaryYear, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DrtOrder>> GetDailyOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DrtOrder>>([]);
        public Task<IReadOnlyList<DrtOrder>> GetFinalOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DrtOrder>>([]);
        public Task<DrtOrderDownloadResult> DownloadOrderAsync(string orderUrl, long maxBytes = 20971520, CancellationToken ct = default) => Task.FromResult(DrtOrderDownloadResult.NotAvailable());
    }

    private sealed class NoConsumer : IConsumerClient
    {
        public string CurrentExit => "direct";
        public ConsumerProxyRotator Rotator => new([]);
        public Task<ConsumerCaseLookupResult> GetCaseStatusAsync(string caseNumber, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsumerCompanySearchResult> SearchByCompanyAsync(int commissionTypeId, int commissionId, string keyword, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsumerOrderDownloadResult> GetDailyOrderPdfAsync(long filingReferenceNumber, DateOnly dateOfHearing, int orderTypeId = 1, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static readonly HostTrafficOptions Traffic = new() { DrtMaxConcurrency = 2, FailureThreshold = 3 };

    private static FetchAgent Agent(AppDbContext db, ScriptedDrt drt, TestClock clock, FetchQueueOptions? queueOptions = null) =>
        new(db, new FetchWorkQueue(db, queueOptions, clock), Gate(db, clock), drt, new NoConsumer(), NullLogger<FetchAgent>.Instance, clock);

    private static HostTrafficGate Gate(AppDbContext db, TestClock clock) =>
        new(db, new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance), Traffic, clock);

    private static int _no = 500;

    /// <summary>A registry case with a gate-minted key at DRT Delhi 1, queued for a status refresh.</summary>
    private static async Task<(long CaseId, long ItemId, string No)> QueuedCaseAsync(TestClock clock, string? key = "")
    {
        var no = Interlocked.Increment(ref _no).ToString();
        await using var db = Ctx();
        var c = new ModuleCase { CaseKey = key == "" ? $"DRT|1|OA|{no}|2022" : key, CreatedUtc = clock.Utc };
        db.Set<ModuleCase>().Add(c);
        await db.SaveChangesAsync();
        var item = await new FetchWorkQueue(db, null, clock).EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, c.CaseId, null, null, 0, default);
        return (c.CaseId, item.WorkItemId, no);
    }

    private static DrtCaseDetails Details(string caseNumber, string status = "PENDING")
    {
        var parts = CaseNumberNormaliser.Parse(new CaseNumberInput(caseNumber));
        return new DrtCaseDetails(
            TribunalId: 1, CaseTypeId: 1, CaseType: parts.TypeCode, CaseTypeCode: parts.TypeCode, CaseNo: parts.No, CaseYear: parts.Year, CaseNumber: parts,
            DiaryNo: null, DiaryYear: null, FilingNo: null, DateOfFiling: new DateOnly(2022, 1, 1), Status: status, StatusParts: CaseStatusVocabulary.Map(status), StatusCode: status,
            DateOfDisposal: null, DisposalNature: null, NextListingDate: new DateOnly(2026, 11, 20), NextListingPurpose: "Hearing", CourtNo: "1", CourtName: "Debts Recovery Tribunal Delhi (DRT 1)",
            MainCaseCaseNo: null, SuitAmount: null, PetitionerName: "State Bank of India", RespondentName: "Acme Limited", PetitionerAddress: null, RespondentAddress: null,
            PetitionerAdvocate: null, RespondentAdvocate: null, AdditionalPetitioner: null, AdditionalRespondent: null, Hearings: [], IaDetails: null);
    }

    private static async Task<ModuleFetchWorkItem> ItemAsync(long id) { await using var db = Ctx(); return await db.Set<ModuleFetchWorkItem>().AsNoTracking().SingleAsync(w => w.WorkItemId == id); }

    private static async Task<List<ModuleCaseObservation>> ObservationsAsync(long caseId) { await using var db = Ctx(); return await db.Set<ModuleCaseObservation>().AsNoTracking().Where(o => o.CaseId == caseId).ToListAsync(); }

    private static async Task<AgentTurn> TurnAsync(ScriptedDrt drt, TestClock clock)
    {
        await using var db = Ctx();
        return await Agent(db, drt, clock).RunOnceAsync(FetchHost.Drt, "agent-1", default);
    }

    [Fact]
    public void The_lookup_comes_from_the_case_key_only()
    {
        var drt = PortalLookup.FromCaseKey("DRT|1|OA|501|2022", out _)!;
        Assert.Equal(FetchHost.Drt, drt.Host);
        Assert.Equal(1, drt.VenueId);
        Assert.Equal(("OA", "501", 2022), (drt.Parts.TypeCode, drt.Parts.No, drt.Parts.Year));
        Assert.Equal(FetchHost.Consumer, PortalLookup.FromCaseKey("CONSUMER|497|CC|599|2017", out _)!.Host);
        Assert.True(PortalLookup.FromCaseKey("DRAT|100|APPEAL|5|2020", out _)!.IsDrat);

        Assert.Null(PortalLookup.FromCaseKey(null, out var noKey));
        Assert.Contains("no verified case key", noKey);
        Assert.Null(PortalLookup.FromCaseKey("HC|DEL|WP|1|2020", out var hc));                 // no portal for High Courts
        Assert.Contains("HC", hc);
        Assert.Null(PortalLookup.FromCaseKey("DRT|Delhi|OA|1|2020", out _));                     // a court known by name only is never looked up
    }

    [Fact]
    public async Task A_verified_answer_updates_the_projection_and_completes_the_item()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, no) = await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Found(Details($"OA/{no}/2022"), exitAddress: "exit-a"));

        Assert.Equal(AgentTurn.Done, await TurnAsync(drt, clock));

        var item = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Succeeded, item.Status);
        Assert.Equal("Updated", item.LastOutcome);
        var obs = Assert.Single(await ObservationsAsync(caseId));
        Assert.True(obs.Verified);
        Assert.Equal((int)ObservationOutcome.Found, obs.Outcome);
        await using var db = Ctx();
        var live = await db.Set<ModuleCaseLive>().AsNoTracking().SingleAsync(l => l.CaseId == caseId);
        Assert.Equal("PENDING", live.Status?.ToUpperInvariant());
        Assert.NotNull((await Gate(db, clock).SnapshotAsync(FetchHost.Drt, default)).LastSuccessUtc);   // and the host's health heard about it
    }

    [Fact]
    public async Task Not_found_is_final_only_after_a_second_empty_answer_through_another_exit()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, _) = await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.NotFoundCandidate(exitAddress: "http://user:secret@proxy-a:8080"));
        drt.Script.Enqueue(DrtCaseLookupResult.NotFoundCandidate(exitAddress: "http://user:secret@proxy-b:8080"));

        Assert.Equal(AgentTurn.Deferred, await TurnAsync(drt, clock));
        var waiting = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Queued, waiting.Status);
        Assert.Equal(0, waiting.AttemptCount);                                                   // one empty answer costs nothing
        Assert.DoesNotContain("secret", waiting.LastError);                                      // the exit is kept as a hash, never as an address
        Assert.DoesNotContain("proxy", waiting.LastError);
        Assert.Empty(await ObservationsAsync(caseId));                                           // and is not on record as "not found"

        Assert.Equal(AgentTurn.Idle, await TurnAsync(drt, clock));                               // not before the re-check delay
        clock.Utc += FetchAgent.NotFoundRecheckDelay + TimeSpan.FromSeconds(1);
        Assert.Equal(AgentTurn.Done, await TurnAsync(drt, clock));
        Assert.Equal("NotFound", (await ItemAsync(itemId)).LastOutcome);
        var obs = Assert.Single(await ObservationsAsync(caseId));
        Assert.Equal((int)ObservationOutcome.NotFound, obs.Outcome);
        Assert.False(obs.Verified);
    }

    [Fact]
    public async Task Two_empty_answers_through_the_same_exit_are_not_confirmation()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, _) = await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.NotFoundCandidate(exitAddress: "proxy-a"));
        drt.Script.Enqueue(DrtCaseLookupResult.NotFoundCandidate(exitAddress: "proxy-a"));

        await TurnAsync(drt, clock);
        clock.Utc += FetchAgent.NotFoundRecheckDelay + TimeSpan.FromSeconds(1);
        Assert.Equal(AgentTurn.Retrying, await TurnAsync(drt, clock));

        var item = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Queued, item.Status);
        Assert.Equal(1, item.AttemptCount);                                                      // costs an attempt, so it cannot loop for ever
        Assert.Equal(FetchAgent.NotFoundCandidate, item.LastOutcome);                            // the first exit is still remembered for the next try
        Assert.Empty(await ObservationsAsync(caseId));
    }

    [Fact]
    public async Task An_unavailable_portal_costs_an_attempt_and_counts_toward_the_breaker()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, _) = await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Unavailable("503 after retries", statusCode: HttpStatusCode.ServiceUnavailable));

        Assert.Equal(AgentTurn.Retrying, await TurnAsync(drt, clock));
        var item = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Queued, item.Status);
        Assert.Equal(1, item.AttemptCount);
        Assert.NotNull(item.NextAttemptUtc);                                                     // cooling down
        Assert.Equal((int)ObservationOutcome.Error, Assert.Single(await ObservationsAsync(caseId)).Outcome);
        await using var db = Ctx();
        Assert.Equal(1, (await Gate(db, clock).SnapshotAsync(FetchHost.Drt, default)).ConsecutiveFailures);
    }

    [Fact]
    public async Task A_permanent_refusal_fails_the_item_without_retries()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (_, itemId, _) = await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Error("400 validation", statusCode: HttpStatusCode.BadRequest));

        Assert.Equal(AgentTurn.Failed, await TurnAsync(drt, clock));
        var item = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Failed, item.Status);
        Assert.Equal("PortalError", item.LastOutcome);
        await using var db = Ctx();
        Assert.Equal(0, (await Gate(db, clock).SnapshotAsync(FetchHost.Drt, default)).ConsecutiveFailures);   // the portal answered: it is up
    }

    [Fact]
    public async Task Another_case_returned_for_the_number_is_recorded_and_never_applied()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, _) = await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Found(Details("OA/9999/2019"), exitAddress: "exit-a"));

        Assert.Equal(AgentTurn.Done, await TurnAsync(drt, clock));
        Assert.Equal("IdentityRejected", (await ItemAsync(itemId)).LastOutcome);
        var obs = Assert.Single(await ObservationsAsync(caseId));
        Assert.Equal((int)ObservationOutcome.IdentityRejected, obs.Outcome);
        Assert.False(obs.Verified);
        await using var db = Ctx();
        Assert.False(await db.Set<ModuleCaseLive>().AnyAsync(l => l.CaseId == caseId && l.Status != null));
    }

    [Fact]
    public async Task While_the_breaker_is_open_items_are_deferred_without_calling_or_using_attempts()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (_, itemId, _) = await QueuedCaseAsync(clock);
        await using (var db = Ctx())
        {
            var gate = Gate(db, clock);
            var permit = new HostPermit(FetchHost.Drt, "other-agent", false);
            for (var i = 0; i < 3; i++) await gate.ReportAsync(permit, HostCallOutcome.Unavailable, "503", null, default);
        }
        var drt = new ScriptedDrt();

        Assert.Equal(AgentTurn.Deferred, await TurnAsync(drt, clock));
        Assert.Equal(0, drt.Calls);
        var item = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Queued, item.Status);
        Assert.Equal(0, item.AttemptCount);
        Assert.True(item.NextAttemptUtc >= clock.Utc + TimeSpan.FromMinutes(4));                 // back when the breaker may let a probe through
    }

    [Fact]
    public async Task A_case_that_cannot_be_looked_up_fails_with_the_reason_and_no_call()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (_, itemId, _) = await QueuedCaseAsync(clock, key: null);
        var drt = new ScriptedDrt();

        Assert.Equal(AgentTurn.Failed, await TurnAsync(drt, clock));
        Assert.Equal(0, drt.Calls);
        var item = await ItemAsync(itemId);
        Assert.Equal("NoLookup", item.LastOutcome);
        Assert.Contains("no verified case key", item.LastError);
    }

    [Fact]
    public void Host_health_is_read_from_the_response()
    {
        Assert.Equal(HostCallOutcome.Throttled, FetchAgent.Classify(true, HttpStatusCode.TooManyRequests));
        Assert.Equal(HostCallOutcome.Unavailable, FetchAgent.Classify(true, null));               // timeout or refused connection
        Assert.Equal(HostCallOutcome.Unavailable, FetchAgent.Classify(false, HttpStatusCode.BadGateway));
        Assert.Equal(HostCallOutcome.Answered, FetchAgent.Classify(false, HttpStatusCode.OK));
        Assert.Equal(HostCallOutcome.Answered, FetchAgent.Classify(false, HttpStatusCode.BadRequest));
        Assert.Null(FetchAgent.ExitHash(null));
        Assert.Equal(FetchAgent.ExitHash("Proxy-A "), FetchAgent.ExitHash("proxy-a"));
        Assert.NotEqual(FetchAgent.ExitHash("proxy-a"), FetchAgent.ExitHash("proxy-b"));
    }

    [Fact]
    public async Task A_result_is_not_written_when_another_agent_took_the_item_over_during_the_call()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, no) = await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Found(Details($"OA/{no}/2022"), exitAddress: "exit-a"));
        drt.During = async _ =>
        {
            // The call outlived the lease: recovery returned the item and another agent claimed it.
            clock.Utc += TimeSpan.FromMinutes(6);
            await using var other = Ctx();
            var q = new FetchWorkQueue(other, null, clock);
            Assert.NotNull(await q.ClaimNextAsync(FetchHost.Drt, "agent-2", default));
        };

        Assert.Equal(AgentTurn.LeaseLost, await TurnAsync(drt, clock));

        Assert.Empty(await ObservationsAsync(caseId));                                           // nothing stale on record
        await using var db = Ctx();
        Assert.False(await db.Set<ModuleCaseLive>().AnyAsync(l => l.CaseId == caseId));
        var item = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Claimed, item.Status);                                 // still agent-2's
        Assert.Equal("agent-2", item.LeaseOwner);
    }

    [Fact]
    public async Task A_long_call_keeps_its_lease_alive()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, no) = await QueuedCaseAsync(clock);
        var shortLease = new FetchQueueOptions { LeaseDuration = TimeSpan.FromMilliseconds(300) };   // renewed every 100 ms of real time
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Found(Details($"OA/{no}/2022"), exitAddress: "exit-a"));
        var recovered = -1;
        drt.During = async _ =>
        {
            clock.Utc += TimeSpan.FromSeconds(1);                                                  // the original lease has run out...
            await Task.Delay(TimeSpan.FromMilliseconds(400));                                       // ...but the heartbeat renews it meanwhile
            await using var other = Ctx();
            recovered = await new FetchWorkQueue(other, shortLease, clock).RecoverExpiredLeasesAsync(FetchHost.Drt, default);
        };

        await using (var db = Ctx())
            Assert.Equal(AgentTurn.Done, await Agent(db, drt, clock, shortLease).RunOnceAsync(FetchHost.Drt, "agent-1", default));

        Assert.Equal(0, recovered);
        Assert.Equal("Updated", (await ItemAsync(itemId)).LastOutcome);
        Assert.True(Assert.Single(await ObservationsAsync(caseId)).Verified);
    }

    [Fact]
    public async Task Retry_After_from_the_portal_keeps_every_agent_away()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await QueuedCaseAsync(clock);
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Unavailable("429 after retries", statusCode: HttpStatusCode.TooManyRequests) with { RetryAfter = TimeSpan.FromMinutes(30) });

        Assert.Equal(AgentTurn.Retrying, await TurnAsync(drt, clock));

        await using var db = Ctx();
        var snap = await Gate(db, clock).SnapshotAsync(FetchHost.Drt, default);
        Assert.True(snap.BreakerOpen);                                                           // one 429, below the threshold, but the portal said when to come back
        Assert.Equal(clock.Utc + TimeSpan.FromMinutes(30), snap.OpenUntilUtc);
    }

    [Fact]
    public async Task A_call_whose_host_permit_lapses_is_stopped_and_the_item_put_back_without_an_attempt()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var (caseId, itemId, no) = await QueuedCaseAsync(clock);
        var shortLease = new FetchQueueOptions { LeaseDuration = TimeSpan.FromMilliseconds(300) };   // heartbeat every 100 ms of real time
        var drt = new ScriptedDrt();
        drt.Script.Enqueue(DrtCaseLookupResult.Found(Details($"OA/{no}/2022"), exitAddress: "exit-a"));
        var cancelled = false;
        drt.During = async ct =>
        {
            await using (var other = Ctx())
                await other.OperationalSlotLeases.Where(l => l.SlotType == "LitigationHost_Drt").ExecuteDeleteAsync();   // the permit is gone
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        };

        await using (var db = Ctx())
            Assert.Equal(AgentTurn.Deferred, await Agent(db, drt, clock, shortLease).RunOnceAsync(FetchHost.Drt, "agent-1", default));

        Assert.True(cancelled);                                                                  // the call did not go on outside the cap
        var item = await ItemAsync(itemId);
        Assert.Equal((int)FetchWorkStatus.Queued, item.Status);
        Assert.Equal(0, item.AttemptCount);
        Assert.Empty(await ObservationsAsync(caseId));
    }
}
