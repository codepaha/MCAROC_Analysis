using System.Collections.Concurrent;
using System.Data.Common;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>#264 verification against the real SQLEXPRESS test database: lease-fenced reconciles (no double
/// processing), Observe mode writing nothing outside the pipeline tables, CoreReadyUtc never withdrawn, and
/// adoption respecting the switch and the cutoff.</summary>
public sealed class PipelineReconcilerTests : IAsyncLifetime
{
    private static readonly HashSet<string> PipelineTables = ["PipelineRuns", "PipelineStageStates", "PipelineEvents"];
    private static readonly PipelineOptions Enabled = new() { Enabled = true };

    private static AppDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).AddInterceptors(interceptors).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static PipelineReconciler Reconciler(AppDbContext db) =>
        new(db, new PipelineSnapshotReader(db, new ConfigurationBuilder().Build(), Options.Create(new BprLitigationOptions())),
            TimeProvider.System, NullLogger<PipelineReconciler>.Instance);

    private static PipelineAdopter Adopter(AppDbContext db, PipelineOptions? options = null) =>
        new(db, new StaticOptionsMonitor(options ?? Enabled), TimeProvider.System, NullLogger<PipelineAdopter>.Instance);

    private static async Task<long> NewRunAsync(long requestId)
    {
        await using var db = CreateContext();
        return (await Adopter(db).EnsureRunAsync(requestId, PipelineRunTrigger.ManualUpload, null, CancellationToken.None))!.Value;
    }

    private static async Task<bool> ReconcileAsync(long runId, params IInterceptor[] interceptors)
    {
        await using var db = CreateContext(interceptors);
        return await Reconciler(db).ReconcileAsync(runId, CancellationToken.None);
    }

    [Fact]
    public async Task A_finished_manual_request_reconciles_to_complete_and_is_not_picked_up_again()
    {
        var requestId = await SeedManualRequestAsync(analysed: true);
        var runId = await NewRunAsync(requestId);

        Assert.True(await ReconcileAsync(runId));

        await using var db = CreateContext();
        var run = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId);
        Assert.Equal(PipelineOutcome.Complete, run.Outcome);
        Assert.NotNull(run.CoreReadyUtc);
        Assert.NotNull(run.CompletedUtc);
        Assert.Null(run.ReconcileLeaseToken);
        Assert.Equal(Enum.GetValues<PipelineStage>().Length, await db.PipelineStageStates.CountAsync(s => s.PipelineRunId == runId));
        Assert.Equal(Enum.GetValues<PipelineStage>().Length, await db.PipelineEvents.CountAsync(e => e.PipelineRunId == runId));

        Assert.False(await ReconcileAsync(runId)); // no longer live
        Assert.DoesNotContain(runId, await Reconciler(db).SelectDueRunIdsAsync(10_000, CancellationToken.None));
    }

    [Fact]
    public async Task Reconciling_unchanged_facts_again_writes_no_new_events()
    {
        var requestId = await SeedManualRequestAsync(analysed: false); // stays InProgress: analysis never ran
        var runId = await NewRunAsync(requestId);
        Assert.True(await ReconcileAsync(runId));
        await using var db = CreateContext();
        var events = await db.PipelineEvents.CountAsync(e => e.PipelineRunId == runId);

        Assert.True(await ReconcileAsync(runId));

        Assert.Equal(events, await db.PipelineEvents.CountAsync(e => e.PipelineRunId == runId));
        Assert.Equal(PipelineOutcome.InProgress, (await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId)).Outcome);
    }

    [Fact]
    public async Task Core_ready_time_is_kept_when_an_enrichment_stage_later_needs_attention()
    {
        var requestId = await SeedManualRequestAsync(analysed: true);
        long searchJobId;
        await using (var seed = CreateContext())
        {
            var job = new LitigationSearchJob { RequestId = requestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Polling, CreatedUtc = DateTime.UtcNow };
            seed.LitigationSearchJobs.Add(job);
            await seed.SaveChangesAsync();
            searchJobId = job.LitigationSearchJobId;
        }
        var runId = await NewRunAsync(requestId);

        Assert.True(await ReconcileAsync(runId));
        await using var db = CreateContext();
        var first = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId);
        Assert.Equal(PipelineOutcome.CoreReady, first.Outcome);

        await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == searchJobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, LitigationSearchJobStatus.Failed).SetProperty(j => j.FailureReason, "BPR timeout"));
        Assert.True(await ReconcileAsync(runId));

        var second = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId);
        Assert.Equal(PipelineOutcome.NeedsAttention, second.Outcome);
        Assert.Equal(first.CoreReadyUtc, second.CoreReadyUtc);
        var litigation = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Litigation);
        Assert.Equal("LITIGATION_SEARCH_FAILED", litigation.ReasonCode);
        Assert.Equal("BPR timeout", litigation.ReasonDetail);
        Assert.True(await db.PipelineEvents.AnyAsync(e => e.PipelineRunId == runId && e.Stage == PipelineStage.Litigation && e.Action == "Observed:NeedsAttention"));
    }

    [Fact]
    public async Task Ten_racing_reconcilers_process_a_run_exactly_once()
    {
        var requestId = await SeedManualRequestAsync(analysed: false);
        var runId = await NewRunAsync(requestId);

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () => { gate.Wait(); return await ReconcileAsync(runId); })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r);
        await using var db = CreateContext();
        Assert.Equal(Enum.GetValues<PipelineStage>().Length, await db.PipelineEvents.CountAsync(e => e.PipelineRunId == runId));
    }

    [Fact]
    public async Task Observe_mode_writes_nothing_outside_the_three_pipeline_tables()
    {
        var requestId = await SeedManualRequestAsync(analysed: true);
        var runId = await NewRunAsync(requestId);
        var recorder = new WriteRecorder();

        Assert.True(await ReconcileAsync(runId, recorder));

        await using var db = CreateContext();
        var allTables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().ToHashSet();
        Assert.NotEmpty(recorder.Writes);
        foreach (var sql in recorder.Writes)
        {
            var touched = allTables.Where(t => sql.Contains($"[{t}]", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(touched);
            Assert.All(touched, t => Assert.Contains(t, PipelineTables));
        }
    }

    [Fact]
    public async Task Adoption_is_inert_while_disabled()
    {
        var requestId = await SeedManualRequestAsync(analysed: false);
        await using var db = CreateContext();

        Assert.Null(await Adopter(db, new PipelineOptions()).EnsureRunAsync(requestId, PipelineRunTrigger.ManualUpload, null, CancellationToken.None));
        Assert.Equal(0, await Adopter(db, new PipelineOptions { AdoptAfterUtc = DateTime.MinValue }).AdoptSweepAsync(50, CancellationToken.None));
        Assert.False(await db.PipelineRuns.AnyAsync(r => r.RequestId == requestId));
    }

    [Fact]
    public async Task Sweep_adopts_only_requests_on_or_after_the_cutoff_and_never_re_adopts_a_finished_one()
    {
        // Far-future creation dates keep every other test's requests (created "now") out of this sweep.
        var cutoff = new DateTime(2990 + Random.Shared.Next(0, 9), 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = await SeedManualRequestAsync(analysed: false, createdUtc: cutoff.AddDays(-1));
        var after = await SeedManualRequestAsync(analysed: false, createdUtc: cutoff.AddDays(1));
        var options = new PipelineOptions { Enabled = true, AdoptAfterUtc = cutoff };

        await using var db = CreateContext();
        await Adopter(db, options).AdoptSweepAsync(50, CancellationToken.None);

        Assert.False(await db.PipelineRuns.AnyAsync(r => r.RequestId == before));
        var run = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.RequestId == after);
        Assert.Equal(PipelineRunTrigger.Adopted, run.Trigger);

        await db.PipelineRuns.Where(r => r.PipelineRunId == run.PipelineRunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Outcome, PipelineOutcome.Complete));
        await Adopter(db, options).AdoptSweepAsync(50, CancellationToken.None);
        Assert.Equal(1, await db.PipelineRuns.CountAsync(r => r.RequestId == after));
    }

    [Fact]
    public async Task Concurrent_adoption_creates_one_live_run()
    {
        var requestId = await SeedManualRequestAsync(analysed: false);

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            await using var db = CreateContext();
            return await Adopter(db).EnsureRunAsync(requestId, PipelineRunTrigger.ManualUpload, null, CancellationToken.None);
        })).ToArray();
        gate.Set();
        var ids = await Task.WhenAll(tasks);

        Assert.Single(ids.Distinct());
        await using var verify = CreateContext();
        Assert.Equal(1, await verify.PipelineRuns.CountAsync(r => r.RequestId == requestId));
    }

    private static async Task<long> SeedManualRequestAsync(bool analysed, DateTime? createdUtc = null)
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "PLR" + Guid.NewGuid().ToString("N")[..7], ClientName = "Pipeline Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Pipeline Test Company",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"PLR-{Guid.NewGuid():N}",
            RequestStatus = analysed ? RequestStatus.AnalysisCompleted : RequestStatus.DataExtracted,
            CreatedDate = createdUtc ?? DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var ingestion = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(ingestion);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = ingestion.IngestionRunId;
        if (analysed)
            db.AnalysisRuns.Add(new AnalysisRun
            {
                RequestId = request.RequestId, IngestionRunId = ingestion.IngestionRunId, RunNumber = 1,
                Status = AnalysisRunStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    /// <summary>Captures the SQL of every command that can modify data.</summary>
    private sealed class WriteRecorder : DbCommandInterceptor
    {
        public ConcurrentBag<string> Writes { get; } = [];

        private void Record(DbCommand command)
        {
            var sql = command.CommandText;
            if (sql.Contains("INSERT ", StringComparison.Ordinal) || sql.Contains("UPDATE ", StringComparison.Ordinal)
                || sql.Contains("DELETE ", StringComparison.Ordinal) || sql.Contains("MERGE ", StringComparison.Ordinal))
                Writes.Add(sql);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Record(command); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Record(command); return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken); }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Record(command); return base.ScalarExecutingAsync(command, eventData, result, cancellationToken); }
    }

    private sealed class StaticOptionsMonitor(PipelineOptions value) : IOptionsMonitor<PipelineOptions>
    {
        public PipelineOptions CurrentValue => value;
        public PipelineOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PipelineOptions, string?> listener) => null;
    }
}
