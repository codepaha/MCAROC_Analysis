using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Bridge;
using MCAROC_Analysis.Services.LitigationModule.PreLogin;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class PreLoginAttributionUiTests : IAsyncLifetime
{
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MCAROC_Analysis"));
        public string ApplicationName { get; set; } = "MCAROC_Analysis.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Root;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(Root);
    }

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

        // Verify automatic exclusion was mirrored to job.DataJson immediately
        var jobAfterApply = await db.PreLoginReportJobs.FirstOrDefaultAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        Assert.NotNull(jobAfterApply);
        var dataAfterApply = JsonSerializer.Deserialize<InstaReportData>(jobAfterApply.DataJson);
        var c4Auto = Assert.Single(dataAfterApply!.LegalCases!.Cases, c => c.CaseRowId == "C-4");
        Assert.True(c4Auto.IsExcludedFromReport);
        Assert.StartsWith("[Auto]", c4Auto.ExclusionReason);

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
    public async Task ApplyOpenSuggestionsAsync_Mirrors_Exclusions_To_Legacy_DataJson()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR6", "CS/600/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "EPSILON EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-6", Petitioner: "EPSILON EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "EPSILON ENTERPRISES LIMITED", "Eve Jackson", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        // 1. Run in Suggest mode
        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        // DataJson should NOT be excluded yet
        var jobSuggest = await db.PreLoginReportJobs.FirstOrDefaultAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        var dataSuggest = JsonSerializer.Deserialize<InstaReportData>(jobSuggest!.DataJson);
        Assert.False(dataSuggest!.LegalCases!.Cases[0].IsExcludedFromReport);

        // 2. Switch to Apply and apply open suggestions
        var appliedCount = await attributionService.ApplyOpenSuggestionsAsync(job.PreLoginReportJobId, "Admin");
        Assert.Equal(1, appliedCount);

        // Verify mirrored into legacy DataJson
        var jobApplied = await db.PreLoginReportJobs.FirstOrDefaultAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        Assert.NotNull(jobApplied);
        var dataApplied = JsonSerializer.Deserialize<InstaReportData>(jobApplied.DataJson);
        var c6 = Assert.Single(dataApplied!.LegalCases!.Cases, c => c.CaseRowId == "C-6");
        Assert.True(c6.IsExcludedFromReport);
        Assert.StartsWith("[Auto]", c6.ExclusionReason);
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

    [Fact]
    public async Task AcceptSuggestionWithSyncAsync_WhenDataJsonCorrupted_FailsAndDoesNotModifyModuleStore()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR7", "CS/700/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "ZETA LOGISTICS EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-7", Petitioner: "ZETA LOGISTICS EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "ZETA LOGISTICS LIMITED", "Zack Taylor", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        var item = Assert.Single(suggestions);

        // Corrupt job DataJson
        job.DataJson = "{ corrupted json not valid";
        await db.SaveChangesAsync();

        // Attempt AcceptSuggestionWithSyncAsync
        var ok = await attributionService.AcceptSuggestionWithSyncAsync(item.ScopeCaseId, "Analyst");
        Assert.False(ok);

        // Verify module store was NOT modified and remains consistent
        await using var verifyDb = Ctx();
        var sc = await verifyDb.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == item.ScopeCaseId);
        Assert.NotNull(sc);
        Assert.False(sc.IsExcluded);

        var decision = await verifyDb.PreLoginAttributionDecisions.FirstOrDefaultAsync(d => d.ScopeCaseId == item.ScopeCaseId && d.Action == PreLoginAttributionAction.Accepted);
        Assert.Null(decision);
    }

    [Fact]
    public async Task AcceptSuggestionWithSyncAsync_WhenDataJsonLacksAlias_FailsAndDoesNotModifyModuleStore()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR8", "CS/800/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "THETA SOLUTIONS EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-8", Petitioner: "THETA SOLUTIONS EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "THETA SOLUTIONS LIMITED", "Tina Fey", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        var item = Assert.Single(suggestions);

        // Modify job DataJson so the case row id does not match (lacks alias C-8)
        var modifiedCases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR8", "CS/800/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "THETA SOLUTIONS EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "DIFFERENT-ALIAS-999", Petitioner: "THETA SOLUTIONS EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        job.DataJson = JsonSerializer.Serialize(new InstaReportData(
            new InstaCompany("THETA SOLUTIONS LIMITED", "-", "U12345", "Company", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"),
            [],
            [],
            new InstaLegalCases(0, 0, 0, 0, 0, 0, 0, 0, 0, modifiedCases, modifiedCases.Count + 1)));
        await db.SaveChangesAsync();

        // Attempt AcceptSuggestionWithSyncAsync
        var ok = await attributionService.AcceptSuggestionWithSyncAsync(item.ScopeCaseId, "Analyst");
        Assert.False(ok);

        // Verify module store was NOT modified
        await using var verifyDb = Ctx();
        var sc = await verifyDb.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == item.ScopeCaseId);
        Assert.NotNull(sc);
        Assert.False(sc.IsExcluded);

        var decision = await verifyDb.PreLoginAttributionDecisions.FirstOrDefaultAsync(d => d.ScopeCaseId == item.ScopeCaseId && d.Action == PreLoginAttributionAction.Accepted);
        Assert.Null(decision);
    }

    [Fact]
    public async Task ApplyOpenSuggestionsAsync_WhenDataJsonCorrupted_ThrowsAndDoesNotModifyModuleStore()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR9", "CS/900/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "IOTA COMMERCE EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-9", Petitioner: "IOTA COMMERCE EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "IOTA COMMERCE LIMITED", "Ian Wright", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        // Corrupt DataJson
        job.DataJson = "corrupt data {";
        await db.SaveChangesAsync();

        // Attempt ApplyOpenSuggestionsAsync
        await Assert.ThrowsAsync<InvalidOperationException>(() => attributionService.ApplyOpenSuggestionsAsync(job.PreLoginReportJobId, "Admin"));

        // Verify module store was NOT modified
        await using var verifyDb = Ctx();
        var sc = await verifyDb.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.Alias == "C-9");
        Assert.NotNull(sc);
        Assert.False(sc.IsExcluded);

        var autoDecision = await verifyDb.PreLoginAttributionDecisions.FirstOrDefaultAsync(d => d.CaseRowId == "C-9" && d.Action == PreLoginAttributionAction.AutomaticallyApplied);
        Assert.Null(autoDecision);
    }

    [Fact]
    public async Task RejectSuggestion_Preserves_AnalystManualExclusion_And_DoesNotModifyDataJson()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR10", "CS/1000/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "KAPPA EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-10", Petitioner: "KAPPA EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "KAPPA INDUSTRIES LIMITED", "Ken Block", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        var item = Assert.Single(suggestions);

        // Simulate analyst manually excluding this case earlier for settlement reasons
        var scBefore = await db.Set<ModuleScopeCase>().FirstAsync(x => x.ScopeCaseId == item.ScopeCaseId);
        scBefore.IsExcluded = true;
        scBefore.ExclusionReason = "Analyst manual settlement exclusion";
        await db.SaveChangesAsync();

        var jobBeforeReject = await db.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        var initialDataJson = jobBeforeReject.DataJson;
        var initialVersion = jobBeforeReject.DataVersion;

        // Analyst rejects the attribution suggestion
        var ok = await attributionService.RejectSuggestionWithSyncAsync(item.ScopeCaseId, "Analyst");
        Assert.True(ok);

        // Verify sc is STILL excluded with the analyst's manual exclusion reason
        await using var verifyDb = Ctx();
        var scAfter = await verifyDb.Set<ModuleScopeCase>().FirstAsync(x => x.ScopeCaseId == item.ScopeCaseId);
        Assert.True(scAfter.IsExcluded);
        Assert.Equal("Analyst manual settlement exclusion", scAfter.ExclusionReason);

        // Verify DataJson and DataVersion were NOT altered
        var jobAfter = await verifyDb.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        Assert.Equal(initialDataJson, jobAfter.DataJson);
        Assert.Equal(initialVersion, jobAfter.DataVersion);

        // Verify decision was recorded as Rejected
        var decision = await verifyDb.PreLoginAttributionDecisions.FirstOrDefaultAsync(d => d.ScopeCaseId == item.ScopeCaseId && d.Action == PreLoginAttributionAction.Rejected);
        Assert.NotNull(decision);
    }

    [Fact]
    public async Task UndoExclusion_WhenNotAutomaticallyApplied_FailsAndDoesNotRestore()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR11", "CS/1100/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "LAMBDA EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-11", Petitioner: "LAMBDA EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "LAMBDA LIMITED", "Larry Page", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var sc = await db.Set<ModuleScopeCase>().FirstAsync(x => x.Alias == "C-11");
        sc.IsExcluded = true;
        sc.ExclusionReason = "Analyst manual exclusion";
        await db.SaveChangesAsync();

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());

        // Attempt undo on a case that was never AutomaticallyApplied
        var undone = await attributionService.UndoExclusionWithSyncAsync(sc.ScopeCaseId, "Analyst");
        Assert.False(undone);

        // Verify case remains excluded
        await using var verifyDb = Ctx();
        var scAfter = await verifyDb.Set<ModuleScopeCase>().FirstAsync(x => x.ScopeCaseId == sc.ScopeCaseId);
        Assert.True(scAfter.IsExcluded);
        Assert.Equal("Analyst manual exclusion", scAfter.ExclusionReason);
    }

    [Fact]
    public async Task TrustReport_WhenNoDecisionsEvaluated_ReturnsNullPrecision()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR12", "CS/1200/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "MU ENTERPRISES EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-12", Petitioner: "MU ENTERPRISES EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "MU ENTERPRISES LIMITED", "Mark Davis", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        // Run in Suggest mode - only Suggested decisions exist (denominator = 0)
        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var report = await attributionService.GetTrustReportAsync(job.PreLoginReportJobId);
        Assert.NotNull(report);
        Assert.Null(report.OverallPrecision);

        var row = Assert.Single(report.Rows);
        Assert.Null(row.Precision);
    }

    [Fact]
    public async Task TrustReport_DateFiltering_CapturesAllEventsUpToExclusiveUtcUpperBound()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR13", "CS/1300/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "NU TRADING EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-13", Petitioner: "NU TRADING EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "NU TRADING LIMITED", "Nancy Wheeler", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        var item = Assert.Single(suggestions);

        // Accept suggestion
        await attributionService.AcceptSuggestionWithSyncAsync(item.ScopeCaseId, "Analyst");

        // Manually adjust the decision timestamp to 2026-10-14 18:00:00 UTC (which is 23:30 IST on Oct 14)
        var decision = await db.PreLoginAttributionDecisions.FirstAsync(d => d.ScopeCaseId == item.ScopeCaseId && d.Action == PreLoginAttributionAction.Accepted);
        decision.DecidedUtc = new DateTime(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        // Query with IST bounds for Oct 14, 2026
        // IST Oct 14 00:00:00 is Oct 13 18:30:00 UTC
        // IST Oct 15 00:00:00 (exclusive upper bound) is Oct 14 18:30:00 UTC
        var startUtc = new DateTime(2026, 10, 13, 18, 30, 0, DateTimeKind.Utc);
        var endUtcExclusive = new DateTime(2026, 10, 14, 18, 30, 0, DateTimeKind.Utc);

        var report = await attributionService.GetTrustReportAsync(job.PreLoginReportJobId, startUtc, endUtcExclusive);
        Assert.NotNull(report);
        Assert.Equal(1, report.TotalAccepted);
        Assert.Equal(1.0, report.OverallPrecision);
    }

    [Fact]
    public async Task Stale_Accept_Cannot_Override_Prior_Rejection_And_Does_Not_Mutate_State()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR21", "CS/2100/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "RHO TRADING EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-21", Petitioner: "RHO TRADING EMPLOYEES UNION", Respondent: "UNION OF INDIA"),
            new("district", "Saket Court", "CNR22", "CS/2200/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "SIGMA TRADING EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-22", Petitioner: "SIGMA TRADING EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "RHO SIGMA LIMITED", "Robert Paul", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        Assert.Equal(2, suggestions.Count);
        var itemC21 = Assert.Single(suggestions, s => s.CaseRowId == "C-21");
        var itemC22 = Assert.Single(suggestions, s => s.CaseRowId == "C-22");

        // Analyst 1 rejects suggestion C-21
        var rejectOk = await attributionService.RejectSuggestionWithSyncAsync(itemC21.ScopeCaseId, "Analyst1");
        Assert.True(rejectOk);

        // Verification after reject: sc is not excluded, job DataJson still includes C-21
        await using var verifyDb1 = Ctx();
        var scAfterReject = await verifyDb1.Set<ModuleScopeCase>().FirstAsync(x => x.ScopeCaseId == itemC21.ScopeCaseId);
        Assert.False(scAfterReject.IsExcluded);

        var jobAfterReject = await verifyDb1.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        var initialDataJson = jobAfterReject.DataJson;
        var initialVersion = jobAfterReject.DataVersion;

        // Duplicate reject should return false
        var dupReject = await attributionService.RejectSuggestionWithSyncAsync(itemC21.ScopeCaseId, "Analyst1");
        Assert.False(dupReject);

        // Tab A tries stale Accept on C-21: MUST FAIL!
        var staleAcceptOk = await attributionService.AcceptSuggestionWithSyncAsync(itemC21.ScopeCaseId, "Analyst2");
        Assert.False(staleAcceptOk);

        // Also test AcceptSuggestionAsync (without sync) fails
        var staleAcceptNoSyncOk = await attributionService.AcceptSuggestionAsync(itemC21.ScopeCaseId, "Analyst2");
        Assert.False(staleAcceptNoSyncOk);

        // Verify C-21 state is completely untouched: NOT excluded, DataJson NOT modified, no Accepted decision
        await using var verifyDb2 = Ctx();
        var scFinal = await verifyDb2.Set<ModuleScopeCase>().FirstAsync(x => x.ScopeCaseId == itemC21.ScopeCaseId);
        Assert.False(scFinal.IsExcluded);
        Assert.True(scFinal.IncludedInReport);

        var jobFinal = await verifyDb2.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        Assert.Equal(initialDataJson, jobFinal.DataJson);
        Assert.Equal(initialVersion, jobFinal.DataVersion);

        var decisionsC21 = await verifyDb2.PreLoginAttributionDecisions.Where(d => d.ScopeCaseId == itemC21.ScopeCaseId).ToListAsync();
        Assert.DoesNotContain(decisionsC21, d => d.Action == PreLoginAttributionAction.Accepted);
        Assert.Contains(decisionsC21, d => d.Action == PreLoginAttributionAction.Rejected);

        // Meanwhile, C-22 is still a pending suggestion and can be accepted normally
        var acceptC22Ok = await attributionService.AcceptSuggestionWithSyncAsync(itemC22.ScopeCaseId, "Analyst1");
        Assert.True(acceptC22Ok);

        // Duplicate accept on C-22 should return false
        var dupAcceptC22 = await attributionService.AcceptSuggestionWithSyncAsync(itemC22.ScopeCaseId, "Analyst1");
        Assert.False(dupAcceptC22);
    }

    [Fact]
    public async Task ApplyOpenSuggestions_Filters_Out_Prior_Rejections()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Saket Court", "CNR23", "CS/2300/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "TAU TRADING EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-23", Petitioner: "TAU TRADING EMPLOYEES UNION", Respondent: "UNION OF INDIA"),
            new("district", "Saket Court", "CNR24", "CS/2400/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "UPSILON TRADING EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-24", Petitioner: "UPSILON TRADING EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "TAU UPSILON LIMITED", "Tina Turner", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
        Assert.Equal(2, suggestions.Count);
        var itemC23 = Assert.Single(suggestions, s => s.CaseRowId == "C-23");
        var itemC24 = Assert.Single(suggestions, s => s.CaseRowId == "C-24");

        // Reject C-23
        var rejectOk = await attributionService.RejectSuggestionWithSyncAsync(itemC23.ScopeCaseId, "Analyst1");
        Assert.True(rejectOk);

        // Apply open suggestions in bulk: only C-24 should be applied!
        var appliedCount = await attributionService.ApplyOpenSuggestionsAsync(job.PreLoginReportJobId, "Analyst1");
        Assert.Equal(1, appliedCount);

        await using var verifyDb = Ctx();
        var scC23 = await verifyDb.Set<ModuleScopeCase>().FirstAsync(x => x.ScopeCaseId == itemC23.ScopeCaseId);
        Assert.False(scC23.IsExcluded);

        var scC24 = await verifyDb.Set<ModuleScopeCase>().FirstAsync(x => x.ScopeCaseId == itemC24.ScopeCaseId);
        Assert.True(scC24.IsExcluded);

        var updatedJob = await verifyDb.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
        var reportData = JsonSerializer.Deserialize<InstaReportData>(updatedJob.DataJson);
        var c23Record = Assert.Single(reportData!.LegalCases!.Cases, c => c.CaseRowId == "C-23");
        Assert.False(c23Record.IsExcludedFromReport);
        var c24Record = Assert.Single(reportData.LegalCases.Cases, c => c.CaseRowId == "C-24");
        Assert.True(c24Record.IsExcludedFromReport);
    }

    [Fact]
    public async Task AcceptSuggestion_Regenerates_Report_Cleans_Old_File_And_Served_By_Download()
    {
        var env = new TestEnvironment();
        var reportService = new PreLoginReportService(null!, env);
        var filesToCleanup = new List<string>();

        try
        {
            await using var db = Ctx();
            var cases = new List<LegalCaseRecord>
            {
                new("district", "Saket Court", "CNR25", "CS/2500/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "PHI CORP EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-25", Petitioner: "PHI CORP EMPLOYEES UNION", Respondent: "UNION OF INDIA")
            };
            var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "PHI CORP LIMITED", "Philip Morris", cases);

            // Create initial dummy report file on disk
            var reportsDir = Path.Combine(env.ContentRootPath, "App_Data", "PreLoginReports");
            Directory.CreateDirectory(reportsDir);
            var initialReportPath = Path.Combine(reportsDir, $"{job.PreLoginReportJobId}-v1-initial.docx");
            await File.WriteAllBytesAsync(initialReportPath, [1, 2, 3, 4]);
            filesToCleanup.Add(initialReportPath);

            job.ReportStoragePath = initialReportPath;
            await db.SaveChangesAsync();

            var backfillService = new PreLoginAssignmentBackfillService(db);
            await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

            var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
            var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider(), null, reportService, env);
            await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

            var suggestions = await attributionService.GetOpenSuggestionsAsync(job.PreLoginReportJobId);
            var item = Assert.Single(suggestions);

            // Accept suggestion with sync
            var ok = await attributionService.AcceptSuggestionWithSyncAsync(item.ScopeCaseId, "Analyst");
            Assert.True(ok);

            await using var verifyDb = Ctx();
            var updatedJob = await verifyDb.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
            Assert.NotNull(updatedJob.ReportStoragePath);
            Assert.NotEqual(initialReportPath, updatedJob.ReportStoragePath);
            filesToCleanup.Add(updatedJob.ReportStoragePath);

            // New report file exists on disk
            Assert.True(File.Exists(updatedJob.ReportStoragePath));
            // Old report file was cleaned up on commit!
            Assert.False(File.Exists(initialReportPath));

            // Verify AssignmentsController.Download serves the newly published file
            var jobService = new PreLoginReportJobService(verifyDb, new PreLoginReportQueue(), reportService, env);
            var controller = new AssignmentsController(verifyDb, jobService, attributionService);
            var downloadResult = await controller.Download(updatedJob.BatchId, updatedJob.PreLoginReportJobId, CancellationToken.None);

            var physicalFile = Assert.IsType<PhysicalFileResult>(downloadResult);
            Assert.Equal(updatedJob.ReportStoragePath, physicalFile.FileName);
        }
        finally
        {
            foreach (var f in filesToCleanup)
            {
                if (File.Exists(f)) { try { File.Delete(f); } catch { } }
            }
        }
    }

    [Fact]
    public async Task UndoExclusion_Regenerates_Report_And_Cleans_Old_File()
    {
        var env = new TestEnvironment();
        var reportService = new PreLoginReportService(null!, env);
        var filesToCleanup = new List<string>();

        try
        {
            await using var db = Ctx();
            var cases = new List<LegalCaseRecord>
            {
                new("district", "Saket Court", "CNR26", "CS/2600/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "CHI TRADING EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-26", Petitioner: "CHI TRADING EMPLOYEES UNION", Respondent: "UNION OF INDIA")
            };
            var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "CHI TRADING LIMITED", "Charles Xavier", cases);

            var reportsDir = Path.Combine(env.ContentRootPath, "App_Data", "PreLoginReports");
            Directory.CreateDirectory(reportsDir);
            var initialReportPath = Path.Combine(reportsDir, $"{job.PreLoginReportJobId}-v1-initial.docx");
            await File.WriteAllBytesAsync(initialReportPath, [1, 2, 3, 4]);
            filesToCleanup.Add(initialReportPath);

            job.ReportStoragePath = initialReportPath;
            await db.SaveChangesAsync();

            var backfillService = new PreLoginAssignmentBackfillService(db);
            await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

            // Run in Apply mode so case is auto-excluded
            var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Apply });
            var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider(), null, reportService, env);
            var runResult = await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);
            Assert.Equal(1, runResult.AutoExcluded);

            var autoExcludedList = await attributionService.GetAutoExcludedCasesAsync(job.PreLoginReportJobId);
            var autoItem = Assert.Single(autoExcludedList);

            await using var checkDb = Ctx();
            var jobAfterApply = await checkDb.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
            var applyReportPath = jobAfterApply.ReportStoragePath;
            if (applyReportPath != null) filesToCleanup.Add(applyReportPath);

            // Undo exclusion with sync
            var undoOk = await attributionService.UndoExclusionWithSyncAsync(autoItem.ScopeCaseId, "Analyst");
            Assert.True(undoOk);

            await using var verifyDb = Ctx();
            var finalJob = await verifyDb.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
            Assert.NotNull(finalJob.ReportStoragePath);
            Assert.NotEqual(applyReportPath, finalJob.ReportStoragePath);
            filesToCleanup.Add(finalJob.ReportStoragePath);

            // New report exists and old is deleted
            Assert.True(File.Exists(finalJob.ReportStoragePath));
            if (applyReportPath != null && !string.Equals(applyReportPath, finalJob.ReportStoragePath, StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(File.Exists(applyReportPath));
            }
        }
        finally
        {
            foreach (var f in filesToCleanup)
            {
                if (File.Exists(f)) { try { File.Delete(f); } catch { } }
            }
        }
    }

    [Fact]
    public async Task ApplyOpenSuggestions_Regenerates_Report_And_Cleans_Old_File()
    {
        var env = new TestEnvironment();
        var reportService = new PreLoginReportService(null!, env);
        var filesToCleanup = new List<string>();

        try
        {
            await using var db = Ctx();
            var cases = new List<LegalCaseRecord>
            {
                new("district", "Saket Court", "CNR27", "CS/2700/2023", "CS", "2023", "PENDING", "-", "2023-02-01", "Delhi", "Delhi", "PSI TRADING EMPLOYEES UNION VS UNION OF INDIA", "2023-06-01", "Pending", CaseRowId: "C-27", Petitioner: "PSI TRADING EMPLOYEES UNION", Respondent: "UNION OF INDIA")
            };
            var (job, borrower, _) = await CreateJobWithTargetsAsync(db, "PSI TRADING LIMITED", "Pam Beesly", cases);

            var reportsDir = Path.Combine(env.ContentRootPath, "App_Data", "PreLoginReports");
            Directory.CreateDirectory(reportsDir);
            var initialReportPath = Path.Combine(reportsDir, $"{job.PreLoginReportJobId}-v1-initial.docx");
            await File.WriteAllBytesAsync(initialReportPath, [1, 2, 3, 4]);
            filesToCleanup.Add(initialReportPath);

            job.ReportStoragePath = initialReportPath;
            await db.SaveChangesAsync();

            var backfillService = new PreLoginAssignmentBackfillService(db);
            await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

            var options = Microsoft.Extensions.Options.Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
            var attributionService = new PreLoginAttributionService(db, options, new ServiceCollection().BuildServiceProvider(), null, reportService, env);
            await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

            var count = await attributionService.ApplyOpenSuggestionsAsync(job.PreLoginReportJobId, "Analyst");
            Assert.Equal(1, count);

            await using var verifyDb = Ctx();
            var updatedJob = await verifyDb.PreLoginReportJobs.FirstAsync(j => j.PreLoginReportJobId == job.PreLoginReportJobId);
            Assert.NotNull(updatedJob.ReportStoragePath);
            Assert.NotEqual(initialReportPath, updatedJob.ReportStoragePath);
            filesToCleanup.Add(updatedJob.ReportStoragePath);

            Assert.True(File.Exists(updatedJob.ReportStoragePath));
            Assert.False(File.Exists(initialReportPath));
        }
        finally
        {
            foreach (var f in filesToCleanup)
            {
                if (File.Exists(f)) { try { File.Delete(f); } catch { } }
            }
        }
    }
}

