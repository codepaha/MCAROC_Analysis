using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Every analysis since early October 2026 saved none of its rule findings: the run was detached before the save, and detaching a
/// principal also detaches the dependents added under it. Found in the TruBoard test (2026-10-13): the run counted 2 Critical and 8 Review
/// findings with priority High, the dossier showed one AI flag and priority Low.</summary>
public class AnalysisFindingsPersistenceTests : IAsyncLifetime
{
    private static readonly DbContextOptions<AppDbContext> Options =
        new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;

    public async Task InitializeAsync()
    {
        await using var db = new AppDbContext(Options);
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(AppDbContext Db, AnalysisRun Run)> RunningAnalysisAsync()
    {
        var db = new AppDbContext(Options);
        var client = new Client { ClientCode = "AF" + Guid.NewGuid().ToString("N")[..8], ClientName = "Findings Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Findings Test Company",
            RequestNumber = $"AF-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var ingestion = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(ingestion);
        await db.SaveChangesAsync();
        var run = new AnalysisRun
        {
            RequestId = request.RequestId, IngestionRunId = ingestion.IngestionRunId, RunNumber = 1, Status = AnalysisRunStatus.Running,
            StartedDate = DateTime.UtcNow, RuleEngineVersion = "test", PromptVersion = "test"
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        return (db, run);
    }

    private static List<AnalysisFinding> Findings(AnalysisRun run, int n) => Enumerable.Range(0, n).Select(i => new AnalysisFinding
    {
        AnalysisRunId = run.AnalysisRunId, RequestId = run.RequestId, Section = FindingSection.Financial, Severity = FindingSeverity.Review,
        TemporalStatus = TemporalStatus.Current, Code = "TEST_FINDING_" + i, Title = "Finding " + i, SummaryText = "summary"
    }).ToList();

    [Fact]
    public async Task Every_rule_finding_is_saved()
    {
        var (db, run) = await RunningAnalysisAsync();
        await using (db)
        {
            db.AnalysisFindings.AddRange(Findings(run, 14));
            await AnalysisOrchestrator.SaveFindingsWithoutWritingRunAsync(db, run, default);
        }

        await using var verify = new AppDbContext(Options);
        Assert.Equal(14, await verify.AnalysisFindings.CountAsync(f => f.AnalysisRunId == run.AnalysisRunId));
    }

    [Fact]
    public async Task The_run_row_is_not_written_so_a_status_change_made_meanwhile_stands()
    {
        var (db, run) = await RunningAnalysisAsync();
        await using (db)
        {
            // Another writer changes the run while the rule engine works (an operator cancellation, a recovery); the in-memory run still says Running.
            await using (var other = new AppDbContext(Options))
                await other.AnalysisRuns.Where(r => r.AnalysisRunId == run.AnalysisRunId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, AnalysisRunStatus.Failed));
            run.Status = AnalysisRunStatus.Running;

            db.AnalysisFindings.AddRange(Findings(run, 2));
            await AnalysisOrchestrator.SaveFindingsWithoutWritingRunAsync(db, run, default);
            Assert.Equal(EntityState.Detached, db.Entry(run).State);                              // and no later save writes it either
        }

        await using var verify = new AppDbContext(Options);
        Assert.Equal(AnalysisRunStatus.Failed, (await verify.AnalysisRuns.AsNoTracking().SingleAsync(r => r.AnalysisRunId == run.AnalysisRunId)).Status);
    }
}
