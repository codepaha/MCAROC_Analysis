using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

public class PipelineNeedsAttentionBoardTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly string _serial = Random.Shared.Next(100_000, 999_999).ToString();
    private readonly List<long> _requests = [];

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static PipelineReconciler CreateReconciler(AppDbContext db) =>
        new(db, new PipelineSnapshotReader(db, new ConfigurationBuilder().Build(), Options.Create(new BprLitigationOptions()), Options.Create(new ReferenceToolOptions())),
            TimeProvider.System, NullLogger<PipelineReconciler>.Instance);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        if (_requests.Count > 0)
        {
            var reqIds = _requests;
            await db.PipelineEvents.Where(e => db.PipelineRuns.Any(r => reqIds.Contains(r.RequestId) && r.PipelineRunId == e.PipelineRunId)).ExecuteDeleteAsync();
            await db.PipelineStageStates.Where(s => db.PipelineRuns.Any(r => reqIds.Contains(r.RequestId) && r.PipelineRunId == s.PipelineRunId)).ExecuteDeleteAsync();
            await db.PipelineRuns.Where(r => reqIds.Contains(r.RequestId)).ExecuteDeleteAsync();
            await db.AutoFetchJobs.Where(j => reqIds.Contains(j.RequestId)).ExecuteDeleteAsync();
            await db.LitigationSearchJobs.Where(j => reqIds.Contains(j.RequestId)).ExecuteDeleteAsync();
            await db.AnalysisRuns.Where(a => reqIds.Contains(a.RequestId)).ExecuteDeleteAsync();
            await db.Requests.Where(r => reqIds.Contains(r.RequestId)).ExecuteUpdateAsync(s => s.SetProperty(r => r.LatestCompletedIngestionRunId, (long?)null));
            await db.IngestionRuns.Where(i => reqIds.Contains(i.RequestId)).ExecuteDeleteAsync();
            await db.Requests.Where(r => reqIds.Contains(r.RequestId)).ExecuteDeleteAsync();
        }
    }

    private async Task<(McaRequest Request, PipelineRun Run)> SeedRunAsync(AppDbContext db, PipelineOutcome outcome = PipelineOutcome.NeedsAttention, string? clientName = null)
    {
        Client? client = null;
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            client = await db.Clients.FirstOrDefaultAsync(c => c.ClientName == clientName);
            if (client is null)
            {
                client = new Client { ClientCode = "C" + _serial, ClientName = clientName, CreatedDate = DateTime.UtcNow };
                db.Clients.Add(client);
                await db.SaveChangesAsync();
            }
        }

        var req = new McaRequest
        {
            RequestNumber = "REQ-" + _serial + "-" + Guid.NewGuid().ToString("N")[..4],
            CompanyName = "Test Company " + _serial,
            Cin = "U72200MH2020PTC" + _serial,
            ClientId = client?.ClientId ?? 1,
            RequestStatus = RequestStatus.AiAnalysisFailed,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        _requests.Add(req.RequestId);

        var run = new PipelineRun
        {
            RequestId = req.RequestId,
            Outcome = outcome,
            Trigger = PipelineRunTrigger.AutoFetch,
            CorrelationId = "corr-" + Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync();

        return (req, run);
    }

    private static PipelineController CreateController(AppDbContext db, PipelineReconciler? reconciler = null, AutoFetchJobService? autoFetchJobs = null, AutoFetchQueue? autoFetchQueue = null, AnalysisOrchestrator? analysisOrchestrator = null)
    {
        var optsMonitor = new TestOptionsMonitor<PipelineOptions>(new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce });
        var controller = new PipelineController(
            db,
            optsMonitor,
            autoFetchJobs: autoFetchJobs,
            autoFetchQueue: autoFetchQueue,
            reconciler: reconciler,
            analysisOrchestrator: analysisOrchestrator);

        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new DictionaryTempDataProvider());
        return controller;
    }

    [Fact]
    public async Task Index_PopulatesClientName_AndAttentionStages()
    {
        await using var db = CreateContext();
        var clientName = "BoardTestClient " + _serial;
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.NeedsAttention, clientName);

        db.PipelineStageStates.Add(new PipelineStageState
        {
            PipelineRunId = run.PipelineRunId,
            Stage = PipelineStage.Litigation,
            State = PipelineStageStateKind.NeedsAttention,
            ReasonCode = "LITIGATION_SOURCE_UNUSABLE",
            ReasonDetail = "Source failed permanently",
            UpdatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.Index(PipelineOutcome.NeedsAttention, null, null, CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PipelineBoardViewModel>(viewResult.Model);
        var row = Assert.Single(model.Rows, r => r.PipelineRunId == run.PipelineRunId);

        Assert.Equal(clientName, row.ClientName);
        Assert.True(row.HasAttention);
        var attention = Assert.Single(row.AttentionStages);
        Assert.Equal("Litigation", attention.Stage);
        Assert.Equal("LITIGATION_SOURCE_UNUSABLE", attention.ReasonCode);
    }

    [Fact]
    public async Task RetryStage_RequiresReason()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db);

        var controller = CreateController(db);
        var result = await controller.RetryStage(run.PipelineRunId, PipelineStage.Fetch, reason: "   ", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Pipeline", redirect.Url);
        Assert.Equal("A reason is required to retry a stage.", controller.TempData["PipelineError"]);
    }

    [Theory]
    [InlineData(PipelineStage.Resolve)]
    [InlineData(PipelineStage.Unlock)]
    [InlineData(PipelineStage.Refresh)]
    [InlineData(PipelineStage.Fetch)]
    [InlineData(PipelineStage.Ingest)]
    [InlineData(PipelineStage.Analysis)]
    [InlineData(PipelineStage.CalcAssurance)]
    [InlineData(PipelineStage.Dossier)]
    public async Task SkipStage_RejectsCoreStages(PipelineStage coreStage)
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db);

        var controller = CreateController(db);
        var result = await controller.SkipStage(run.PipelineRunId, coreStage, reason: "Cannot do this", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Pipeline", redirect.Url);
        Assert.Equal($"Core stage '{coreStage}' cannot be skipped.", controller.TempData["PipelineError"]);

        var stageRow = await db.PipelineStageStates.FirstOrDefaultAsync(s => s.PipelineRunId == run.PipelineRunId && s.Stage == coreStage);
        Assert.True(stageRow is null || stageRow.State != PipelineStageStateKind.Skipped);
    }

    [Fact]
    public async Task SkipStage_EnrichmentStage_SucceedsAndUnblocksCompletion()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.NeedsAttention);

        // Core stages are completed
        var ingestion = new IngestionRun { RequestId = req.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(ingestion);
        await db.SaveChangesAsync();

        req.LatestCompletedIngestionRunId = ingestion.IngestionRunId;
        req.RequestStatus = RequestStatus.AnalysisCompleted;
        db.AnalysisRuns.Add(new AnalysisRun
        {
            RequestId = req.RequestId,
            IngestionRunId = ingestion.IngestionRunId,
            RunNumber = 1,
            Status = AnalysisRunStatus.Completed,
            StartedDate = DateTime.UtcNow,
            CompletedDate = DateTime.UtcNow
        });

        // Failed litigation job puts Litigation in NeedsAttention
        db.LitigationSearchJobs.Add(new LitigationSearchJob
        {
            RequestId = req.RequestId,
            Status = LitigationSearchJobStatus.Failed,
            FailureReason = "Vendor permanent failure",
            CreatedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        var reconciler = CreateReconciler(db);
        var controller = CreateController(db, reconciler: reconciler);

        var result = await controller.SkipStage(run.PipelineRunId, PipelineStage.Litigation, reason: "Skipping vendor issue", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("Stage Litigation skipped.", controller.TempData["PipelineOk"]);

        // Verify stage state is Skipped (Warning)
        var stageRow = await db.PipelineStageStates.SingleAsync(s => s.PipelineRunId == run.PipelineRunId && s.Stage == PipelineStage.Litigation);
        Assert.Equal(PipelineStageStateKind.Skipped, stageRow.State);
        Assert.Equal(PipelineStageSkipKind.Warning, stageRow.SkipKind);
        Assert.Equal("MANUAL_SKIP", stageRow.ReasonCode);
        Assert.Equal("Skipping vendor issue", stageRow.ReasonDetail);

        // Verify PipelineEvent written
        var evt = await db.PipelineEvents.SingleAsync(e => e.PipelineRunId == run.PipelineRunId && e.Stage == PipelineStage.Litigation && e.Action == "ManualSkipped");
        Assert.Equal("MANUAL_SKIP", evt.ReasonCode);

        // After reconciliation, the run outcome should be CompleteWithWarnings
        var updatedRun = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == run.PipelineRunId);
        Assert.Equal(PipelineOutcome.CompleteWithWarnings, updatedRun.Outcome);
        Assert.NotNull(updatedRun.CompletedUtc);
    }

    [Fact]
    public async Task CancelRun_CancelsRunRequestAndActiveStages()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.NeedsAttention);

        // Add 1 Succeeded stage, 1 Running stage, and 1 NeedsAttention stage
        db.PipelineStageStates.AddRange(
            new PipelineStageState { PipelineRunId = run.PipelineRunId, Stage = PipelineStage.Resolve, State = PipelineStageStateKind.Succeeded, UpdatedUtc = DateTime.UtcNow },
            new PipelineStageState { PipelineRunId = run.PipelineRunId, Stage = PipelineStage.Fetch, State = PipelineStageStateKind.Running, UpdatedUtc = DateTime.UtcNow },
            new PipelineStageState { PipelineRunId = run.PipelineRunId, Stage = PipelineStage.Analysis, State = PipelineStageStateKind.NeedsAttention, ReasonCode = "ANALYSIS_FAILED", UpdatedUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.CancelRun(run.PipelineRunId, reason: "Client requested termination", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("Pipeline run cancelled.", controller.TempData["PipelineOk"]);

        var updatedRun = await db.PipelineRuns.Include(r => r.Request).SingleAsync(r => r.PipelineRunId == run.PipelineRunId);
        Assert.Equal(PipelineOutcome.Cancelled, updatedRun.Outcome);
        Assert.NotNull(updatedRun.CompletedUtc);
        Assert.Equal(RequestStatus.Cancelled, updatedRun.Request!.RequestStatus);

        var resolveStage = await db.PipelineStageStates.SingleAsync(s => s.PipelineRunId == run.PipelineRunId && s.Stage == PipelineStage.Resolve);
        Assert.Equal(PipelineStageStateKind.Succeeded, resolveStage.State);

        var fetchStage = await db.PipelineStageStates.SingleAsync(s => s.PipelineRunId == run.PipelineRunId && s.Stage == PipelineStage.Fetch);
        Assert.Equal(PipelineStageStateKind.Cancelled, fetchStage.State);
        Assert.Equal("MANUAL_CANCEL", fetchStage.ReasonCode);
        Assert.Equal("Client requested termination", fetchStage.ReasonDetail);

        var analysisStage = await db.PipelineStageStates.SingleAsync(s => s.PipelineRunId == run.PipelineRunId && s.Stage == PipelineStage.Analysis);
        Assert.Equal(PipelineStageStateKind.Cancelled, analysisStage.State);
        Assert.Equal("MANUAL_CANCEL", analysisStage.ReasonCode);

        var evt = await db.PipelineEvents.SingleAsync(e => e.PipelineRunId == run.PipelineRunId && e.Action == "Cancelled");
        Assert.Equal("MANUAL_CANCEL", evt.ReasonCode);
    }

    private sealed class DictionaryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestOptionsMonitor<T>(T current) : IOptionsMonitor<T>
    {
        public T CurrentValue => current;
        public T Get(string? name) => current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
