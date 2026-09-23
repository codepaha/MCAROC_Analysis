using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #265's verification bar: real SQL Server, real parallelism. Each test pins a fake clock to
/// its own random far-future date, so its (Kind, DayKey) counters are private to it even though every test
/// shares one database, and uses fresh scope keys.</summary>
public sealed class PaidCallAdmissionTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private readonly FakeTime _time = new(new DateTimeOffset(2100 + Random.Shared.Next(0, 800), 1, 1, 6, 0, 0, TimeSpan.Zero)
        .AddDays(Random.Shared.Next(0, 360)));
    private readonly PipelineOptions _options = new();

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    private PaidCallAdmissionService Service(AppDbContext db) =>
        new(db, new StaticOptionsMonitor(_options), _time, NullLogger<PaidCallAdmissionService>.Instance);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Fifty_parallel_admissions_on_one_scope_have_exactly_one_winner()
    {
        _options.Caps.LitigationSearchPerDay = 1000;
        var requestId = await SeedRequestAsync();
        var scope = NewScope("search");

        var results = await RunInParallel(50, _ => Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId));

        Assert.Single(results, r => r.Admitted);
        Assert.All(results.Where(r => !r.Admitted), r => Assert.Equal(AdmissionDenial.InFlight, r.Denial));
        Assert.Equal(1, await UsedAsync(PaidCallKind.LitigationSearch));
        Assert.Equal(1, await CountAdmissionsAsync(scope));
    }

    [Fact]
    public async Task Cap_k_with_n_parallel_auto_admissions_admits_exactly_k_and_manual_at_cap_still_counts()
    {
        _options.Caps.LitigationSearchPerDay = 3;
        var requestId = await SeedRequestAsync();

        var results = await RunInParallel(20, _ => Admit(PaidCallKind.LitigationSearch, NewScope("search"), PaidCallTrigger.Auto, requestId));

        Assert.Equal(3, results.Count(r => r.Admitted));
        Assert.All(results.Where(r => !r.Admitted), r => Assert.Equal(AdmissionDenial.CostCapReached, r.Denial));
        Assert.Equal(3, await UsedAsync(PaidCallKind.LitigationSearch));

        var manual = await Admit(PaidCallKind.LitigationSearch, NewScope("search"), PaidCallTrigger.Manual, requestId);
        Assert.True(manual.Admitted);
        Assert.Equal(4, await UsedAsync(PaidCallKind.LitigationSearch)); // counted, never blocked
    }

    [Fact]
    public async Task Real_spend_caps_default_to_zero_so_auto_is_inert_but_manual_works()
    {
        var requestId = await SeedRequestAsync();

        var auto = await Admit(PaidCallKind.LitigationAnalysis, NewScope("analysis"), PaidCallTrigger.Auto, requestId);
        var unlock = await Admit(PaidCallKind.ReferenceUnlock, NewScope("unlock"), PaidCallTrigger.Auto, requestId);
        var manual = await Admit(PaidCallKind.LitigationAnalysis, NewScope("analysis"), PaidCallTrigger.Manual, requestId);

        Assert.Equal(AdmissionDenial.CostCapReached, auto.Denial);
        Assert.Equal(AdmissionDenial.CostCapReached, unlock.Denial);
        Assert.True(manual.Admitted);
    }

    [Fact]
    public async Task Crash_between_reserve_and_call_is_released_after_the_ttl_and_restores_the_counter()
    {
        _options.Caps.LitigationSearchPerDay = 5;
        var requestId = await SeedRequestAsync();
        var scope = NewScope("search");
        var admitted = await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId);
        Assert.Equal(1, await UsedAsync(PaidCallKind.LitigationSearch));

        // Inside the TTL a never-linked reservation is left alone.
        Assert.Equal(AdmissionResolution.StillReserved, await ResolveAsync(admitted.AdmissionId!.Value));

        _time.Advance(TimeSpan.FromMinutes(_options.ReservationTtlMinutes + 1));
        Assert.Equal(AdmissionResolution.Released, await ResolveAsync(admitted.AdmissionId.Value));

        Assert.Equal(0, await UsedAsync(PaidCallKind.LitigationSearch));
        var s = await ScopeAsync(PaidCallKind.LitigationSearch, scope);
        Assert.Null(s.ActiveAdmissionId);
        Assert.Null(s.LastCommittedUtc); // a release never starts a cooldown
        Assert.True((await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId)).Admitted);
    }

    [Fact]
    public async Task Attempted_call_with_unknown_outcome_is_committed_and_never_auto_retried()
    {
        _options.Caps.LitigationSearchPerDay = 5;
        var requestId = await SeedRequestAsync();
        var scope = NewScope("search");
        var admitted = await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId);

        // The register path sets RegistrationAttemptedUtc before calling BPR; the process then dies before a
        // vendor job id comes back — the exact "may have been bought" state.
        var attemptedUtc = _time.GetUtcNow().UtcDateTime.AddSeconds(5);
        var jobId = await SeedSearchJobAsync(requestId, LitigationSearchJobStatus.Registering, attemptedUtc);
        await using (var db = CreateContext())
            await Service(db).SetReferenceAsync(admitted.AdmissionId!.Value, jobId, CancellationToken.None);

        Assert.Equal(AdmissionResolution.Committed, await ResolveAsync(admitted.AdmissionId.Value));
        Assert.Equal(1, await UsedAsync(PaidCallKind.LitigationSearch)); // commit never gives the slot back
        var s = await ScopeAsync(PaidCallKind.LitigationSearch, scope);
        Assert.Null(s.ActiveAdmissionId);
        Assert.Equal(attemptedUtc, s.LastCommittedUtc!.Value, TimeSpan.FromMilliseconds(1));

        var retry = await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId);
        Assert.Equal(AdmissionDenial.Fresh, retry.Denial);
        Assert.True((await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Manual, requestId)).Admitted);
    }

    [Fact]
    public async Task Unlinked_reservation_whose_job_was_attempted_is_committed_not_released()
    {
        _options.Caps.LitigationSearchPerDay = 5;
        var requestId = await SeedRequestAsync();
        var admitted = await Admit(PaidCallKind.LitigationSearch, NewScope("search"), PaidCallTrigger.Auto, requestId);
        // Crash after the job was created and registration attempted, but before SetReferenceAsync ran.
        await SeedSearchJobAsync(requestId, LitigationSearchJobStatus.Failed, _time.GetUtcNow().UtcDateTime.AddSeconds(1));

        _time.Advance(TimeSpan.FromMinutes(_options.ReservationTtlMinutes + 1));

        Assert.Equal(AdmissionResolution.Committed, await ResolveAsync(admitted.AdmissionId!.Value));
        Assert.Equal(1, await UsedAsync(PaidCallKind.LitigationSearch));
    }

    [Fact]
    public async Task Rolling_window_is_not_reset_by_a_day_boundary()
    {
        _options.Caps.LitigationSearchPerDay = 5;
        var requestId = await SeedRequestAsync();
        var scope = NewScope("search");

        // 23:59 IST on some day.
        var localDay = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), Ist).Date;
        _time.Set(new DateTimeOffset(localDay.AddHours(23).AddMinutes(59), Ist.GetUtcOffset(localDay)));
        var first = await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId);
        await using (var db = CreateContext())
            Assert.True(await Service(db).CommitAsync(first.AdmissionId!.Value, _time.GetUtcNow().UtcDateTime, CancellationToken.None));

        // Two minutes later it's a new DayKey — a fixed-bucket design would admit here.
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(PaidCallAdmissionService.DayKeyFor(first.ReservedUtc, "Asia/Kolkata"),
            PaidCallAdmissionService.DayKeyFor(_time.GetUtcNow().UtcDateTime, "Asia/Kolkata"));
        Assert.Equal(AdmissionDenial.Fresh, (await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId)).Denial);

        _time.Advance(TimeSpan.FromDays(7));
        Assert.True((await Admit(PaidCallKind.LitigationSearch, scope, PaidCallTrigger.Auto, requestId)).Admitted);
    }

    [Fact]
    public async Task Release_returns_the_slot_to_the_admissions_own_day()
    {
        _options.Caps.LitigationSearchPerDay = 5;
        var requestId = await SeedRequestAsync();
        var admitted = await Admit(PaidCallKind.LitigationSearch, NewScope("search"), PaidCallTrigger.Auto, requestId);
        var day1 = PaidCallAdmissionService.DayKeyFor(admitted.ReservedUtc, "Asia/Kolkata");

        _time.Advance(TimeSpan.FromDays(1));
        await Admit(PaidCallKind.LitigationSearch, NewScope("search"), PaidCallTrigger.Auto, requestId);
        await using (var db = CreateContext())
            Assert.True(await Service(db).ReleaseAsync(admitted.AdmissionId!.Value, CancellationToken.None));

        Assert.Equal(0, await UsedAsync(PaidCallKind.LitigationSearch, day1));
        Assert.Equal(1, await UsedAsync(PaidCallKind.LitigationSearch)); // today untouched
    }

    [Fact]
    public async Task Resolution_is_idempotent()
    {
        _options.Caps.LitigationSearchPerDay = 5;
        var requestId = await SeedRequestAsync();
        var admitted = await Admit(PaidCallKind.LitigationSearch, NewScope("search"), PaidCallTrigger.Auto, requestId);

        var releases = await RunInParallel(10, async _ =>
        {
            await using var db = CreateContext();
            return await Service(db).ReleaseAsync(admitted.AdmissionId!.Value, CancellationToken.None);
        });

        Assert.Single(releases, r => r);
        Assert.Equal(0, await UsedAsync(PaidCallKind.LitigationSearch)); // decremented once, not ten times
    }

    [Fact]
    public async Task Analysis_commits_once_the_run_is_claimed_and_is_never_auto_admitted_again()
    {
        _options.Caps.LitigationAnalysisPerDay = 5;
        var requestId = await SeedRequestAsync();
        var scope = NewScope("analysis");
        var admitted = await Admit(PaidCallKind.LitigationAnalysis, scope, PaidCallTrigger.Auto, requestId);
        var runId = await SeedAnalysisRunAsync(requestId, attemptCount: 0, LitigationAiAnalysisRunStatus.Pending);
        await using (var db = CreateContext())
            await Service(db).SetReferenceAsync(admitted.AdmissionId!.Value, runId, CancellationToken.None);

        Assert.Equal(AdmissionResolution.StillReserved, await ResolveAsync(admitted.AdmissionId.Value)); // queued, no call yet

        await using (var db = CreateContext())
            await db.LitigationAiAnalysisRuns.Where(r => r.LitigationAiAnalysisRunId == runId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AttemptCount, 1)
                .SetProperty(r => r.Status, LitigationAiAnalysisRunStatus.InProgress)
                .SetProperty(r => r.StartedUtc, _time.GetUtcNow().UtcDateTime));
        Assert.Equal(AdmissionResolution.Committed, await ResolveAsync(admitted.AdmissionId.Value));

        _time.Advance(TimeSpan.FromDays(3650));
        Assert.Equal(AdmissionDenial.Fresh, (await Admit(PaidCallKind.LitigationAnalysis, scope, PaidCallTrigger.Auto, requestId)).Denial);
    }

    [Fact]
    public void Search_scope_key_ignores_keyword_order_and_case()
    {
        var a = PaidCallScopeKeys.LitigationSearch("L45200MH1995PLC093041", ["Lodha Developers", "MACROTECH"]);
        var b = PaidCallScopeKeys.LitigationSearch("L45200MH1995PLC093041", ["macrotech ", "LODHA DEVELOPERS"]);
        Assert.Equal(a, b);
        Assert.StartsWith("search|L45200MH1995PLC093041|", a);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<AdmissionResult> Admit(PaidCallKind kind, string scope, PaidCallTrigger trigger, long requestId)
    {
        await using var db = CreateContext();
        return await Service(db).TryAdmitAsync(new PaidCallAdmissionRequest(kind, scope, trigger, requestId), CancellationToken.None);
    }

    private async Task<AdmissionResolution> ResolveAsync(long id)
    {
        await using var db = CreateContext();
        return await Service(db).ResolveAsync(id, CancellationToken.None);
    }

    private static async Task<T[]> RunInParallel<T>(int n, Func<int, Task<T>> body)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () => { gate.Wait(); return await body(i); })).ToArray();
        gate.Set();
        return await Task.WhenAll(tasks);
    }

    private static string NewScope(string prefix) => $"{prefix}|test|{Guid.NewGuid():N}";

    private async Task<int> UsedAsync(PaidCallKind kind, DateOnly? day = null)
    {
        var dayKey = day ?? PaidCallAdmissionService.DayKeyFor(_time.GetUtcNow().UtcDateTime, "Asia/Kolkata");
        await using var db = CreateContext();
        return await db.SpendCounters.Where(c => c.Kind == kind && c.DayKey == dayKey).Select(c => c.Used).SingleOrDefaultAsync();
    }

    private static async Task<SpendScope> ScopeAsync(PaidCallKind kind, string scope)
    {
        await using var db = CreateContext();
        return await db.SpendScopes.AsNoTracking().SingleAsync(s => s.Kind == kind && s.ScopeKey == scope);
    }

    private static async Task<int> CountAdmissionsAsync(string scope)
    {
        await using var db = CreateContext();
        return await db.PaidCallAdmissions.CountAsync(a => a.ScopeKey == scope);
    }

    private static async Task<long> SeedRequestAsync()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "PCA" + Guid.NewGuid().ToString("N")[..7], ClientName = "Admission Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Admission Test Company",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"PCA-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    private static async Task<long> SeedSearchJobAsync(long requestId, LitigationSearchJobStatus status, DateTime? attemptedUtc)
    {
        await using var db = CreateContext();
        var job = new LitigationSearchJob
        {
            RequestId = requestId, KeywordsJson = "[]", Status = status, CreatedUtc = DateTime.UtcNow,
            RegistrationAttemptedUtc = attemptedUtc
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        return job.LitigationSearchJobId;
    }

    private static async Task<long> SeedAnalysisRunAsync(long requestId, int attemptCount, LitigationAiAnalysisRunStatus status)
    {
        await using var db = CreateContext();
        var run = new LitigationAiAnalysisRun
        {
            RequestId = requestId, RunNumber = 1, Status = status, AttemptCount = attemptCount,
            ModelId = "test-model", PromptVersion = "v1", CreatedUtc = DateTime.UtcNow
        };
        db.LitigationAiAnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        return run.LitigationAiAnalysisRunId;
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public void Set(DateTimeOffset at) => _now = at.ToUniversalTime();
    }

    private sealed class StaticOptionsMonitor(PipelineOptions value) : IOptionsMonitor<PipelineOptions>
    {
        public PipelineOptions CurrentValue => value;
        public PipelineOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PipelineOptions, string?> listener) => null;
    }
}
