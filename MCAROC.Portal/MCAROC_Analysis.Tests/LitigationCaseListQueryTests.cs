using System.Data.Common;
using System.Diagnostics;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.RequestDetails;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using Xunit.Abstractions;

namespace MCAROC_Analysis.Tests;

public class LitigationCaseListQueryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Thousands_of_cases_are_filtered_and_paged_in_SQL_without_materializing_order_bodies()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("LitigationUI");
        var capture = new CaptureSql();
        AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(isolated.ConnectionString).AddInterceptors(capture).Options);
        await using var seed = Db();
        var client = new Client { ClientCode = "UI", ClientName = "UI Test Client", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, CompanyName = "Target Company", RequestNumber = "UI-THOUSANDS" };
        var other = new McaRequest { Client = client, CompanyName = "Other Company", RequestNumber = "UI-OTHER" };
        seed.Requests.AddRange(request, other); await seed.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "current" };
        seed.LitigationSearchJobs.Add(job); await seed.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { RequestId = request.RequestId, LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "current", RetrievedUtc = DateTime.UtcNow, Status = LitigationReportSnapshotStatus.Completed };
        var old = new LitigationReportSnapshot { RequestId = request.RequestId, LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "old", RetrievedUtc = DateTime.UtcNow.AddDays(-1), Status = LitigationReportSnapshotStatus.Completed };
        seed.LitigationReportSnapshots.AddRange(snapshot, old); await seed.SaveChangesAsync();
        var cases = Enumerable.Range(1, 3000).Select(i => new LitigationCase
        {
            RequestId = request.RequestId, CaseNumber = $"CASE-{i:D5}", Court = "District Court", CaseStatus = i % 3 == 0 ? "Disposed" : i % 3 == 1 ? "Pending" : null,
            PetitionersJson = i == 2999 ? "[\"Unique Search Party\"]" : "[\"Target Company\"]", RespondentsJson = "[\"Opponent\"]",
            LastHearingDate = "2026-10-01", LastSeenUtc = DateTime.UtcNow
        }).ToList();
        // Reused reports can contain a canonical case originally persisted for another request.
        cases[0].RequestId = other.RequestId;
        seed.LitigationCases.AddRange(cases);
        var outside = new LitigationCase { RequestId = other.RequestId, CaseNumber = "OUTSIDE", Court = "District Court" };
        seed.LitigationCases.Add(outside); await seed.SaveChangesAsync();
        seed.LitigationCaseSourceReports.AddRange(cases.Select(c => new LitigationCaseSourceReport
            { LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, LitigationCaseId = c.LitigationCaseId }));
        seed.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationReportSnapshotId = old.LitigationReportSnapshotId, LitigationCaseId = outside.LitigationCaseId });
        var order = new LitigationCaseOrder { LitigationCaseId = cases[0].LitigationCaseId, PdfUrl = "https://vendor.invalid/private", OrderDate = "2026-10-01" };
        seed.LitigationCaseOrders.Add(order); await seed.SaveChangesAsync();
        seed.LitigationOrderDocuments.Add(new LitigationOrderDocument { LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, ExtractedText = new string('x', 100_000) });
        var run = new LitigationAiAnalysisRun { RequestId = request.RequestId, RunNumber = 1, Status = LitigationAiAnalysisRunStatus.Completed };
        seed.LitigationAiAnalysisRuns.Add(run);
        await seed.SaveChangesAsync();
        seed.LitigationCaseAiAnalyses.Add(new LitigationCaseAiAnalysis { LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId,
            LitigationCaseId = cases[0].LitigationCaseId, Status = LitigationAiAnalysisItemStatus.Completed,
            AnalysisJson = "{\"active_restraint\":true,\"lis_pendens\":true,\"summary\":\"" + new string('s', 100_000) + "\"}",
            EvidenceJson = new string('e', 100_000), RawResponseJson = new string('r', 100_000), CompletedUtc = DateTime.UtcNow.AddDays(-1) });
        await seed.SaveChangesAsync();
        await using var read = Db();
        capture.Commands.Clear(); capture.Durations.Clear();
        var query = new LitigationCaseListQuery(read);
        var timer = Stopwatch.StartNew();
        var first = (await query.BuildAsync(request.RequestId, null, null, 1, 25, null, null, null, default))!;
        output.WriteLine($"3000-case first page: {timer.ElapsedMilliseconds} ms, {capture.Commands.Count} SQL statements");
        output.WriteLine("SQL execution durations (ms): " + string.Join(", ", capture.Durations.Select(d => d.ToString("F1"))));
        Assert.Equal(3000, first.Total); Assert.Equal(120, first.TotalPages); Assert.Equal(25, first.Cases.Count);
        Assert.Equal(cases[0].LitigationCaseId, first.Cases[0].LitigationCaseId);
        Assert.Equal(1, first.Cases[0].OrderCount); Assert.Equal(1, first.Cases[0].DownloadedOrderCount);
        Assert.All(first.Cases, c => Assert.Empty(c.Orders));
        Assert.True(first.Cases[0].Analysis!.ActiveRestraint); Assert.True(first.Cases[0].Analysis!.LisPendens);
        Assert.True(first.Cases[0].IsAnalysisStaleComparedToCase); Assert.Null(first.Cases[0].Analysis!.Summary);
        Assert.DoesNotContain(capture.Commands, sql => LoadsOrderText(sql) || sql.Contains("PdfUrl") || sql.Contains("RawReportBytes") || sql.Contains("RawResponseJson"));
        // The order needs each matching case's forum, side and status as displayed, so keys are read for every match (never orders or
        // evidence) and the displayed page is then fetched by id.
        Assert.Contains(capture.Commands, sql => sql.Contains("[LitigationCaseId] IN ("));
        Assert.Contains(capture.Commands, sql => sql.Contains("JSON_VALUE"));
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("EvidenceJson"));
        var next = (await query.BuildAsync(request.RequestId, null, null, 2, 25, null, null, null, default))!;
        timer.Restart();
        await query.BuildAsync(request.RequestId, null, null, 1, 25, null, null, null, default);
        output.WriteLine($"3000-case first page after query warm-up: {timer.ElapsedMilliseconds} ms");
        Assert.Empty(first.Cases.Select(c => c.LitigationCaseId).Intersect(next.Cases.Select(c => c.LitigationCaseId)));
        timer.Restart();
        var last = (await query.BuildAsync(request.RequestId, null, null, int.MaxValue, 100, null, null, null, default))!;
        output.WriteLine($"3000-case last page (100 rows): {timer.ElapsedMilliseconds} ms");
        Assert.Equal(30, last.Page); Assert.Equal(100, last.Cases.Count); Assert.Equal(3000, last.Last);
        var match = (await query.BuildAsync(request.RequestId, null, null, 200, 25, "unique search party", null, null, default))!;
        Assert.Equal(1, match.Total); Assert.Equal(1, match.Page); Assert.Equal(cases[2998].LitigationCaseId, Assert.Single(match.Cases).LitigationCaseId);
        var unknown = (await query.BuildAsync(request.RequestId, "District Court", "Unknown", 1, 25, null, null, null, default))!;
        Assert.Equal(1000, unknown.Total);
        var focused = (await query.BuildAsync(request.RequestId, null, null, 1, 25, null, null, cases[2998].LitigationCaseId, default))!;
        Assert.Equal(120, focused.Page); Assert.Contains(focused.Cases, c => c.LitigationCaseId == cases[2998].LitigationCaseId);
        var empty = (await query.BuildAsync(request.RequestId, null, null, -1, 999, "OUTSIDE", null, null, default))!;
        Assert.Equal(25, empty.PageSize); Assert.Equal(1, empty.Page); Assert.Empty(empty.Cases);
        Assert.Null(await query.BuildAsync(long.MaxValue, null, null, 1, 25, null, null, null, default));

        var controller = new RequestsController(read, null!, null!, null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var stale = Assert.IsType<StatusCodeResult>(await controller.LitigationCases(request.RequestId, snapshot: old.LitigationReportSnapshotId));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal(409, Assert.IsType<StatusCodeResult>(await controller.LitigationCases(request.RequestId, generation: "stale")).StatusCode);
        var partial = Assert.IsType<PartialViewResult>(await controller.LitigationCases(request.RequestId, page: 2, snapshot: snapshot.LitigationReportSnapshotId));
        Assert.Equal(2, Assert.IsType<LitigationCaseListViewModel>(partial.Model).Page);
        Assert.Equal(409, Assert.IsType<StatusCodeResult>(await controller.LitigationAnalysisAvailability(request.RequestId, "stale")).StatusCode);
        Assert.IsType<NotFoundResult>(await controller.LitigationAnalysisAvailability(long.MaxValue));
        capture.Commands.Clear();
        var lifecycle = new LitigationController(read, null!, Options.Create(new BprLitigationOptions()), null!);
        Assert.IsType<OkObjectResult>(await lifecycle.GetAnalysisProgress(request.RequestId, default));
        Assert.Single(capture.Commands);
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("AnalysisJson") || sql.Contains("EvidenceJson") || sql.Contains("LitigationCaseAiAnalyses"));
        Assert.IsType<NotFoundResult>(await lifecycle.GetAnalysisProgress(long.MaxValue, default));

        var ingestion = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        seed.IngestionRuns.Add(ingestion); await seed.SaveChangesAsync();
        seed.RocCharges.Add(new RocCharge { RequestId = request.RequestId, IngestionRunId = ingestion.IngestionRunId,
            RocChargeNumber = "UI-CHARGE", LatestChargeHolderRaw = "Opponent", ChargeStatus = "Open" });
        cases[0].CaseType = cases[27].CaseType = "Recovery suit";
        // The charge holder brings the recovery suit against the company (#554: a lender link needs the company as respondent).
        cases[0].PetitionersJson = cases[27].PetitionersJson = "[\"Opponent\"]";
        cases[0].RespondentsJson = cases[27].RespondentsJson = "[\"Target Company\"]";
        await seed.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var chargeLinks = new ChargeLitigationService(read, cache);
        var initialLinks = await chargeLinks.GetAsync(request.RequestId);
        Assert.Equal(2, initialLinks.Links.Count);
        var withLinks = new RequestsController(read, null!, null!, null!, null!, null!, null!, null!, null!, chargeLitigation: chargeLinks);
        withLinks.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        capture.Commands.Clear();
        foreach (var response in new[] {
            await withLinks.LitigationCases(request.RequestId, page: 2),
            await withLinks.LitigationCases(request.RequestId, q: cases[27].CaseNumber) })
        {
            var result = Assert.IsType<PartialViewResult>(response);
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ChargeLitigationLink>>(result.ViewData["ChargeLinks"]));
            var html = await LitigationTabRenderingTests.RenderCaseListAsync(result);
            Assert.Contains("Charge holder&#x27;s recovery case", html);
        }
        cache.Compact(1);
        Assert.Equal(409, Assert.IsType<StatusCodeResult>(await withLinks.LitigationCases(request.RequestId, page: 2)).StatusCode);
        Assert.DoesNotContain(capture.Commands, sql => LoadsOrderText(sql) || sql.Contains("PdfUrl") || sql.Contains("EvidenceJson"));
    }

    [Fact]
    public void Pagination_links_preserve_filters_and_encode_user_input()
    {
        var model = new LitigationCaseListViewModel { RequestId = 7, PageSize = 50, Search = "A & B", Court = "High Court", Status = "Unknown", Outcome = "Fine" };
        var url = model.PageUrl(3);
        Assert.Contains("q=A%20%26%20B", url); Assert.Contains("page=3", url);
        Assert.Contains("pageSize=50", url); Assert.EndsWith("#tab-litigation", url);
    }

    /// <summary>The order text is read into memory: the column is selected. A null check on it inside SQL (the charge links' evidence version
    /// counts the orders whose extraction ended without text) reads no text and is not a body load.</summary>
    private static bool LoadsOrderText(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql, @"\[\w+\]\.\[ExtractedText\]\s+IS\s+(?:NOT\s+)?NULL", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Contains("ExtractedText", StringComparison.Ordinal);

    [Fact]
    public void A_null_check_on_the_order_text_is_not_a_body_load()
    {
        Assert.False(LoadsOrderText("SELECT COUNT(*) FROM [LitigationOrderDocuments] AS [l] WHERE [l].[ExtractedText] IS NULL AND [l].[Status] = 2"));
        Assert.False(LoadsOrderText("SELECT CASE WHEN [l].[ExtractedText] IS NOT NULL THEN N'' END FROM [LitigationOrderDocuments] AS [l]"));
        Assert.True(LoadsOrderText("SELECT [l].[LitigationOrderDocumentId], [l].[ExtractedText] FROM [LitigationOrderDocuments] AS [l]"));
        Assert.True(LoadsOrderText("SELECT [l].[ExtractedText] FROM [LitigationOrderDocuments] AS [l] WHERE [l].[ExtractedText] IS NOT NULL"));
    }

    private sealed class CaptureSql : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<double> Durations { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        { Durations.Add(eventData.Duration.TotalMilliseconds); return ValueTask.FromResult(result); }
    }
}
