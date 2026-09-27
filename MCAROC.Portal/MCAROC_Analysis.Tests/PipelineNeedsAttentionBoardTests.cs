using System.Net;
using System.Text;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.Excel;
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
            await db.PaidCallAdmissions.Where(a => reqIds.Contains(a.RequestId)).ExecuteDeleteAsync();
            await db.AutoFetchJobs.Where(j => reqIds.Contains(j.RequestId)).ExecuteDeleteAsync();
            await db.LitigationSearchJobs.Where(j => reqIds.Contains(j.RequestId)).ExecuteDeleteAsync();
            await db.LitigationAiAnalysisRuns.Where(r => reqIds.Contains(r.RequestId)).ExecuteDeleteAsync();
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

    private static PipelineController CreateController(
        AppDbContext db,
        PipelineReconciler? reconciler = null,
        AutoFetchJobService? autoFetchJobs = null,
        AutoFetchQueue? autoFetchQueue = null,
        AnalysisOrchestrator? analysisOrchestrator = null,
        LitigationStartService? litigation = null,
        IOptions<BprLitigationOptions>? bprOptions = null)
    {
        var optsMonitor = new TestOptionsMonitor<PipelineOptions>(new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce });
        var controller = new PipelineController(
            db,
            optsMonitor,
            autoFetchJobs: autoFetchJobs,
            autoFetchQueue: autoFetchQueue,
            reconciler: reconciler,
            analysisOrchestrator: analysisOrchestrator,
            litigation: litigation,
            bprOptions: bprOptions);

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
        Assert.Equal("Pipeline run cancelled; future coordinator actions and queued jobs halted.", controller.TempData["PipelineOk"]);

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

    [Fact]
    public async Task CancelRun_ConcurrentWithReconcile_InterleavedAfterSnapshotRead_RunRemainsCancelledAndNoStagesStart()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.InProgress);

        // Seed successful core stages so Litigation is ready to start
        var ingestion = new IngestionRun
        {
            RequestId = req.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow,
            CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean
        };
        db.IngestionRuns.Add(ingestion);
        await db.SaveChangesAsync();

        req.LatestCompletedIngestionRunId = ingestion.IngestionRunId;
        req.RequestStatus = RequestStatus.AnalysisCompleted;
        db.AnalysisRuns.Add(new AnalysisRun
        {
            RequestId = req.RequestId, IngestionRunId = ingestion.IngestionRunId, RunNumber = 1,
            Status = AnalysisRunStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var bprOptions = new BprLitigationOptions { BaseUrl = "https://bpr.test", Id = "id", SecretKey = "secret" };
        var pipelineOptions = new PipelineOptions
        {
            Enabled = true,
            Mode = PipelineMode.Enforce,
            Enforce = { Litigation = true },
            Policy = { LitigationSearch = true }
        };
        var spyActions = new SpyPipelineActions();

        var interleavedFired = false;
        var snapshotReader = new InterleavingSnapshotReader(
            db, new ConfigurationBuilder().Build(), Options.Create(bprOptions),
            Options.Create(new ReferenceToolOptions()),
            onAfterRead: () =>
            {
                interleavedFired = true;
                // Concurrent cancellation lands while reconciler holds its initial lease and snapshot
                using var cancelDb = CreateContext();
                var cancelController = CreateController(cancelDb);
                var cancelResult = cancelController.CancelRun(run.PipelineRunId, reason: "Concurrent user cancellation", returnUrl: "/Pipeline", CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsType<RedirectResult>(cancelResult);
            });

        var reconciler = new PipelineReconciler(
            db, snapshotReader, TimeProvider.System, NullLogger<PipelineReconciler>.Instance,
            new TestOptionsMonitor<PipelineOptions>(pipelineOptions), spyActions);

        // Reconciler runs: claims lease, reads snapshot, interleaved CancelRun fires, then reconciler enters write path
        var reconcileSucceeded = await reconciler.ReconcileAsync(run.PipelineRunId, CancellationToken.None);

        Assert.True(interleavedFired, "The interleaving hook must have fired");
        Assert.False(reconcileSucceeded, "Reconcile must be fenced out and return false because the run was cancelled");

        // Verify no new stage was started by the reconciler
        Assert.Equal(0, spyActions.LitigationSearchStarts);
        Assert.Equal(0, spyActions.LitigationAnalysisStarts);
        Assert.Equal(0, spyActions.DossierPreRenders);
        Assert.Equal(0, spyActions.RetryFetchCalls);

        // Verify database state: run remains Cancelled, Request is Cancelled, and Cancelled event is recorded
        await using var verifyDb = CreateContext();
        var reloadedRun = await verifyDb.PipelineRuns.AsNoTracking().Include(r => r.Request).SingleAsync(r => r.PipelineRunId == run.PipelineRunId);
        Assert.Equal(PipelineOutcome.Cancelled, reloadedRun.Outcome);
        Assert.NotNull(reloadedRun.CompletedUtc);
        Assert.Equal(RequestStatus.Cancelled, reloadedRun.Request!.RequestStatus);

        var cancelEvent = await verifyDb.PipelineEvents.AsNoTracking().FirstOrDefaultAsync(e => e.PipelineRunId == run.PipelineRunId && e.Action == "Cancelled");
        Assert.NotNull(cancelEvent);
        Assert.Equal("MANUAL_CANCEL", cancelEvent.ReasonCode);
    }

    [Fact]
    public async Task RetryStage_CancelledRun_RejectsAndNeverCallsActions()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.Cancelled);
        req.RequestStatus = RequestStatus.Cancelled;
        await db.SaveChangesAsync();

        var bpr = new BprLitigationOptions { BaseUrl = "https://bpr.test", Id = "id", SecretKey = "secret" };
        var admission = new PaidCallAdmissionService(db, new TestOptionsMonitor<PipelineOptions>(new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce }), TimeProvider.System, NullLogger<PaidCallAdmissionService>.Instance);
        var search = new LitigationSearchJobService(db, null!, new LitigationSearchQueue(), null!, null!, Options.Create(bpr), NullLogger<LitigationSearchJobService>.Instance);
        var analysis = new LitigationAiAnalysisOrchestrator(db, null!, new LitigationAiAnalysisQueue(), Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);
        var litigationService = new LitigationStartService(db, admission, search, new LitigationSearchQueue(), analysis, Options.Create(bpr));

        var controller = CreateController(db, litigation: litigationService, bprOptions: Options.Create(bpr));
        var result = await controller.RetryStage(run.PipelineRunId, PipelineStage.Litigation, reason: "Retry after cancel", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Pipeline", redirect.Url);
        Assert.Equal("Cannot retry a stage on a cancelled pipeline run.", controller.TempData["PipelineError"]);

        // Proves litigation action was never called: no search job, no admission, no event
        Assert.False(await db.LitigationSearchJobs.AnyAsync(j => j.RequestId == req.RequestId));
        Assert.False(await db.PaidCallAdmissions.AnyAsync(a => a.RequestId == req.RequestId));
        Assert.False(await db.PipelineEvents.AnyAsync(e => e.PipelineRunId == run.PipelineRunId && e.Action == "RetryManual"));

        var reloadedRun = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == run.PipelineRunId);
        Assert.Equal(PipelineOutcome.Cancelled, reloadedRun.Outcome);
    }

    [Theory]
    [InlineData(PipelineOutcome.Complete)]
    [InlineData(PipelineOutcome.CompleteWithWarnings)]
    public async Task RetryStage_CompletedRun_Rejects(PipelineOutcome completedOutcome)
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, completedOutcome);

        var controller = CreateController(db);
        var result = await controller.RetryStage(run.PipelineRunId, PipelineStage.Fetch, reason: "Retry completed", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Pipeline", redirect.Url);
        Assert.Equal("Cannot retry a stage on a completed pipeline run.", controller.TempData["PipelineError"]);
    }

    [Theory]
    [InlineData(PipelineOutcome.Cancelled, "Cannot skip a stage on a cancelled pipeline run.")]
    [InlineData(PipelineOutcome.Complete, "Cannot skip a stage on a completed pipeline run.")]
    [InlineData(PipelineOutcome.CompleteWithWarnings, "Cannot skip a stage on a completed pipeline run.")]
    public async Task SkipStage_TerminalRun_Rejects(PipelineOutcome terminalOutcome, string expectedError)
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, terminalOutcome);
        if (terminalOutcome == PipelineOutcome.Cancelled)
        {
            req.RequestStatus = RequestStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        var controller = CreateController(db);
        var result = await controller.SkipStage(run.PipelineRunId, PipelineStage.Litigation, reason: "Skip terminal", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Pipeline", redirect.Url);
        Assert.Equal(expectedError, controller.TempData["PipelineError"]);
    }

    [Theory]
    [InlineData(PipelineOutcome.Cancelled, "Pipeline run is already cancelled.")]
    [InlineData(PipelineOutcome.Complete, "Cannot cancel a completed pipeline run.")]
    [InlineData(PipelineOutcome.CompleteWithWarnings, "Cannot cancel a completed pipeline run.")]
    public async Task CancelRun_TerminalRun_Rejects(PipelineOutcome terminalOutcome, string expectedError)
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, terminalOutcome);
        if (terminalOutcome == PipelineOutcome.Cancelled)
        {
            req.RequestStatus = RequestStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        var controller = CreateController(db);
        var result = await controller.CancelRun(run.PipelineRunId, reason: "Cancel terminal", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Pipeline", redirect.Url);
        Assert.Equal(expectedError, controller.TempData["PipelineError"]);
    }

    [Fact]
    public async Task CancelRun_CancelsQueuedDomainJobs()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.NeedsAttention);

        db.AutoFetchJobs.Add(new AutoFetchJob
        {
            RequestId = req.RequestId,
            Status = AutoFetchJobStatus.Queued,
            CreatedUtc = DateTime.UtcNow
        });
        db.LitigationSearchJobs.Add(new LitigationSearchJob
        {
            RequestId = req.RequestId,
            Status = LitigationSearchJobStatus.Pending,
            CreatedUtc = DateTime.UtcNow
        });
        db.LitigationAiAnalysisRuns.Add(new LitigationAiAnalysisRun
        {
            RequestId = req.RequestId,
            RunNumber = 1,
            Status = LitigationAiAnalysisRunStatus.Pending,
            CreatedUtc = DateTime.UtcNow
        });
        db.AnalysisRuns.Add(new AnalysisRun
        {
            RequestId = req.RequestId,
            RunNumber = 1,
            Status = AnalysisRunStatus.Running,
            StartedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.CancelRun(run.PipelineRunId, reason: "Operator cancelled work", returnUrl: "/Pipeline", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("Pipeline run cancelled; future coordinator actions and queued jobs halted.", controller.TempData["PipelineOk"]);

        await using var verifyDb = CreateContext();
        var autoFetch = await verifyDb.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.RequestId == req.RequestId);
        Assert.Equal(AutoFetchJobStatus.Failed, autoFetch.Status);
        Assert.Equal("Run cancelled: Operator cancelled work", autoFetch.FailureReason);

        var litSearch = await verifyDb.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.RequestId == req.RequestId);
        Assert.Equal(LitigationSearchJobStatus.Failed, litSearch.Status);
        Assert.Equal("Run cancelled: Operator cancelled work", litSearch.FailureReason);

        var litAnalysis = await verifyDb.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.RequestId == req.RequestId);
        Assert.Equal(LitigationAiAnalysisRunStatus.Failed, litAnalysis.Status);
        Assert.Equal("Run cancelled: Operator cancelled work", litAnalysis.FailureReason);

        var analysisRun = await verifyDb.AnalysisRuns.AsNoTracking().SingleAsync(a => a.RequestId == req.RequestId);
        Assert.Equal(AnalysisRunStatus.Failed, analysisRun.Status);
        Assert.Equal("Run cancelled: Operator cancelled work", analysisRun.FailureReason);
    }

    [Fact]
    public async Task CancelRun_ActiveLitigationSearchWorkerPausedAfterClaim_WorkerCannotContinueOrOverwriteCancelledState()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.NeedsAttention);

        var job = new LitigationSearchJob
        {
            RequestId = req.RequestId,
            Status = LitigationSearchJobStatus.Pending,
            EntityType = "company",
            ApplicationCustomerId = "1",
            KeywordsJson = "[]",
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var bprOpts = new BprLitigationOptions
        {
            BaseUrl = "https://bpr.test/", Id = "app", SecretKey = "secret",
            PollIntervalSeconds = 1, PollTimeoutMinutes = 1, MaxAttempts = 2
        };
        var handler = new StubHttpHandler();
        var client = new BprLitigationClient(
            new HttpClient(handler) { BaseAddress = new Uri(bprOpts.BaseUrl) }, Options.Create(bprOpts), NullLogger<BprLitigationClient>.Instance);
        var casePersistenceQueue = new LitigationCasePersistenceQueue();
        var casePersistenceService = new LitigationCasePersistenceService(
            db, casePersistenceQueue, new LitigationOrderDocumentQueue(), Options.Create(bprOpts),
            NullLogger<LitigationCasePersistenceService>.Instance);
        var service = new LitigationSearchJobService(
            db, client, new LitigationSearchQueue(), casePersistenceQueue, casePersistenceService, Options.Create(bprOpts),
            NullLogger<LitigationSearchJobService>.Instance);

        var controller = CreateController(db);

        // When authenticate is called, worker has claimed the job. Interleave CancelRun here!
        handler.OnPath("sec/authenticate", _ =>
        {
            controller.CancelRun(run.PipelineRunId, reason: "Operator cancelled work", returnUrl: "/Pipeline", CancellationToken.None).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"jwt":"token-1"}""", Encoding.UTF8, "application/json") };
        });

        // Worker resumes and executes
        await service.ProcessAsync(job.LitigationSearchJobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var reloadedJob = await verifyDb.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.LitigationSearchJobId == job.LitigationSearchJobId);
        Assert.Equal(LitigationSearchJobStatus.Failed, reloadedJob.Status);
        Assert.Equal("Run cancelled: Operator cancelled work", reloadedJob.FailureReason);
        Assert.Null(reloadedJob.VendorJobId);

        var reloadedReq = await verifyDb.Requests.AsNoTracking().SingleAsync(r => r.RequestId == req.RequestId);
        Assert.Equal(RequestStatus.Cancelled, reloadedReq.RequestStatus);

        var reloadedRun = await verifyDb.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == run.PipelineRunId);
        Assert.Equal(PipelineOutcome.Cancelled, reloadedRun.Outcome);
    }

    [Fact]
    public async Task CancelRun_ActiveAutoFetchWorkerPausedAfterClaim_WorkerCannotContinueOrOverwriteCancelledState()
    {
        await using var db = CreateContext();
        var (req, run) = await SeedRunAsync(db, PipelineOutcome.NeedsAttention);

        var job = new AutoFetchJob
        {
            RequestId = req.RequestId,
            Cin = req.Cin!,
            Bid = ReferenceToolClient.ComputeBid(req.Cin!),
            Status = AutoFetchJobStatus.Queued,
            WarningsJson = "[]",
            CreatedUtc = DateTime.UtcNow
        };
        db.AutoFetchJobs.Add(job);
        await db.SaveChangesAsync();

        var tempDir = Path.Combine(Path.GetTempPath(), "af_cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var refOpts = Options.Create(new ReferenceToolOptions
            {
                BaseUrl = "https://reference-tool.test",
                SessionCookie = "PHPSESSID=abc"
            });
            var refSession = new ReferenceToolSession();
            refSession.SetSigningKey([1, 2, 3, 4]);
            var handler = new StubHttpHandler();
            handler.OnPath("jwt/service.php", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jwtToken":"01020304"}""", Encoding.UTF8, "application/json")
            });
            var client = new ReferenceToolClient(new HttpClient(handler), refOpts, refSession, null!, NullLogger<ReferenceToolClient>.Instance);
            var autoFetchService = new AutoFetchJobService(db, client, refOpts, new FileValidationService(new ExcelSheetReader()),
                null!, null!, null!, null!, new FakeEnv(tempDir), NullLogger<AutoFetchJobService>.Instance);

            var controller = CreateController(db);

            // When userDetailsService.php is called, worker has claimed the job. Interleave CancelRun here!
            handler.OnPath("userDetailsService.php", _ =>
            {
                controller.CancelRun(run.PipelineRunId, reason: "Operator cancelled work", returnUrl: "/Pipeline", CancellationToken.None).GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"123","user_id":"123","username":"test"}""", Encoding.UTF8, "application/json")
                };
            });

            // Worker resumes and executes
            await autoFetchService.ProcessAsync(job.AutoFetchJobId, CancellationToken.None);

            await using var verifyDb = CreateContext();
            var reloadedJob = await verifyDb.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == job.AutoFetchJobId);
            Assert.Equal(AutoFetchJobStatus.Failed, reloadedJob.Status);
            Assert.Equal("Run cancelled: Operator cancelled work", reloadedJob.FailureReason);
            Assert.Null(reloadedJob.RocDocumentId);

            var reloadedReq = await verifyDb.Requests.AsNoTracking().SingleAsync(r => r.RequestId == req.RequestId);
            Assert.Equal(RequestStatus.Cancelled, reloadedReq.RequestStatus);

            var reloadedRun = await verifyDb.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == run.PipelineRunId);
            Assert.Equal(PipelineOutcome.Cancelled, reloadedRun.Outcome);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class InterleavingSnapshotReader(
        AppDbContext db, IConfiguration config, IOptions<BprLitigationOptions> bprOptions,
        IOptions<ReferenceToolOptions> referenceToolOptions, Action onAfterRead)
        : PipelineSnapshotReader(db, config, bprOptions, referenceToolOptions)
    {
        public override async Task<PipelineSnapshot?> ReadAsync(long requestId, CancellationToken ct)
        {
            var snapshot = await base.ReadAsync(requestId, ct);
            onAfterRead();
            return snapshot;
        }
    }

    private sealed class SpyPipelineActions : IPipelineActions
    {
        public int LitigationSearchStarts { get; private set; }
        public int LitigationAnalysisStarts { get; private set; }
        public int DossierPreRenders { get; private set; }
        public int RetryFetchCalls { get; private set; }

        public Task<PipelineActionResult> StartLitigationSearchAsync(long requestId, string correlationId, CancellationToken ct)
        {
            LitigationSearchStarts++;
            return Task.FromResult(new PipelineActionResult(true, false, 1, null, null));
        }

        public Task<PipelineActionResult> StartLitigationAnalysisAsync(long requestId, string correlationId, CancellationToken ct)
        {
            LitigationAnalysisStarts++;
            return Task.FromResult(new PipelineActionResult(true, false, 1, null, null));
        }

        public Task<DossierRenderResult> EnsureDossierRenderedAsync(long requestId, CancellationToken ct)
        {
            DossierPreRenders++;
            return Task.FromResult(new DossierRenderResult(true, false, false, "unused.pdf"));
        }

        public Task<PipelineActionResult> RetryFetchAsync(long requestId, string correlationId, CancellationToken ct)
        {
            RetryFetchCalls++;
            return Task.FromResult(new PipelineActionResult(true, false, 1, null, null));
        }
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

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly List<(string PathSuffix, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
        public void OnPath(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((pathSuffix, respond));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = _routes.FirstOrDefault(r => request.RequestUri!.AbsolutePath.Contains(r.PathSuffix, StringComparison.Ordinal));
            return Task.FromResult(route.Respond is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + request.RequestUri) }
                : route.Respond(request));
        }
    }

    private sealed class FakeEnv(string contentRoot) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
