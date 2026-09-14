using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.CalculationAssurance;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Coverage for the #164 internal calculation-audit review controller — the Index assembly logic
/// (snapshot resolution, joins across ledger/checks/discrepancies/approvals/holds) and that every action
/// delegates to CalculationDiscrepancyWorkflowService and follows the post-redirect-get + TempData-error
/// pattern, never re-implementing any workflow rule itself.</summary>
public class CalculationAuditControllerTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static CalculationAuditController NewController(AppDbContext db) => new(
        db, new CalculationDiscrepancyWorkflowService(db, NullLogger<CalculationDiscrepancyWorkflowService>.Instance))
    {
        TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
    };

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    [Fact]
    public async Task Index_UnknownRequest_IsNotFound()
    {
        await using var db = CreateContext();
        Assert.IsType<NotFoundResult>(await NewController(db).Index(-999, null, CancellationToken.None));
    }

    [Fact]
    public async Task Index_NoSnapshot_ShowsEmptyState()
    {
        await using var seedDb = CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = CreateContext();
        var result = await NewController(db).Index(requestId, null, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CalculationAuditPageModel>(view.Model);
        Assert.Empty(model.Snapshots);
        Assert.Null(model.Selected);
    }

    [Fact]
    public async Task Index_WithSnapshot_AssemblesLedgerChecksAndDiscrepanciesForTheCurrentSnapshot()
    {
        await using var seedDb = CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        var snapshot = new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = ingestionRunId, AnalysisRunId = analysisRunId, CreatedUtc = DateTime.UtcNow };
        seedDb.CalculationAuditSnapshots.Add(snapshot);
        var ledger = new CalculationLedgerEntry
        {
            Snapshot = snapshot, CalculationKey = "Test.Key", MetricLabel = "Test metric", Period = "FY2025",
            ValueNumeric = 100m, CreatedUtc = DateTime.UtcNow
        };
        seedDb.CalculationLedgerEntries.Add(ledger);
        seedDb.CalculationCheckResults.Add(new CalculationCheckResult
        {
            Snapshot = snapshot, CheckKey = "Test.Check", Status = CalculationCheckStatus.NotTriggered, RanUtc = DateTime.UtcNow
        });
        await seedDb.SaveChangesAsync();

        var discrepancy = new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId, SourceType = CalculationDiscrepancySourceType.AiCandidate,
            PrimaryLedgerEntryId = ledger.CalculationLedgerEntryId, ClaimSummary = "Candidate claim",
            Status = CalculationDiscrepancyStatus.Open, CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow
        };
        seedDb.CalculationDiscrepancies.Add(discrepancy);
        await seedDb.SaveChangesAsync();

        await using var db = CreateContext();
        var result = await NewController(db).Index(requestId, null, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<CalculationAuditPageModel>(view.Model);
        Assert.Single(model.Snapshots);
        Assert.True(model.Snapshots[0].IsCurrent);
        Assert.Single(model.LedgerEntries);
        Assert.Single(model.CheckResults);
        var row = Assert.Single(model.Discrepancies);
        Assert.Equal(discrepancy.CalculationDiscrepancyId, row.Discrepancy.CalculationDiscrepancyId);
        Assert.Equal("Test.Key", row.PrimaryLedgerEntry?.CalculationKey);
        Assert.False(row.HasActiveHold);
    }

    [Fact]
    public async Task Index_ResolvesCurrentSnapshot_AsTheOneMatchingLatestCompletedIngestionRun_NotJustNewest()
    {
        await using var seedDb = CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        var oldSnapshot = new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = 999, AnalysisRunId = 999, CreatedUtc = DateTime.UtcNow.AddDays(1) }; // newer by CreatedUtc, but not the real ingestion run
        var currentSnapshot = new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = ingestionRunId, AnalysisRunId = analysisRunId, CreatedUtc = DateTime.UtcNow.AddDays(-1) };
        seedDb.CalculationAuditSnapshots.AddRange(oldSnapshot, currentSnapshot);
        await seedDb.SaveChangesAsync();

        await using var db = CreateContext();
        var result = await NewController(db).Index(requestId, null, CancellationToken.None);

        var model = Assert.IsType<CalculationAuditPageModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(currentSnapshot.CalculationAuditSnapshotId, model.Selected!.CalculationAuditSnapshotId);
        Assert.True(model.Selected.IsCurrent);
    }

    [Fact]
    public async Task Index_NullLatestCompletedIngestionRunId_FailsClosed_NeverFallsBackToTheNewestSnapshot()
    {
        await using var seedDb = CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);
        // Simulate a request whose completed-ingestion pointer was cleared (e.g. mid re-ingest) while an
        // old, now-abandoned snapshot still exists — that snapshot must never be silently treated as current.
        await seedDb.Requests.Where(r => r.RequestId == requestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LatestCompletedIngestionRunId, (long?)null));
        seedDb.CalculationAuditSnapshots.Add(new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = 111, AnalysisRunId = 111, CreatedUtc = DateTime.UtcNow });
        await seedDb.SaveChangesAsync();

        await using var db = CreateContext();
        var result = await NewController(db).Index(requestId, null, CancellationToken.None);

        var model = Assert.IsType<CalculationAuditPageModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(model.Selected);
        Assert.Single(model.Snapshots); // still listed for historical browsing
        Assert.Empty(model.LedgerEntries);
        Assert.Empty(model.Discrepancies);
    }

    [Fact]
    public async Task Index_UnmatchedLatestCompletedIngestionRunId_FailsClosed_NeverFallsBackToTheNewestSnapshot()
    {
        await using var seedDb = CreateContext();
        var (requestId, ingestionRunId, _) = await DossierTestSeed.SeedAsync(seedDb);
        // A snapshot exists, but for a DIFFERENT ingestion run than the request's current one (e.g. an
        // abandoned re-ingest attempt that got its own snapshot before being superseded).
        seedDb.CalculationAuditSnapshots.Add(new CalculationAuditSnapshot
        {
            RequestId = requestId, IngestionRunId = ingestionRunId + 1000, AnalysisRunId = ingestionRunId + 1000, CreatedUtc = DateTime.UtcNow
        });
        await seedDb.SaveChangesAsync();

        await using var db = CreateContext();
        var result = await NewController(db).Index(requestId, null, CancellationToken.None);

        var model = Assert.IsType<CalculationAuditPageModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(model.Selected);
        Assert.Single(model.Snapshots);
    }

    [Fact]
    public async Task Index_ExplicitSnapshotId_StillShowsAHistoricalNonCurrentSnapshot()
    {
        // Fail-closed applies only to the DEFAULT resolution — a reviewer deliberately browsing an old
        // snapshot by id must still see its data, just clearly marked as not current.
        await using var seedDb = CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);
        var oldSnapshot = new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = 555, AnalysisRunId = 555, CreatedUtc = DateTime.UtcNow };
        seedDb.CalculationAuditSnapshots.Add(oldSnapshot);
        await seedDb.SaveChangesAsync();

        await using var db = CreateContext();
        var result = await NewController(db).Index(requestId, oldSnapshot.CalculationAuditSnapshotId, CancellationToken.None);

        var model = Assert.IsType<CalculationAuditPageModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.NotNull(model.Selected);
        Assert.Equal(oldSnapshot.CalculationAuditSnapshotId, model.Selected!.CalculationAuditSnapshotId);
        Assert.False(model.Selected.IsCurrent);
    }

    [Fact]
    public async Task Confirm_Success_RedirectsToIndex_NoTempDataError()
    {
        await using var seedDb = CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);
        var (discrepancyId, _) = await SeedOpenAiCandidateAsync(seedDb, requestId, ingestionRunId, analysisRunId);

        await using var db = CreateContext();
        var controller = NewController(db);
        var result = await controller.Confirm(discrepancyId, requestId, "alice", CalculationDiscrepancySeverity.Material, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CalculationAuditController.Index), redirect.ActionName);
        Assert.Equal(requestId, redirect.RouteValues!["requestId"]);
        Assert.False(controller.TempData.ContainsKey("CalcAuditError"));

        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Confirmed,
            (await verifyDb.CalculationDiscrepancies.FirstAsync(d => d.CalculationDiscrepancyId == discrepancyId)).Status);
    }

    [Fact]
    public async Task Confirm_Failure_SetsTempDataError_AndRedirects()
    {
        await using var seedDb = CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);
        var (discrepancyId, _) = await SeedOpenAiCandidateAsync(seedDb, requestId, ingestionRunId, analysisRunId);

        await using var db = CreateContext();
        var controller = NewController(db);
        // Undefined severity — refused by the workflow service.
        var result = await controller.Confirm(discrepancyId, requestId, "alice", (CalculationDiscrepancySeverity)999, null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(CalculationAuditController.Index), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey("CalcAuditError"));
        Assert.NotNull(controller.TempData["CalcAuditError"]);
    }

    [Fact]
    public async Task Reject_Success_TransitionsAndRedirects()
    {
        await using var seedDb = CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);
        var (discrepancyId, _) = await SeedOpenAiCandidateAsync(seedDb, requestId, ingestionRunId, analysisRunId);

        await using var db = CreateContext();
        var controller = NewController(db);
        var result = await controller.Reject(discrepancyId, requestId, "alice", "Not a real issue.", CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        await using var verifyDb = CreateContext();
        Assert.Equal(CalculationDiscrepancyStatus.Rejected,
            (await verifyDb.CalculationDiscrepancies.FirstAsync(d => d.CalculationDiscrepancyId == discrepancyId)).Status);
    }

    private static async Task<(long DiscrepancyId, long LedgerEntryId)> SeedOpenAiCandidateAsync(
        AppDbContext db, long requestId, long ingestionRunId, long analysisRunId)
    {
        var snapshot = new CalculationAuditSnapshot { RequestId = requestId, IngestionRunId = ingestionRunId, AnalysisRunId = analysisRunId, CreatedUtc = DateTime.UtcNow };
        db.CalculationAuditSnapshots.Add(snapshot);
        var ledger = new CalculationLedgerEntry
        {
            Snapshot = snapshot, CalculationKey = "Test.Key", MetricLabel = "Test", Period = "FY2025",
            ValueNumeric = 100m, CreatedUtc = DateTime.UtcNow
        };
        db.CalculationLedgerEntries.Add(ledger);
        await db.SaveChangesAsync();

        var discrepancy = new CalculationDiscrepancy
        {
            CalculationAuditSnapshotId = snapshot.CalculationAuditSnapshotId, SourceType = CalculationDiscrepancySourceType.AiCandidate,
            PrimaryLedgerEntryId = ledger.CalculationLedgerEntryId, ClaimSummary = "x", ClaimedActualValue = 100m,
            Status = CalculationDiscrepancyStatus.Open, CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow
        };
        db.CalculationDiscrepancies.Add(discrepancy);
        await db.SaveChangesAsync();
        return (discrepancy.CalculationDiscrepancyId, ledger.CalculationLedgerEntryId);
    }
}
