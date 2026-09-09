using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Integration tests against the real .\SQLEXPRESS test database — same pattern as
/// FilingClaimSemanticsTests.cs/DocumentRetrieverTests.cs. Covers the genuinely dangerous parts of the
/// dashboard's aggregation logic: latest-run-only joins (never raw RequestId), CIN-based company dedup
/// (never raw RequestId), Historical/CompletedWithErrors handling, and the decoupled-pipeline overlap.</summary>
public class DashboardQueryServiceTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Client> SeedClientAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = $"T{Guid.NewGuid():N}"[..10], ClientName = "Test Client", IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private async Task<McaRequest> SeedRequestAsync(AppDbContext db, Client client, string? cin = null,
        RequestStatus status = RequestStatus.AnalysisCompleted, DateTime? createdDate = null)
    {
        var request = new McaRequest
        {
            ClientId = client.ClientId, EntityType = EntityType.Company, CompanyName = "Test Co",
            Cin = cin ?? $"CIN{Guid.NewGuid():N}"[..15], RequestNumber = $"TEST-{Guid.NewGuid():N}",
            RequestStatus = status, CreatedDate = createdDate ?? DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private async Task<AnalysisRun> SeedAnalysisRunAsync(AppDbContext db, McaRequest request, int runNumber,
        AnalysisRunStatus status = AnalysisRunStatus.Completed, ReviewPriority? priority = null)
    {
        var run = new AnalysisRun
        {
            RequestId = request.RequestId, IngestionRunId = 1, RunNumber = runNumber, Status = status,
            StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, OverallReviewPriority = priority
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task SeedFindingAsync(AppDbContext db, AnalysisRun run, string code, FindingSeverity severity,
        TemporalStatus temporal = TemporalStatus.Current, FindingSection section = FindingSection.Financial)
    {
        db.AnalysisFindings.Add(new AnalysisFinding
        {
            AnalysisRunId = run.AnalysisRunId, RequestId = run.RequestId, Section = section, Severity = severity,
            TemporalStatus = temporal, Code = code, Title = $"{code} title", SummaryText = "x"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SupersededRunFindings_NeverAppearInAnyAggregate()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var request = await SeedRequestAsync(db, client);
        var oldRun = await SeedAnalysisRunAsync(db, request, runNumber: 1);
        await SeedFindingAsync(db, oldRun, "OLD_CRITICAL", FindingSeverity.Critical);
        var newRun = await SeedAnalysisRunAsync(db, request, runNumber: 2);
        await SeedFindingAsync(db, newRun, "NEW_REVIEW", FindingSeverity.Review);

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        Assert.Equal(0, vm.CriticalFindingsCount); // OLD_CRITICAL belongs to the superseded run
        Assert.Equal(1, vm.ReviewFindingsCount);
    }

    [Fact]
    public async Task CriticalFindings_CompanyCountDedupsByCinAcrossMultipleRequests_NotByRequestId()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var cin = $"CIN{Guid.NewGuid():N}"[..15];

        var requestA1 = await SeedRequestAsync(db, client, cin: cin);
        var runA1 = await SeedAnalysisRunAsync(db, requestA1, 1);
        await SeedFindingAsync(db, runA1, "CRIT_1", FindingSeverity.Critical);

        var requestA2 = await SeedRequestAsync(db, client, cin: cin); // same company, different request
        var runA2 = await SeedAnalysisRunAsync(db, requestA2, 1);
        await SeedFindingAsync(db, runA2, "CRIT_2", FindingSeverity.Critical);
        await SeedFindingAsync(db, runA2, "CRIT_3", FindingSeverity.Critical);

        var requestB = await SeedRequestAsync(db, client); // different company
        var runB = await SeedAnalysisRunAsync(db, requestB, 1);
        await SeedFindingAsync(db, runB, "CRIT_4", FindingSeverity.Critical);

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        Assert.Equal(4, vm.CriticalFindingsCount);
        Assert.Equal(2, vm.CriticalFindingsCompanyCount); // not 3 — proves CIN dedup, not RequestId
    }

    [Fact]
    public async Task TopRiskIndicators_RankByDistinctCompanies_ThroughTheFullPipeline()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);

        // WIDESPREAD: 2 findings, 2 different companies.
        var reqW1 = await SeedRequestAsync(db, client);
        var runW1 = await SeedAnalysisRunAsync(db, reqW1, 1);
        await SeedFindingAsync(db, runW1, "WIDESPREAD", FindingSeverity.Critical);
        var reqW2 = await SeedRequestAsync(db, client);
        var runW2 = await SeedAnalysisRunAsync(db, reqW2, 1);
        await SeedFindingAsync(db, runW2, "WIDESPREAD", FindingSeverity.Critical);

        // CONCENTRATED: 5 findings, 1 company.
        var reqC = await SeedRequestAsync(db, client);
        var runC = await SeedAnalysisRunAsync(db, reqC, 1);
        for (var i = 0; i < 5; i++) await SeedFindingAsync(db, runC, "CONCENTRATED", FindingSeverity.Critical);

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        Assert.Equal("WIDESPREAD", vm.TopRiskIndicators[0].Code);
        Assert.Equal("CONCENTRATED", vm.TopRiskIndicators[1].Code);
    }

    [Fact]
    public async Task PositiveAndHistoricalFindings_ExcludedFromTopRiskIndicatorsAndSectionChart()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var request = await SeedRequestAsync(db, client);
        var run = await SeedAnalysisRunAsync(db, request, 1);
        await SeedFindingAsync(db, run, "STRONG_LIQUIDITY", FindingSeverity.Positive);
        await SeedFindingAsync(db, run, "OLD_ISSUE", FindingSeverity.Critical, TemporalStatus.Historical);
        await SeedFindingAsync(db, run, "CURRENT_ISSUE", FindingSeverity.Critical);

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        Assert.DoesNotContain(vm.TopRiskIndicators, r => r.Code is "STRONG_LIQUIDITY" or "OLD_ISSUE");
        Assert.Contains(vm.TopRiskIndicators, r => r.Code == "CURRENT_ISSUE");
        Assert.DoesNotContain(vm.FindingsBySection, s => s.Severity == FindingSeverity.Positive);
        Assert.Equal(1, vm.PositiveFindingsCount); // Positive still counted in its own plain KPI
    }

    [Fact]
    public async Task AnalysisCompletedRequestWithActiveFilingBatch_CountsUnderBothAnalysisCompletedAndProcessing()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var request = await SeedRequestAsync(db, client, status: RequestStatus.AnalysisCompleted);
        db.McaFilingBatches.Add(new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = 1, Status = FilingBatchStatus.Processing, StartedDate = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        Assert.Equal(1, vm.AnalysisCompletedCount);
        Assert.Equal(1, vm.ProcessingCount); // the intentional, documented overlap
    }

    [Fact]
    public async Task CompletedWithErrorsRun_FindingsCountNormallyInEveryRiskAggregate()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var request = await SeedRequestAsync(db, client);
        var run = await SeedAnalysisRunAsync(db, request, 1, status: AnalysisRunStatus.CompletedWithErrors);
        await SeedFindingAsync(db, run, "STILL_VALID", FindingSeverity.Critical);

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        // Only the AI narrative layer failed in CompletedWithErrors — deterministic findings remain valid
        // and must not be excluded from any risk aggregate.
        Assert.Equal(1, vm.CriticalFindingsCount);
    }

    [Fact]
    public async Task PriorityRequests_ExcludesLowPriorityWithNoAttentionFlag()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);

        var lowNoAttention = await SeedRequestAsync(db, client);
        await SeedAnalysisRunAsync(db, lowNoAttention, 1, priority: ReviewPriority.Low);

        var highPriority = await SeedRequestAsync(db, client);
        await SeedAnalysisRunAsync(db, highPriority, 1, priority: ReviewPriority.High);

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        Assert.DoesNotContain(vm.PriorityRequests, r => r.Request.RequestId == lowNoAttention.RequestId);
        Assert.Contains(vm.PriorityRequests, r => r.Request.RequestId == highPriority.RequestId);
    }

    [Fact]
    public async Task PriorityRequests_IncludesLowPriorityRequest_WhenAttentionRequiredAlso()
    {
        await using var db = CreateContext();
        var client = await SeedClientAsync(db);
        var request = await SeedRequestAsync(db, client);
        request.IsManualReviewRequired = true;
        await db.SaveChangesAsync();
        await SeedAnalysisRunAsync(db, request, 1, priority: ReviewPriority.Low);

        var vm = await new DashboardQueryService(db).BuildAsync(new DashboardFilterCriteria { ClientId = client.ClientId, DateFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) });

        Assert.Contains(vm.PriorityRequests, r => r.Request.RequestId == request.RequestId);
    }
}
