using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Owner, 2026-10-26: litigation comes only from the litigation service, whose cases land after the analysis has run. When the
/// request's litigation version moves past the one an analysis was evaluated at, the rules run again (no AI) and the run's rule findings,
/// counts and priority follow; the AI's cross-section findings stay.</summary>
public sealed class LitigationFindingsRefresherTests : IAsyncLifetime
{
    private readonly List<long> _requestIds = [];

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        foreach (var id in _requestIds)
        {
            var caseIds = await db.LitigationCases.Where(c => c.RequestId == id).Select(c => c.LitigationCaseId).ToListAsync();
            await db.LitigationCaseSourceReports.Where(s => caseIds.Contains(s.LitigationCaseId)).ExecuteDeleteAsync();
            await db.LitigationCases.Where(c => c.RequestId == id).ExecuteDeleteAsync();
            await db.LitigationReportSnapshots.Where(s => s.RequestId == id).ExecuteDeleteAsync();
            await db.LitigationSearchJobs.Where(j => j.RequestId == id).ExecuteDeleteAsync();
            await db.AnalysisFindings.Where(f => f.RequestId == id).ExecuteDeleteAsync();
            await db.AnalysisRuns.Where(a => a.RequestId == id).ExecuteDeleteAsync();
            await db.Requests.Where(r => r.RequestId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.LatestCompletedIngestionRunId, (long?)null));
            await db.IngestionRuns.Where(r => r.RequestId == id).ExecuteDeleteAsync();
            await db.Requests.Where(r => r.RequestId == id).ExecuteDeleteAsync();
        }
    }

    private async Task<(long RequestId, long AnalysisRunId)> SeedAnalysedRequestAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1, EntityType = EntityType.Company, CompanyName = "Target Company", RequestNumber = $"LFR-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        _requestIds.Add(request.RequestId);
        var ingestion = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(ingestion);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = ingestion.IngestionRunId;

        // An analysis made before this existed: a finding from the workbook's own litigation rows, and one from the AI.
        var run = new AnalysisRun
        {
            RequestId = request.RequestId, IngestionRunId = ingestion.IngestionRunId, RunNumber = 1, Status = AnalysisRunStatus.Completed,
            StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, ReviewFindingsCount = 1, OverallReviewPriority = ReviewPriority.High
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        db.AnalysisFindings.AddRange(
            new AnalysisFinding
            {
                AnalysisRunId = run.AnalysisRunId, RequestId = request.RequestId, Section = FindingSection.Litigation, Severity = FindingSeverity.Review,
                TemporalStatus = TemporalStatus.Current, Code = LitigationRules.PendingAgainstCompanyCode, Title = "Pending Litigation Against Company",
                SummaryText = "48 pending confirmed case(s) appear to be filed against the company.",
                SourceReferenceJson = "{\"entityType\":\"Litigation\",\"entityIds\":[1,2]}"
            },
            new AnalysisFinding
            {
                AnalysisRunId = run.AnalysisRunId, RequestId = request.RequestId, Section = FindingSection.CrossSection, Severity = FindingSeverity.Watch,
                TemporalStatus = TemporalStatus.Current, Code = AnalysisOrchestrator.AiCrossSectionCodePrefix + "1", Title = "AI", SummaryText = "AI read"
            });
        await db.SaveChangesAsync();
        return (request.RequestId, run.AnalysisRunId);
    }

    private static async Task SeedSearchAsync(AppDbContext db, long requestId, params LitigationCase[] cases)
    {
        var job = new LitigationSearchJob { RequestId = requestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "h" + Guid.NewGuid().ToString("N") };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId, ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        foreach (var c in cases) c.RequestId = requestId;
        db.LitigationCases.AddRange(cases);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.AddRange(cases.Select(c => new LitigationCaseSourceReport
            { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow }));
        await db.SaveChangesAsync();
        await RequestLiveData.BumpAsync(db, requestId, default);
    }

    private static LitigationCase PendingAgainstTheCompany(string number) => new()
    {
        CaseNumber = number, CaseStatus = "PENDING", PetitionersJson = "[\"XYZ BANK\"]", RespondentsJson = "[\"Target Company\"]",
        AttributionOutcome = LitigationAttributionOutcome.Attributed, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
    };

    private static LitigationFindingsRefresher Refresher(AppDbContext db) => new(db, NullLogger<LitigationFindingsRefresher>.Instance);

    [Fact]
    public async Task An_analysis_follows_the_litigation_services_cases_as_they_land_and_keeps_the_AIs_findings()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedAnalysedRequestAsync(db);

        // Before any search: the workbook-based finding goes; the litigation rules are "not evaluated", not "nothing found".
        Assert.Contains(requestId, await Refresher(db).DueAsync(int.MaxValue, default));
        Assert.True(await Refresher(db).RefreshAsync(requestId, default));
        await using (var read = CreateContext())
        {
            var codes = await read.AnalysisFindings.Where(f => f.AnalysisRunId == runId).Select(f => f.Code).ToListAsync();
            Assert.DoesNotContain(LitigationRules.PendingAgainstCompanyCode, codes);
            Assert.Contains(AnalysisOrchestrator.AiCrossSectionCodePrefix + "1", codes);
            var run = await read.AnalysisRuns.SingleAsync(a => a.AnalysisRunId == runId);
            Assert.Equal(0, run.ReviewFindingsCount);
            Assert.Contains(LitigationRules.PendingAgainstCompanyCode, run.DataSufficiencyNotesJson);
            Assert.Equal(await RequestLiveData.ReadAsync(read, requestId, default), run.LitigationFindingsVersion);
        }
        Assert.DoesNotContain(requestId, await Refresher(db).DueAsync(int.MaxValue, default));

        // The search lands with one pending case against the company: the version moves, and the finding cites that case.
        var landed = PendingAgainstTheCompany("WP 1/2024");
        await SeedSearchAsync(db, requestId, landed);
        Assert.Contains(requestId, await Refresher(db).DueAsync(int.MaxValue, default));
        Assert.True(await Refresher(db).RefreshAsync(requestId, default));
        await using (var read = CreateContext())
        {
            var finding = await read.AnalysisFindings.SingleAsync(f => f.AnalysisRunId == runId && f.Code == LitigationRules.PendingAgainstCompanyCode);
            Assert.Equal($"{{\"entityType\":\"LitigationCase\",\"entityIds\":[{landed.LitigationCaseId}]}}", finding.SourceReferenceJson);
            Assert.Equal("1 pending case(s) name the company as the respondent.", finding.SummaryText);
            var run = await read.AnalysisRuns.SingleAsync(a => a.AnalysisRunId == runId);
            Assert.Equal(1, run.ReviewFindingsCount);
            Assert.DoesNotContain(LitigationRules.PendingAgainstCompanyCode, run.DataSufficiencyNotesJson ?? "");
        }

        // Nothing moved since: nothing to do.
        Assert.DoesNotContain(requestId, await Refresher(db).DueAsync(int.MaxValue, default));
        Assert.False(await Refresher(db).RefreshAsync(requestId, default));
    }

    [Fact]
    public async Task The_analysis_context_reads_the_service_cases_at_the_requests_litigation_version()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedAnalysedRequestAsync(db);
        await SeedSearchAsync(db, requestId, PendingAgainstTheCompany("OA 9/2025"));
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);

        var ctx = await AnalysisContextLoader.LoadAsync(db, null, request, request.LatestCompletedIngestionRunId!.Value, default);

        Assert.True(ctx.Litigation.Searched);
        Assert.Single(ctx.Litigation.Cases);
        Assert.Equal(request.LiveDataVersion, ctx.Litigation.LiveDataVersion);
    }
}
