using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers LitigationCasePersistenceService's crash-safety, concurrency-safety and case-level
/// CNR-first de-duplication guarantees — see LitigationReportSnapshot's own remarks for the design this
/// exercises: an immutable per-report snapshot that is also the atomic, RowVersion-protected, resumable unit
/// of import work.</summary>
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

    private static string TwoCaseReportJson(string cnr1, string cnr2) => $$"""
        {
            "request_details": {"job_id": "job-1", "report_date": "2026-09-20", "keywords": ["Test Company"]},
            "district_court": {
                "against": {
                    "civil": [
                        {
                            "_id": "provider-1", "csp_id": "csp-1", "cnr_number": "{{cnr1}}",
                            "type": "district", "court": "Sub Judge A", "bench": "Bench A",
                            "case_no": "22/2020", "case_type": "OS", "case_year": "2020",
                            "case_stage": "Trial", "case_status": "DISPOSED", "act": "Code",
                            "orders": []
                        },
                        {
                            "_id": "provider-2", "csp_id": "csp-2", "cnr_number": "{{cnr2}}",
                            "type": "district", "court": "Sub Judge B", "bench": "Bench B",
                            "case_no": "23/2020", "case_type": "OS", "case_year": "2020",
                            "case_stage": "Trial", "case_status": "PENDING", "act": "Code",
                            "orders": []
                        }
                    ]
                }
            }
        }
        """;

    private static async Task<LitigationReportSnapshot> GetSnapshotAsync(AppDbContext db, long jobId, string reportHash) =>
        await db.LitigationReportSnapshots.FirstAsync(s => s.LitigationSearchJobId == jobId && s.ReportHash == reportHash);

    [Fact]
    public async Task PersistCasesForJobAsync_persists_a_case_its_order_and_a_provenance_linked_snapshot()
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

        var snapshot = await GetSnapshotAsync(verifyDb, job.LitigationSearchJobId, job.RawResponseHash!);
        Assert.Equal(LitigationReportSnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(1, snapshot.CasesPersistedCount);

        var sourceReports = await verifyDb.LitigationCaseSourceReports
            .Where(s => s.LitigationCaseId == cases[0].LitigationCaseId).ToListAsync();
        Assert.Single(sourceReports);
        Assert.Equal(snapshot.LitigationReportSnapshotId, sourceReports[0].LitigationReportSnapshotId);
        // Per-observation identity, captured on the link itself — not only on the mutable canonical case row.
        Assert.Equal("provider-1", sourceReports[0].ProviderCaseId);
        Assert.Equal("csp-1", sourceReports[0].CspId);
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
        var snapshot = await GetSnapshotAsync(verifyDb, job.LitigationSearchJobId, job.RawResponseHash!);
        Assert.Equal(1, await verifyDb.LitigationCaseSourceReports.CountAsync(s => s.LitigationReportSnapshotId == snapshot.LitigationReportSnapshotId));
    }

    [Fact]
    public async Task PersistCasesForJobAsync_resumes_after_a_crash_between_cases_without_duplicating_the_first()
    {
        // A two-case report; simulate a worker that committed case 1 (case row + source-report link,
        // CasesPersistedCount=1) then crashed before case 2 — the snapshot is left InProgress with an expired
        // lease, exactly what a real crash leaves behind. A fresh call must reclaim it, resume from case 2
        // (not reprocess case 1 — no duplicate), and end with both cases present exactly once.
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "B1");
        var reportJson = TwoCaseReportJson("TNKP070001332030", "TNKP070001332031");
        var job = await SeedCompletedJobAsync(db, request.RequestId, reportJson);

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, ReportHash = job.RawResponseHash!, ReportFormat = BprReportFormat.Json,
            RawReportBytes = job.RawReportBytes!, RawReportByteLength = job.RawReportBytes!.LongLength, RetrievedUtc = DateTime.UtcNow,
            Status = LitigationReportSnapshotStatus.InProgress, AttemptCount = 1, CasesPersistedCount = 1,
            LeaseOwner = "crashed-worker:111", LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5), // expired — reclaimable
            StartedUtc = DateTime.UtcNow.AddMinutes(-10), CreatedUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        db.LitigationReportSnapshots.Add(snapshot);
        var firstCase = new LitigationCase
        {
            RequestId = request.RequestId, Cnr = "TNKP070001332030", ProceedingType = "OS", Court = "Sub Judge A",
            FirstSeenUtc = DateTime.UtcNow.AddMinutes(-10), LastSeenUtc = DateTime.UtcNow.AddMinutes(-10)
        };
        db.LitigationCases.Add(firstCase);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            Case = firstCase, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
            ProviderCaseId = "provider-1", CspId = "csp-1", FirstSeenUtc = DateTime.UtcNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);
        await service.PersistCasesForJobAsync(job.LitigationSearchJobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var cases = await verifyDb.LitigationCases.Where(c => c.RequestId == request.RequestId).OrderBy(c => c.Cnr).ToListAsync();
        Assert.Equal(2, cases.Count); // case 1 not duplicated, case 2 newly added
        Assert.Equal("TNKP070001332030", cases[0].Cnr);
        Assert.Equal("TNKP070001332031", cases[1].Cnr);
        Assert.Equal("Sub Judge B", cases[1].Court); // case 2's real data, proving it was actually processed

        var reloadedSnapshot = await GetSnapshotAsync(verifyDb, job.LitigationSearchJobId, job.RawResponseHash!);
        Assert.Equal(LitigationReportSnapshotStatus.Completed, reloadedSnapshot.Status);
        Assert.Equal(2, reloadedSnapshot.CasesPersistedCount);

        var links = await verifyDb.LitigationCaseSourceReports
            .Where(s => s.LitigationReportSnapshotId == reloadedSnapshot.LitigationReportSnapshotId).ToListAsync();
        Assert.Equal(2, links.Count); // one link per case, no duplicate for case 1
    }

    [Fact]
    public async Task PersistCasesForJobAsync_two_concurrent_contexts_processing_the_same_report_never_duplicate_anything()
    {
        // Forces real concurrency: two independent AppDbContext instances (separate connections, exactly like
        // two worker processes) both call PersistCasesForJobAsync for the same completed job at the same time.
        await using var setupDb = CreateContext();
        var request = await SeedRequestAsync(setupDb, "B2");
        var job = await SeedCompletedJobAsync(setupDb, request.RequestId, ReportJson("TNKP070001332032", "OS"));
        var jobId = job.LitigationSearchJobId;
        var reportHash = job.RawResponseHash!;

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();
        var serviceA = new LitigationCasePersistenceService(dbA, NullLogger<LitigationCasePersistenceService>.Instance);
        var serviceB = new LitigationCasePersistenceService(dbB, NullLogger<LitigationCasePersistenceService>.Instance);

        await Task.WhenAll(
            serviceA.PersistCasesForJobAsync(jobId, CancellationToken.None),
            serviceB.PersistCasesForJobAsync(jobId, CancellationToken.None));

        await using var verifyDb = CreateContext();
        var cases = await verifyDb.LitigationCases.Where(c => c.RequestId == request.RequestId).ToListAsync();
        Assert.Single(cases); // exactly one — not two from the race

        var orders = await verifyDb.LitigationCaseOrders.Where(o => o.LitigationCaseId == cases[0].LitigationCaseId).ToListAsync();
        Assert.Single(orders);

        var snapshot = await GetSnapshotAsync(verifyDb, jobId, reportHash);
        Assert.Equal(LitigationReportSnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(1, snapshot.CasesPersistedCount);

        var links = await verifyDb.LitigationCaseSourceReports
            .Where(s => s.LitigationReportSnapshotId == snapshot.LitigationReportSnapshotId).ToListAsync();
        Assert.Single(links); // exactly one link, not two
    }

    [Fact]
    public async Task PersistCasesForJobAsync_merges_the_same_case_found_by_a_later_rerun_via_CNR()
    {
        // A request has at most one LitigationSearchJob row, reused in place on every rerun — so "a later
        // search run" means the SAME job id completing again with a new report (matching production's
        // CreateOrResetJobAsync reality), not a second job row. Both runs surface the same real-world case
        // (same CNR, compatible proceeding type): must become ONE LitigationCase row with TWO source-report
        // links (one per distinct report snapshot), not two separate case rows, and not deduplicated to one.
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
        Assert.Equal(2, links.Select(l => l.LitigationReportSnapshotId).Distinct().Count()); // two distinct snapshots, not deduplicated

        var snapshots = await verifyDb.LitigationReportSnapshots.Where(s => s.LitigationSearchJobId == job1.LitigationSearchJobId).ToListAsync();
        Assert.Equal(2, snapshots.Count); // one immutable snapshot per rerun, both preserved
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
    public async Task PersistCasesForJobAsync_marks_a_non_Json_report_format_Failed_without_throwing()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db, "A6");
        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, EntityType = "individual",
            ApplicationCustomerId = "1", KeywordsJson = "[]", ReportFormat = BprReportFormat.Xlsx,
            RawReportBytes = [0x50, 0x4B, 0x03, 0x04], RawResponseHash = "deadbeef", CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var service = new LitigationCasePersistenceService(db, NullLogger<LitigationCasePersistenceService>.Instance);

        await service.PersistCasesForJobAsync(job.LitigationSearchJobId, CancellationToken.None); // must not throw

        Assert.Equal(0, await db.LitigationCases.CountAsync(c => c.RequestId == request.RequestId));
        var snapshot = await GetSnapshotAsync(db, job.LitigationSearchJobId, "deadbeef");
        Assert.Equal(LitigationReportSnapshotStatus.Failed, snapshot.Status); // terminal — never retried forever
        Assert.NotNull(snapshot.FailureReason);
    }

    [Fact]
    public async Task FindUnprocessedCompletedJobIdsAsync_returns_completed_jobs_but_not_pending_ones()
    {
        // Deliberately does not try to pre-filter to "unprocessed" (see the method's own doc comment) —
        // PersistCasesForJobAsync's own snapshot lookup is what makes re-enqueuing an already-fully-processed
        // job a cheap no-op, so this only needs to prove the Completed-only filter.
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
