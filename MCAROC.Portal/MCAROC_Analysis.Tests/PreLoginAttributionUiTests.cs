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
}

