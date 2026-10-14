using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Bridge;
using MCAROC_Analysis.Services.LitigationModule.PreLogin;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class PreLoginAttributionUiTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(PreLoginReportJob Job, PreLoginLitigationTarget Borrower, PreLoginLitigationTarget Director)> CreateJobWithTargetsAsync(
        AppDbContext db, string companyName, string directorName, List<LegalCaseRecord> cases)
    {
        var job = new PreLoginReportJob
        {
            Cin = "U" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant(),
            SubmittedCompanyName = companyName,
            Format = "standard",
            Status = PreLoginReportJobStatus.Completed,
            CreatedUtc = DateTime.UtcNow,
            DataVersion = 1,
            DataJson = JsonSerializer.Serialize(new InstaReportData(
                new InstaCompany(companyName, "-", "U12345", "Company", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"),
                [],
                [],
                new InstaLegalCases(0, 0, 0, 0, 0, 0, 0, 0, 0, cases, cases.Count + 1)))
        };
        db.PreLoginReportJobs.Add(job);
        await db.SaveChangesAsync();

        var borrowerTarget = new PreLoginLitigationTarget
        {
            PreLoginReportJobId = job.PreLoginReportJobId,
            TargetKind = PreLoginTargetKind.Borrower,
            TargetIndex = 0,
            TargetName = companyName,
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow
        };
        var directorTarget = new PreLoginLitigationTarget
        {
            PreLoginReportJobId = job.PreLoginReportJobId,
            TargetKind = PreLoginTargetKind.Director,
            TargetIndex = 1,
            TargetName = directorName,
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow
        };
        db.PreLoginLitigationTargets.AddRange(borrowerTarget, directorTarget);
        await db.SaveChangesAsync();

        return (job, borrowerTarget, directorTarget);
    }

    [Fact]
    public async Task Suggestions_Query_Returns_Open_Suggestions_And_Filters_Properly()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("highcourt", "Delhi High Court", "CNR1", "WP/100/2023", "WP", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi", "ALPHA TECH EMPLOYEES UNION VS UNION OF INDIA", "2023-05-01", "Pending", CaseRowId: "C-1", Petitioner: "ALPHA TECH EMPLOYEES UNION", Respondent: "UNION OF INDIA"),
            new("district", "Saket Court", "CNR2", "CS/200/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "BETA CONSUMER FORUM TRUST VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-2", Petitioner: "BETA CONSUMER FORUM TRUST", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, director) = await CreateJobWithTargetsAsync(db, "ALPHA TECH LIMITED", "John Doe", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());

        // Run attribution
        var runResult = await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);
        Assert.True(runResult.SuggestedExclusions >= 1);

        // Test GetOpenSuggestionsAsync
        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        Assert.NotEmpty(suggestions);
        var suggItem = Assert.Single(suggestions, s => s.CaseRowId == "C-1");
        Assert.Equal(job.PreLoginReportJobId, suggItem.JobId);
        Assert.Equal(PreLoginTargetKind.Borrower, suggItem.TargetKind);

        // Test TargetKind filter
        var directorSuggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId, targetKind: PreLoginTargetKind.Director);
        Assert.Empty(directorSuggestions);

        // Test GetCaseAttributionsForJobAsync
        var attributions = await attributionService.GetCaseAttributionsForJobAsync(job.PreLoginReportJobId);
        Assert.True(attributions.ContainsKey("C-1"));
        Assert.True(attributions["C-1"].IsSuggestedExclusion);
        Assert.False(attributions["C-1"].IsAutoExcluded);
    }

    [Fact]
    public async Task Accept_And_Reject_Suggestions_With_Sync_Updates_Both_Stores_Cleanly()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR3", "CS/300/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "BETA LOGISTICS EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-3", Petitioner: "BETA LOGISTICS EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "BETA LOGISTICS LIMITED", "Jane Doe", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        var item = Assert.Single(suggestions);

        // Accept suggestion with dual sync
        var ok = await attributionService.AcceptSuggestionWithSyncAsync(item.ScopeCaseId, "TestAnalyst");
        Assert.True(ok);

        // Verify module scope case is excluded
        var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == item.ScopeCaseId);
        Assert.NotNull(sc);
        Assert.True(sc.IsExcluded);
        Assert.False(sc.IncludedInReport);
        Assert.Contains("[Accepted suggestion]", sc.ExclusionReason);

        // Verify job.DataJson updated and DataVersion incremented
        var updatedJob = await db.PreLoginReportJobs.FirstOrDefaultAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        Assert.NotNull(updatedJob);
        Assert.True(updatedJob.DataVersion > 1);
        var updatedData = JsonSerializer.Deserialize<InstaReportData>(updatedJob.DataJson);
        var c3 = Assert.Single(updatedData!.LegalCases!.Cases, c => c.CaseRowId == "C-3");
        Assert.True(c3.IsExcludedFromReport);
        Assert.Contains("[Accepted suggestion]", c3.ExclusionReason);

        // Verify suggestion is no longer open
        var openNow = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        Assert.Empty(openNow);
    }

    [Fact]
    public async Task Auto_Excluded_List_And_Undo_With_Sync_Restores_Case()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR4", "CS/400/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "GAMMA EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-4", Petitioner: "GAMMA EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "GAMMA INDUSTRIES LIMITED", "Alice Smith", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        // Run attribution in Apply mode
        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Apply });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        var runResult = await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);
        Assert.True(runResult.AutoExcluded >= 1);

        // Verify GetAutoExcludedCasesAsync returns C-4
        var autoList = await attributionService.GetAutoExcludedCasesAsync(job.PreLoginReportJobId);
        var autoItem = Assert.Single(autoList);
        Assert.Equal("C-4", autoItem.CaseRowId);

        // Undo exclusion with sync
        var undone = await attributionService.UndoExclusionWithSyncAsync(autoItem.ScopeCaseId, "TestAnalyst");
        Assert.True(undone);

        // Verify module scope case restored
        var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == autoItem.ScopeCaseId);
        Assert.NotNull(sc);
        Assert.False(sc.IsExcluded);
        Assert.True(sc.IncludedInReport);

        // Verify job.DataJson restored
        var updatedJob = await db.PreLoginReportJobs.FirstOrDefaultAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        Assert.NotNull(updatedJob);
        var updatedData = JsonSerializer.Deserialize<InstaReportData>(updatedJob.DataJson);
        var c4 = Assert.Single(updatedData!.LegalCases!.Cases, c => c.CaseRowId == "C-4");
        Assert.False(c4.IsExcludedFromReport);

        // Verify AutoExcluded list now empty
        var autoListAfter = await attributionService.GetAutoExcludedCasesAsync(job.PreLoginReportJobId);
        Assert.Empty(autoListAfter);
    }

    [Fact]
    public async Task Trust_Report_Computes_Precision_With_TargetKind_And_Date_Breakdown()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR5", "CS/500/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "DELTA INFRA EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-5", Petitioner: "DELTA INFRA EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "DELTA INFRA LIMITED", "Bob Builder", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        var item = Assert.Single(suggestions);

        // Accept suggestion
        await attributionService.AcceptSuggestionWithSyncAsync(item.ScopeCaseId, "Analyst");

        // Record an analyst miss
        await attributionService.RecordAnalystMissAsync(item.ScopeCaseId, "Analyst", "manual-catch");

        // Retrieve Trust Report
        var report = await attributionService.GetTrustReportAsync(job.PreLoginReportJobId);
        Assert.NotNull(report);
        Assert.Equal(job.PreLoginReportJobId, report.JobId);
        Assert.True(report.TotalAccepted >= 1);
        Assert.True(report.TotalMisses >= 1);
        Assert.NotEmpty(report.Rows);
        Assert.NotEmpty(report.TargetRows);

        var targetRow = Assert.Single(report.TargetRows, r => r.TargetKind == PreLoginTargetKind.Borrower);
        Assert.True(targetRow.Accepted >= 1);
        Assert.True(targetRow.AnalystMiss >= 1);

        // Analyst miss must NOT decrease precision denominator: denominator = accepted + autoApplied + rejected = 1. precision = 1.0.
        Assert.Equal(1.0, targetRow.Precision);
    }
}

