using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers LitigationCasePersistenceService's two distinct correctness guarantees: report-level
/// idempotency (re-processing the exact same completed report is a no-op — keyed on job id + report hash,
/// since a request's job row is reused across reruns) and case-level CNR-first de-duplication (never merges
/// on CSP ID or fuzzy field matching — see LitigationCaseIdentity).</summary>
public class LitigationCasePersistenceServiceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<McaRequest> SeedRequestAsync(AppDbContext db, string suffix)
    {
        var client = new Client
        {
            ClientCode = "LITC" + suffix + Guid.NewGuid().ToString("N")[..6], ClientName = "Litigation Case Test Co", CreatedDate = DateTime.UtcNow
        };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Litigation Case Test Company",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"LITC-{suffix}-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>Creates the request's job row, or — matching production's one-job-per-request reality
    /// (CreateOrResetJobAsync reuses the same row on every rerun, enforced by a unique index on RequestId) —
    /// updates it in place if one already exists, simulating a rerun that produced a new report.</summary>
    private static async Task<LitigationSearchJob> SeedCompletedJobAsync(AppDbContext db, long requestId, string reportJson)
    {
        var bytes = Encoding.UTF8.GetBytes(reportJson);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        var job = await db.LitigationSearchJobs.FirstOrDefaultAsync(j => j.RequestId == requestId);
        if (job is null)
        {
            job = new LitigationSearchJob
            {
                RequestId = requestId, EntityType = "individual", ApplicationCustomerId = "1", KeywordsJson = "[]",
                CreatedUtc = DateTime.UtcNow
            };
            db.LitigationSearchJobs.Add(job);
        }

        job.Status = LitigationSearchJobStatus.Completed;
        job.ReportFormat = BprReportFormat.Json;
        job.RawReportBytes = bytes;
        job.RawResponseHash = hash;
        job.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return job;
    }

    private static string ReportJson(string cnr, string caseType, string caseNo = "22/2020", string cspId = "csp-1", string orderUrl = "https://source.example/o1.pdf") => $$"""
        {
            "request_details": {"job_id": "job-1", "report_date": "2026-09-20", "keywords": ["Test Company"]},
            "district_court": {
                "against": {
                    "civil": [
                        {
                            "_id": "provider-1", "csp_id": "{{cspId}}", "cnr_number": "{{cnr}}",
                            "type": "district", "court": "Sub Judge", "bench": "Bench",
                            "case_no": "{{caseNo}}", "case_type": "{{caseType}}", "case_year": "2020",
                            "case_stage": "Trial", "case_status": "DISPOSED", "act": "Code",
                            "filing_date": "29-01-2020", "state": "Tamil Nadu", "district": "Kancheepuram",
                            "petitioners": ["Petitioner One"], "respondents": ["Respondent One"],
                            "orders": [{"pdf_url": "{{orderUrl}}", "order_date": "09-01-2025", "order_type": "Judgment"}]
                        }
                    ]
                }
            }
        }
        """;

    [Fact]
    public async Task PersistCasesForJobAsync_persists_a_case_its_order_and_a_source_report_link()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A1");
        var job = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("TNKP070001332020", "OS"));
        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);

        await service.PersistCasesForJobAsync(job.LitigationSearchJobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var cases = await verifyDb.LitigationCases.Where(c => c.RequestId == request.RequestId).ToListAsync();
        Assert.Single(cases);
        Assert.Equal("TNKP070001332020", cases[0].Cnr);
        Assert.Equal("csp-1", cases[0].CspId);
        Assert.Equal("Sub Judge", cases[0].Court);

        var orders = await verifyDb.LitigationCaseOrders.Where(o => o.LitigationCaseId == cases[0].LitigationCaseId).ToListAsync();
        Assert.Single(orders);
        Assert.Equal("https://source.example/o1.pdf", orders[0].PdfUrl);

        var sourceReports = await verifyDb.LitigationCaseSourceReports
            .Where(s => s.LitigationCaseId == cases[0].LitigationCaseId).ToListAsync();
        Assert.Single(sourceReports);
        Assert.Equal(job.LitigationSearchJobId, sourceReports[0].LitigationSearchJobId);
    }

    [Fact]
    public async Task PersistCasesForJobAsync_reprocessing_the_same_job_is_idempotent()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A2");
        var job = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("TNKP070001332021", "OS"));
        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);

        await service.PersistCasesForJobAsync(job.LitigationSearchJobId, CancellationToken.None);
        await service.PersistCasesForJobAsync(job.LitigationSearchJobId, CancellationToken.None); // retry — must be a no-op

        await using var verifyDb = CreateContext();
        Assert.Equal(1, await verifyDb.LitigationCases.CountAsync(c => c.RequestId == request.RequestId));
        Assert.Equal(1, await verifyDb.LitigationCaseSourceReports.CountAsync(s => s.LitigationSearchJobId == job.LitigationSearchJobId));
    }

    [Fact]
    public async Task PersistCasesForJobAsync_merges_the_same_case_found_by_a_later_rerun_via_CNR()
    {
        // A request has at most one LitigationSearchJob row, reused in place on every rerun — so "a later
        // search run" means the SAME job id completing again with a new report (matching production's
        // CreateOrResetJobAsync reality), not a second job row. Both runs surface the same real-world case
        // (same CNR, compatible proceeding type): must become ONE LitigationCase row with TWO source-report
        // links (one per distinct report hash), not two separate case rows, and not deduplicated away to one
        // link either.
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A3");
        var job1 = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("TNKP070001332022", "OS"));
        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);
        await service.PersistCasesForJobAsync(job1.LitigationSearchJobId, CancellationToken.None);

        var job2 = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("TNKP070001332022", "OS", caseNo: "23/2020"));
        Assert.Equal(job1.LitigationSearchJobId, job2.LitigationSearchJobId); // same row, reused — not a second job
        await service.PersistCasesForJobAsync(job2.LitigationSearchJobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var cases = await verifyDb.LitigationCases.Where(c => c.RequestId == request.RequestId).ToListAsync();
        Assert.Single(cases);
        // The merged case reflects the later observation's mutable fields (case number updated).
        Assert.Equal("23/2020", cases[0].CaseNumber);

        var links = await verifyDb.LitigationCaseSourceReports.Where(s => s.LitigationCaseId == cases[0].LitigationCaseId).ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(2, links.Select(l => l.ReportHash).Distinct().Count()); // two distinct reports/runs, not deduplicated
        Assert.All(links, l => Assert.Equal(job1.LitigationSearchJobId, l.LitigationSearchJobId));
    }

    [Fact]
    public async Task PersistCasesForJobAsync_never_merges_on_a_CNR_conflict_with_incompatible_proceeding_types()
    {
        // Same CNR, but "OS" normalises to a different proceeding type than "CC"/"C" — CanAutoDedupe requires
        // BOTH a matching CNR and a compatible proceeding type, so these must stay two separate cases. Two
        // reruns of the same (reused) job row, same as the merge-via-CNR test's shape.
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A4");
        var job1 = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("TNKP070001332023", "OS"));
        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);
        await service.PersistCasesForJobAsync(job1.LitigationSearchJobId, CancellationToken.None);

        var job2 = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("TNKP070001332023", "CC"));
        await service.PersistCasesForJobAsync(job2.LitigationSearchJobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var cases = await verifyDb.LitigationCases.Where(c => c.RequestId == request.RequestId).ToListAsync();
        Assert.Equal(2, cases.Count); // never silently merged despite the shared CNR
    }

    [Fact]
    public async Task PersistCasesForJobAsync_never_merges_two_cases_on_a_shared_CSP_ID_alone()
    {
        // CSP ID is retained provider identity, never a merge key — two cases sharing a CSP ID but with no
        // CNR at all must remain two separate rows. The two reruns use different case numbers so their raw
        // bytes (and hash) genuinely differ — otherwise report-level idempotency alone would explain a single
        // row, without ever exercising the "no CNR ⇒ never auto-dedupe" logic this test targets.
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A5");
        var job1 = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("", "OS", caseNo: "22/2020", cspId: "shared-csp"));
        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);
        await service.PersistCasesForJobAsync(job1.LitigationSearchJobId, CancellationToken.None);

        var job2 = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("", "OS", caseNo: "23/2020", cspId: "shared-csp"));
        await service.PersistCasesForJobAsync(job2.LitigationSearchJobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var cases = await verifyDb.LitigationCases.Where(c => c.RequestId == request.RequestId).ToListAsync();
        Assert.Equal(2, cases.Count);
        Assert.All(cases, c => Assert.Equal("shared-csp", c.CspId));
    }

    [Fact]
    public async Task PersistCasesForJobAsync_logs_and_skips_a_non_Json_report_format_without_throwing()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A6");
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, EntityType = "individual",
            ApplicationCustomerId = "1", KeywordsJson = "[]", ReportFormat = BprReportFormat.Xlsx,
            RawReportBytes = [0x50, 0x4B, 0x03, 0x04], CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);

        await service.PersistCasesForJobAsync(job.LitigationSearchJobId, CancellationToken.None); // must not throw

        Assert.Equal(0, await db.LitigationCases.CountAsync(c => c.RequestId == request.RequestId));
    }

    [Fact]
    public async Task FindUnprocessedCompletedJobIdsAsync_returns_completed_jobs_but_not_pending_ones()
    {
        // Deliberately does not try to pre-filter to "unprocessed" (see the method's own doc comment) —
        // PersistCasesForJobAsync's own (job id, report hash) check is what makes re-enqueuing an
        // already-fully-processed job a cheap no-op, so this only needs to prove the Completed-only filter.
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A7");
        var completedJob = await SeedCompletedJobAsync(db, request.RequestId, ReportJson("TNKP070001332024", "OS"));

        var otherRequest = await SeedRequestAsync(db, "A7b");
        var pendingJob = new LitigationSearchJob
        {
            RequestId = otherRequest.RequestId, Status = LitigationSearchJobStatus.Pending, EntityType = "individual",
            ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(pendingJob);
        await db.SaveChangesAsync();

        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);
        var ids = await service.FindUnprocessedCompletedJobIdsAsync(CancellationToken.None);

        Assert.Contains(completedJob.LitigationSearchJobId, ids);
        Assert.DoesNotContain(pendingJob.LitigationSearchJobId, ids);
    }
}
