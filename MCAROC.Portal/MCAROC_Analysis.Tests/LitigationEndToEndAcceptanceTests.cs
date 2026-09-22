using System.Net;
using System.Net.Sockets;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>
/// Release-gate coverage for LIT-08.  This intentionally crosses the durable hand-offs rather than proving
/// each component with a mock: BPR result -> fenced job completion -> immutable snapshot -> case/order import
/// -> standalone report.  The BPR handler is local and uses an RFC 5737 address, so it is safe in CI and does
/// not need a vendor credential.
/// </summary>
public sealed class LitigationEndToEndAcceptanceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    private static readonly BprLitigationOptions OptionsValue = new()
    {
        BaseUrl = "https://bpr.example/", Id = "test-app", SecretKey = "test-secret",
        PollIntervalSeconds = 1, PollTimeoutMinutes = 1, MaxAttempts = 1, OrderRetentionDays = 7
    };

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Representative_report_is_deduplicated_refreshed_and_reconciled_in_both_exports()
    {
        long requestId;
        long jobId;
        await using (var setup = CreateContext())
        {
            var client = new Client { ClientCode = "E2E" + Guid.NewGuid().ToString("N")[..8], ClientName = "Acceptance Co", CreatedDate = DateTime.UtcNow };
            var request = new McaRequest
            {
                Client = client, EntityType = EntityType.Company, CompanyName = "Acceptance Co Limited",
                Cin = "U45203OR1995PLC003982", RequestNumber = "LIT-E2E-" + Guid.NewGuid().ToString("N"),
                RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
            };
            setup.Requests.Add(request);
            await setup.SaveChangesAsync();
            requestId = request.RequestId;

            var service = NewSearchService(setup, new StubHandler(FirstReport));
            var job = await service.CreateOrResetJobAsync(
                requestId, LitigationKeywordPlanner.Build(request.CompanyName!), "individual", request.RequestNumber!, CancellationToken.None);
            jobId = job.LitigationSearchJobId;
        }

        await ProcessAndPersistAsync(jobId, FirstReport);

        long originalDocumentId;
        await using (var afterFirstImport = CreateContext())
        {
            Assert.Single(await afterFirstImport.LitigationCases.Where(c => c.RequestId == requestId).ToListAsync());
            Assert.Equal(2, await afterFirstImport.LitigationCaseOrders.CountAsync(o => o.Case!.RequestId == requestId));
            Assert.Equal(2, await afterFirstImport.LitigationOrderDocuments.CountAsync(d => d.Order!.Case!.RequestId == requestId));
            Assert.Equal(1, await afterFirstImport.LitigationCaseSourceReports.CountAsync(s => s.Case!.RequestId == requestId));

            var document = await afterFirstImport.LitigationOrderDocuments
                .OrderBy(d => d.LitigationOrderDocumentId).FirstAsync(d => d.Order!.Case!.RequestId == requestId);
            originalDocumentId = document.LitigationOrderDocumentId;
            document.Status = LitigationOrderDocumentStatus.Expired;
            document.RetainedUntilUtc = DateTime.UtcNow.AddDays(-1);
            await afterFirstImport.SaveChangesAsync();
        }

        // A later BPR report for the same CNR but a different CSP must merge the case and re-admit an expired
        // order when the vendor has surfaced it again.  CSP is preserved as provenance, never a merge key.
        await using (var rerun = CreateContext())
        {
            var service = NewSearchService(rerun, new StubHandler(SecondReport));
            var job = await service.CreateOrResetJobAsync(
                requestId, LitigationKeywordPlanner.Build("Acceptance Co Limited"), "individual", "rerun-customer", CancellationToken.None);
            Assert.Equal(jobId, job.LitigationSearchJobId);
        }

        await ProcessAndPersistAsync(jobId, SecondReport);

        await using var verify = CreateContext();
        var onlyCase = Assert.Single(await verify.LitigationCases.Where(c => c.RequestId == requestId).ToListAsync());
        Assert.Equal("TNKP070001331234", onlyCase.Cnr);
        Assert.Equal(2, await verify.LitigationCaseOrders.CountAsync(o => o.LitigationCaseId == onlyCase.LitigationCaseId));
        Assert.Equal(2, await verify.LitigationCaseSourceReports.CountAsync(s => s.LitigationCaseId == onlyCase.LitigationCaseId));

        var refreshed = await verify.LitigationOrderDocuments.FindAsync(originalDocumentId);
        Assert.NotNull(refreshed);
        Assert.Equal(LitigationOrderDocumentStatus.Pending, refreshed!.Status);
        Assert.Equal(1, refreshed.RefreshCount);

        var report = await new LitigationReportAssembler(verify).AssembleAsync(requestId);
        Assert.NotNull(report);
        Assert.Single(report.Cases);
        Assert.Equal(2, report.Cases[0].Orders.Count);
        Assert.True(report.CourtSummaryGrid.IsReconciled);
        Assert.Equal(1, report.CourtSummaryGrid.TotalCases);
        Assert.Equal(2, report.CourtSummaryGrid.TotalOrders);

        var csv = Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report));
        Assert.Contains("TNKP070001331234", csv);
        Assert.DoesNotContain("vendor.example", csv, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(LitigationReportArtifacts.RenderPdf(report));
    }

    private static async Task ProcessAndPersistAsync(long jobId, string report)
    {
        await using (var jobDb = CreateContext())
            await NewSearchService(jobDb, new StubHandler(report)).ProcessAsync(jobId, CancellationToken.None);

        await using var importDb = CreateContext();
        var persistence = NewPersistenceService(importDb);
        var job = await importDb.LitigationSearchJobs.SingleAsync(j => j.LitigationSearchJobId == jobId);
        Assert.Equal(LitigationSearchJobStatus.Completed, job.Status);
        var snapshotId = await persistence.EnsureSnapshotAsync(
            jobId, job.RawResponseHash!, job.ReportFormat, job.RawReportBytes!, DateTime.UtcNow, CancellationToken.None);
        await persistence.PersistSnapshotAsync(snapshotId, CancellationToken.None);
    }

    private static LitigationSearchJobService NewSearchService(AppDbContext db, StubHandler handler)
    {
        var client = new BprLitigationClient(new HttpClient(handler) { BaseAddress = new Uri(OptionsValue.BaseUrl) },
            Options.Create(OptionsValue), NullLogger<BprLitigationClient>.Instance, FakeResolver);
        return new LitigationSearchJobService(db, client, new LitigationSearchQueue(), new LitigationCasePersistenceQueue(),
            NewPersistenceService(db), Options.Create(OptionsValue), NullLogger<LitigationSearchJobService>.Instance);
    }

    private static LitigationCasePersistenceService NewPersistenceService(AppDbContext db) => new(db,
        new LitigationCasePersistenceQueue(), new LitigationOrderDocumentQueue(), Options.Create(OptionsValue),
        NullLogger<LitigationCasePersistenceService>.Instance);

    private static Task<IPAddress[]> FakeResolver(string host, CancellationToken _) =>
        string.Equals(host, "bpr.example", StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") })
            : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));

    private sealed class StubHandler(string report) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken _)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("sec/authenticate", StringComparison.Ordinal)) return Task.FromResult(Json("{\"jwt\":\"local-test-token\"}"));
            if (path.Contains("bprjob/register", StringComparison.Ordinal)) return Task.FromResult(Json("{\"job_id\":\"local-job\"}"));
            if (path.Contains("report/job/", StringComparison.Ordinal)) return Task.FromResult(Json(report));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private const string FirstReport = """
        { "request_details":{"job_id":"local-job","keywords":["Acceptance Co Limited"]}, "district_court":{"against":{"civil":[
          {"_id":"provider-one","csp_id":"csp-first","cnr_number":"TNKP070001331234","type":"district","court":"Acceptance District Court","case_no":"12/2025","case_type":"OS","case_year":"2025","case_status":"PENDING","orders":[{"pdf_url":"https://vendor.example/order-a.pdf","order_date":"2025-01-05","order_type":"Order"}]},
          {"_id":"provider-two","csp_id":"csp-second","cnr_number":"TNKP070001331234","type":"district","court":"Acceptance District Court","case_no":"12/2025","case_type":"OS","case_year":"2025","case_status":"PENDING","orders":[{"pdf_url":"https://vendor.example/order-b.pdf","order_date":"2025-01-06","order_type":"Judgment"}]}
        ]}}}
        """;

    private const string SecondReport = """
        { "request_details":{"job_id":"local-job-rerun","keywords":["Acceptance Co Limited"]}, "district_court":{"against":{"civil":[
          {"_id":"provider-three","csp_id":"csp-third","cnr_number":"TNKP070001331234","type":"district","court":"Acceptance District Court","case_no":"12/2025","case_type":"OS","case_year":"2025","case_status":"PENDING","orders":[{"pdf_url":"https://vendor.example/order-a.pdf","order_date":"2025-01-05","order_type":"Order"}]}
        ]}}}
        """;
}
