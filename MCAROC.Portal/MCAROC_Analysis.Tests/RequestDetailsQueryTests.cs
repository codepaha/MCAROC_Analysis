using System.Data.Common;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.RequestDetails;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Tests;

public class RequestDetailsQueryTests : IAsyncLifetime
{
    private static AppDbContext Db(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(TestDatabase.ConnectionString).AddInterceptors(interceptors).Options);
    public async Task InitializeAsync() { await using var db = Db(); await TestDatabase.MigrateAsync(db); }
    public Task DisposeAsync() => Task.CompletedTask;
    private static RequestsController Controller(AppDbContext db) => new(db, null!, null!, null!, null!, null!,
        new DossierCache(db, new DossierAssembler(db), new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 })), null!, new CorporateTimelineBuilder(db));

    [Fact]
    public async Task Filing_summary_excludes_duplicates_but_keeps_their_citation_ids_without_loading_bodies()
    {
        await using var db = Db();
        var (id, _, _) = await Dossier.DossierTestSeed.SeedAsync(db);
        var batch = new McaFilingBatch { RequestId = id, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch); await db.SaveChangesAsync();
        var filing = new McaFiling { RequestId = id, BatchId = batch.BatchId, Srn = "SUMMARY" };
        var original = new McaFilingDocument { RequestId = id, BatchId = batch.BatchId, Category = FilingCategory.Charge,
            ManualReviewRequired = true, ProcessingStatus = FilingDocumentProcessingStatus.Completed, ChunkingStatus = ChunkingStatus.Chunked };
        filing.Documents.Add(original); db.McaFilings.Add(filing); await db.SaveChangesAsync();
        var duplicate = new McaFilingDocument { RequestId = id, BatchId = batch.BatchId, FilingId = filing.FilingId,
            DuplicateOfDocumentId = original.FilingDocumentId, Category = FilingCategory.Charge, ManualReviewRequired = true };
        db.McaFilingDocuments.Add(duplicate);
        db.McaFilingExtractions.Add(new McaFilingExtraction { FilingId = filing.FilingId, Status = ExtractionStatus.Success, RawModelResponse = new string('x', 20000) });
        await db.SaveChangesAsync();
        await using var read = Db();
        var vm = new RequestDetailsViewModel { Request = await read.Requests.AsNoTracking().SingleAsync(r => r.RequestId == id), Documents = [] };
        await new FilingDetailsQuery(read).LoadAsync(vm, default);
        Assert.Equal(1, vm.FilingCategoryCounts[FilingCategory.Charge]);
        Assert.Equal(1, vm.ManualReviewFilingCount);
        Assert.Equal(1, vm.AuthoritativeBatchChunkedCount);
        Assert.Equal(1, vm.AiSuccessCount);
        Assert.Contains(duplicate.FilingDocumentId, vm.AuthoritativeDocumentIds!);
        Assert.Empty(vm.FilingSummaries);
        Assert.Empty(read.ChangeTracker.Entries<McaFilingDocument>());
        Assert.Empty(read.ChangeTracker.Entries<McaFilingExtraction>());
    }

    [Fact]
    public async Task Document_pages_keep_selected_batch_and_reject_another_requests_batch()
    {
        await using var db = Db();
        var (id, _, _) = await Dossier.DossierTestSeed.SeedAsync(db);
        var (otherId, _, _) = await Dossier.DossierTestSeed.SeedAsync(db);
        var first = new McaFilingBatch { RequestId = id, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow.AddDays(-1) };
        var latest = new McaFilingBatch { RequestId = id, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow };
        var foreign = new McaFilingBatch { RequestId = otherId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.AddRange(first, latest, foreign); await db.SaveChangesAsync();
        db.McaFilings.AddRange(new McaFiling { RequestId = id, BatchId = first.BatchId, Srn = "OLD" },
            new McaFiling { RequestId = id, BatchId = latest.BatchId, Srn = "NEW" });
        await db.SaveChangesAsync();
        var controller = Controller(db);
        var result = Assert.IsType<PartialViewResult>(await controller.Documents(id, null, batchId: first.BatchId));
        var vm = Assert.IsType<DocumentsPageViewModel>(result.Model);
        Assert.Equal(first.BatchId, vm.BatchId);
        Assert.Equal("OLD", Assert.Single(vm.Filings).Filing.Srn);
        Assert.IsType<NotFoundResult>(await controller.Documents(id, null, batchId: foreign.BatchId));
    }

    [Fact]
    public async Task Timeline_uses_loaded_ingestion_even_when_a_new_run_is_published()
    {
        await using var db = Db();
        var (id, _, _) = await Dossier.DossierTestSeed.SeedAsync(db);
        var vm = new RequestDetailsViewModel { Request = await db.Requests.SingleAsync(r => r.RequestId == id), Documents = [] };
        await new WorkbookDetailsQuery(db).LoadAsync(vm, null, default);
        var expected = await new CorporateTimelineBuilder(db).BuildAsync(id);
        Assert.Equal(expected, CorporateTimelineBuilder.BuildFrom(vm));
        var next = new IngestionRun { RequestId = id, RunNumber = 2, Status = IngestionRunStatus.CompletedClean, StartedDate = DateTime.UtcNow };
        db.IngestionRuns.Add(next); await db.SaveChangesAsync();
        await db.Requests.Where(r => r.RequestId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.LatestCompletedIngestionRunId, next.IngestionRunId));
        Assert.Equal(expected, CorporateTimelineBuilder.BuildFrom(vm));
    }

    [Fact]
    public async Task Concurrent_publication_returns_refresh_instead_of_a_mixed_assessment()
    {
        await using var db = Db();
        var (id, _, _) = await Dossier.DossierTestSeed.SeedAsync(db);
        var next = new IngestionRun { RequestId = id, RunNumber = 2, Status = IngestionRunStatus.CompletedClean, StartedDate = DateTime.UtcNow };
        db.IngestionRuns.Add(next); await db.SaveChangesAsync();
        await using var read = Db(new PublishAtFinalRead(id, next.IngestionRunId));
        var controller = Controller(read);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var result = Assert.IsType<ViewResult>(await controller.Details(id, null));
        Assert.Equal("RefreshRequired", result.ViewName);
        Assert.Equal(409, controller.Response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Repeated_report_hash_renders_the_same_snapshot_across_tabs_and_charge_links(bool hasOpenCharges)
    {
        await using var seed = Db();
        var (id, _, _) = await Dossier.DossierTestSeed.SeedAsync(seed);
        if (!hasOpenCharges)
            await seed.RocCharges.Where(c => c.RequestId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ChargeStatus, "Satisfied"));
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var job = new LitigationSearchJob { RequestId = id, Status = LitigationSearchJobStatus.Completed, RawResponseHash = hashA };
        seed.LitigationSearchJobs.Add(job);
        await seed.SaveChangesAsync();
        var snapshotA = new LitigationReportSnapshot { RequestId = id, LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = hashA, RetrievedUtc = DateTime.UtcNow.AddDays(-2), Status = LitigationReportSnapshotStatus.Completed };
        var snapshotB = new LitigationReportSnapshot { RequestId = id, LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = hashB, RetrievedUtc = DateTime.UtcNow.AddDays(-1), Status = LitigationReportSnapshotStatus.Completed };
        seed.LitigationReportSnapshots.AddRange(snapshotA, snapshotB);
        var caseA = new LitigationCase { RequestId = id, CaseNumber = "A/1", Court = "Debts Recovery Tribunal",
            CaseStatus = "Pending", PetitionersJson = "[\"STATE BANK OF INDIA\"]" };
        var caseB = new LitigationCase { RequestId = id, CaseNumber = "B/1", Court = "Debts Recovery Tribunal",
            CaseStatus = "Pending", PetitionersJson = "[\"STATE BANK OF INDIA\"]" };
        seed.LitigationCases.AddRange(caseA, caseB);
        await seed.SaveChangesAsync();
        seed.LitigationCaseSourceReports.AddRange(
            new LitigationCaseSourceReport { LitigationReportSnapshotId = snapshotA.LitigationReportSnapshotId, LitigationCaseId = caseA.LitigationCaseId },
            new LitigationCaseSourceReport { LitigationReportSnapshotId = snapshotB.LitigationReportSnapshotId, LitigationCaseId = caseB.LitigationCaseId });
        await seed.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        // A -> B -> A, then another reload of A. Keep the cache but recreate the request context each time.
        foreach (var snapshot in new[] { snapshotA, snapshotB, snapshotA, snapshotA })
        {
            await seed.LitigationSearchJobs.Where(j => j.RequestId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.RawResponseHash, snapshot.ReportHash));
            await using var read = Db();
            var chargeLinks = new ChargeLitigationService(read, cache);
            var controller = new RequestsController(read, null!, null!, null!, null!, null!,
                new DossierCache(read, new DossierAssembler(read, chargeLinks), cache), null!, new CorporateTimelineBuilder(read),
                chargeLitigation: chargeLinks);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            var result = Assert.IsType<ViewResult>(await controller.Details(id, null));
            Assert.NotEqual("RefreshRequired", result.ViewName);
            Assert.Equal(200, controller.Response.StatusCode);
            var vm = Assert.IsType<RequestDetailsViewModel>(result.Model);
            Assert.Equal(snapshot.LitigationReportSnapshotId, vm.LitigationDataLake!.AuthoritativeSnapshot!.LitigationReportSnapshotId);
            Assert.False(vm.LitigationDataLake.IsPriorRunDataShown);
            Assert.Equal(snapshot.LitigationReportSnapshotId, vm.ChargeLitigation!.SnapshotId);
            Assert.Equal(await ChargeLitigationService.VersionAsync(read, id), vm.ChargeLitigation.Version);
            Assert.Equal(snapshot.LitigationReportSnapshotId, (await RequestPageVersion.ReadAsync(read, id, default))!.LitigationSnapshot);
            Assert.Equal(snapshot == snapshotA ? caseA.LitigationCaseId : caseB.LitigationCaseId,
                Assert.Single(vm.LitigationDataLake.Cases).LitigationCaseId);
            if (hasOpenCharges)
            {
                var lakeLinks = vm.ChargeLitigation.Links.Where(l => l.Case.LitigationCaseId is not null).ToList();
                Assert.NotEmpty(lakeLinks);
                Assert.All(lakeLinks,
                    link => Assert.Equal(snapshot == snapshotA ? caseA.LitigationCaseId : caseB.LitigationCaseId, link.Case.LitigationCaseId));
            }
            else
                Assert.Empty(vm.ChargeLitigation.Links);
        }
    }

    private sealed class PublishAtFinalRead(long requestId, long runId) : DbCommandInterceptor
    {
        private int reads;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Request page publication version") && ++reads == 2)
            {
                await using var writer = Db();
                await writer.Requests.Where(r => r.RequestId == requestId).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.LatestCompletedIngestionRunId, runId), cancellationToken);
            }
            return result;
        }
    }
}
