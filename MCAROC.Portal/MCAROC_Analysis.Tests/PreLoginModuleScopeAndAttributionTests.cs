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

public class PreLoginModuleScopeAndAttributionTests : IAsyncLifetime
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
    public async Task Backfill_Preserves_Stable_CaseRowIds_And_Is_Idempotent()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "City Civil Court", "CNR1", "CS/1/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Maharashtra", "Mumbai", "Details 1", "2023-05-01", "Pending", CaseRowId: "C-1"),
            new("highcourt", "Bombay High Court", "CNR2", "WP/2/2023", "WP", "2023", "DISPOSED", "-", "2023-02-01", "Maharashtra", "Mumbai", "Director: John Doe (name-match only)", "2023-06-01", "Disposed", CaseRowId: "C-2")
        };
        var (job, borrower, director) = await CreateJobWithTargetsAsync(db, "Alpha Tech Ltd", "John Doe", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        var r1 = await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        Assert.Equal(2, r1.CasesBackfilled);
        Assert.True(r1.CreatedScope);

        var scope = await db.Set<ModuleScope>().FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        Assert.NotNull(scope);
        Assert.Equal(3, scope.NextAliasIndex);
        Assert.Equal(1, scope.Version); // scope version is independent of job.DataVersion

        var scopeCases = await db.Set<ModuleScopeCase>().Where(sc => sc.ScopeId == scope.ScopeId).OrderBy(sc => sc.AliasIndex).ToListAsync();
        Assert.Equal(2, scopeCases.Count);
        Assert.Equal("C-1", scopeCases[0].Alias);
        Assert.Equal(borrower.PreLoginLitigationTargetId, scopeCases[0].TargetId);
        Assert.Equal("C-2", scopeCases[1].Alias);
        Assert.Equal(director.PreLoginLitigationTargetId, scopeCases[1].TargetId);

        // Idempotency: second run does not duplicate
        var r2 = await backfillService.BackfillJobAsync(job.PreLoginReportJobId);
        Assert.Equal(0, r2.CasesBackfilled);
        Assert.False(r2.CreatedScope);

        var countAfter = await db.Set<ModuleScopeCase>().CountAsync(sc => sc.ScopeId == scope.ScopeId);
        Assert.Equal(2, countAfter);
    }

    [Fact]
    public async Task Backfill_Preserves_Analyst_Exclusions_And_Notes()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "City Civil Court", "CNR1", "CS/10/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi", "Details", "2023-05-01", "Pending",
                CaseRowId: "C-1", IsExcludedFromReport: true, ExclusionReason: "Wrong party", AnalystNote: "Confirmed excluded by analyst")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "Beta Logistics Ltd", "Jane Doe", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var sc = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");

        Assert.True(sc.IsExcluded);
        Assert.Equal("Wrong party", sc.ExclusionReason);
        Assert.Equal("Confirmed excluded by analyst", sc.AnalystNote);
        Assert.False(sc.IncludedInReport);
    }

    [Fact]
    public async Task Attribution_Suggest_Mode_Flags_Exclusion_Without_Excluding()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("highcourt", "Delhi High Court", "CNR100", "WP/100/2023", "WP", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "GAMMA EMPLOYEES UNION VS UNION OF INDIA", "2023-05-01", "Pending",
                CaseRowId: "C-1", Petitioner: "GAMMA EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "GAMMA INDUSTRIES LIMITED", "Director Name", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var opt = Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, opt, new ServiceCollection().BuildServiceProvider());

        var res = await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        Assert.Equal(1, res.Assessed);
        Assert.Equal(1, res.NotTheCompany);
        Assert.Equal(1, res.SuggestedExclusions);
        Assert.Equal(0, res.AutoExcluded);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var sc = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");

        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, sc.AttributionOutcome);
        Assert.Equal("other-body", sc.AttributionRule);
        Assert.False(sc.IsExcluded); // Suggest mode does NOT exclude!

        var decision = await db.PreLoginAttributionDecisions.FirstOrDefaultAsync(d => d.ScopeCaseId == sc.ScopeCaseId);
        Assert.NotNull(decision);
        Assert.Equal(PreLoginAttributionAction.Suggested, decision.Action);
        Assert.Equal("other-body", decision.ReasonKind);
    }

    [Fact]
    public async Task Attribution_Apply_Mode_Excludes_NotTheCompany_Only()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("highcourt", "Delhi High Court", "CNR201", "WP/201/2023", "WP", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "DELTA EMPLOYEES UNION VS UNION OF INDIA", "2023-05-01", "Pending",
                CaseRowId: "C-1", Petitioner: "DELTA EMPLOYEES UNION", Respondent: "UNION OF INDIA"),
            new("district", "Civil Court", "CNR202", "CS/202/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "-", "2023-05-01", "Pending", CaseRowId: "C-2") // No parties -> Ambiguous
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "DELTA INFRA LIMITED", "Director Name", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var opt = Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Apply });
        var attributionService = new PreLoginAttributionService(db, opt, new ServiceCollection().BuildServiceProvider());

        var res = await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        Assert.Equal(2, res.Assessed);
        Assert.Equal(1, res.NotTheCompany);
        Assert.Equal(1, res.Ambiguous);
        Assert.Equal(1, res.AutoExcluded);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var sc1 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");
        var sc2 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-2");

        // NotTheCompany is auto-excluded:
        Assert.True(sc1.IsExcluded);
        Assert.StartsWith("[Auto]", sc1.ExclusionReason);

        // Ambiguous is NOT excluded: "could not confirm" stays in report marked for review:
        Assert.False(sc2.IsExcluded);
        Assert.Equal(LitigationAttributionOutcome.Ambiguous, sc2.AttributionOutcome);

        var decisions = await db.PreLoginAttributionDecisions.Where(d => d.PreLoginReportJobId == job.PreLoginReportJobId).ToListAsync();
        Assert.Single(decisions);
        Assert.Equal(PreLoginAttributionAction.AutomaticallyApplied, decisions[0].Action);
    }

    [Fact]
    public async Task Attribution_Director_Uses_Rules_Only_Without_Ai()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "City Court", "CNR301", "CS/301/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "Director: Vikram Sharma (name-match only)", "2023-05-01", "Pending",
                CaseRowId: "C-1", Petitioner: "VIKRAM SHARMA", Respondent: "ICICI BANK LTD"),
            new("district", "City Court", "CNR302", "CS/302/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "Director: Vikram Sharma (name-match only)", "2023-05-01", "Pending",
                CaseRowId: "C-2", Petitioner: "TATA MOTORS LTD", Respondent: "STATE BANK OF INDIA")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "KAPPA CORP", "VIKRAM SHARMA", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var opt = Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Apply });
        var attributionService = new PreLoginAttributionService(db, opt, new ServiceCollection().BuildServiceProvider());

        var res = await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        Assert.Equal(2, res.Assessed);
        Assert.Equal(1, res.Attributed);
        Assert.Equal(1, res.NotTheCompany);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var sc1 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");
        var sc2 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-2");

        Assert.Equal(LitigationAttributionOutcome.Attributed, sc1.AttributionOutcome);
        Assert.Equal("exact-individual-name", sc1.AttributionRule);
        Assert.False(sc1.IsExcluded);

        Assert.Equal(LitigationAttributionOutcome.NotTheCompany, sc2.AttributionOutcome);
        Assert.Equal("corporate-entities-only", sc2.AttributionRule);
        Assert.True(sc2.IsExcluded);
    }

    [Fact]
    public async Task Switching_To_Apply_Mode_Promotes_Open_Suggestions()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("highcourt", "Delhi High Court", "CNR401", "WP/401/2023", "WP", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "LAMBDA EMPLOYEES UNION VS UNION OF INDIA", "2023-05-01", "Pending",
                CaseRowId: "C-1", Petitioner: "LAMBDA EMPLOYEES UNION", Respondent: "UNION OF INDIA")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "LAMBDA STEEL LIMITED", "Director Name", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        // Run in Suggest mode
        var optSuggest = Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, optSuggest, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var sc = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");
        Assert.False(sc.IsExcluded);

        // Switch to Apply: promotes open suggestions
        var applied = await attributionService.ApplyOpenSuggestionsAsync(job.PreLoginReportJobId, "AdminUser");
        Assert.Equal(1, applied);

        var scAfter = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");
        Assert.True(scAfter.IsExcluded);
        Assert.StartsWith("[Auto]", scAfter.ExclusionReason);

        var decision = await db.PreLoginAttributionDecisions.FirstOrDefaultAsync(d => d.ScopeCaseId == sc.ScopeCaseId && d.Action == PreLoginAttributionAction.AutomaticallyApplied);
        Assert.NotNull(decision);
        Assert.Equal("AdminUser", decision.DecidedBy);
    }

    [Fact]
    public async Task Analyst_Decisions_And_Trust_Report_Metrics()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "City Court", "CNR501", "CS/501/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "OMEGA EMPLOYEES UNION VS UNION OF INDIA", "2023-05-01", "Pending",
                CaseRowId: "C-1", Petitioner: "OMEGA EMPLOYEES UNION", Respondent: "UNION OF INDIA"),
            new("district", "City Court", "CNR502", "CS/502/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "OMEGA CREDIT COOPERATIVE SOCIETY VS ICICI", "2023-05-01", "Pending",
                CaseRowId: "C-2", Petitioner: "OMEGA CREDIT COOPERATIVE SOCIETY", Respondent: "ICICI"),
            new("district", "City Court", "CNR503", "CS/503/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "OMEGA INFRA LIMITED VS UNION OF INDIA", "2023-05-01", "Pending",
                CaseRowId: "C-3", Petitioner: "OMEGA INFRA LIMITED", Respondent: "UNION OF INDIA")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "OMEGA INFRA LIMITED", "Director Name", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var opt = Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, opt, new ServiceCollection().BuildServiceProvider());
        await attributionService.CheckAttributionAsync(job.PreLoginReportJobId);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var sc1 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");
        var sc2 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-2");
        var sc3 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-3");

        // Analyst accepts suggestion for C-1
        await attributionService.AcceptSuggestionAsync(sc1.ScopeCaseId, "Analyst1");
        // Analyst rejects suggestion for C-2
        await attributionService.RejectSuggestionAsync(sc2.ScopeCaseId, "Analyst1");
        // Analyst manually excludes C-3 (which was marked Attributed) -> counts as Miss
        await attributionService.RecordAnalystMissAsync(sc3.ScopeCaseId, "Analyst1", "Client says not theirs");

        var report = await attributionService.GetTrustReportAsync(job.PreLoginReportJobId);
        Assert.NotNull(report);
        Assert.True(report.TotalDecisions >= 3);

        var otherBodyRow = report.Rows.FirstOrDefault(r => r.ReasonKind == "other-body");
        Assert.NotNull(otherBodyRow);
        Assert.True(otherBodyRow.Suggested >= 2);
        Assert.Equal(1, otherBodyRow.Accepted);
        Assert.Equal(1, otherBodyRow.Rejected);
        Assert.Equal(0.5, otherBodyRow.Precision); // 1 / (1 + 1), miss is not in precision denominator!

        var missRow = report.Rows.FirstOrDefault(r => r.ReasonKind == "exact");
        Assert.NotNull(missRow);
        Assert.Equal(1, missRow.AnalystMiss);

        Assert.Equal(0.5, report.OverallPrecision); // miss does not penalize precision denominator
    }

    [Fact]
    public async Task PreLoginModuleParity_Reports_Clean_After_Backfill()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "City Civil Court", "CNR601", "CS/601/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi", "Details", "2023-05-01", "Pending", CaseRowId: "C-1"),
            new("highcourt", "Delhi High Court", "CNR602", "WP/602/2023", "WP", "2023", "DISPOSED", "-", "2023-02-01", "Delhi", "Delhi", "Details", "2023-06-01", "Disposed", CaseRowId: "C-2")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "Parity Test Co", "Director One", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var parity = await PreLoginModuleParity.CheckAsync(db, job.PreLoginReportJobId);
        Assert.True(parity.IsClean);
        Assert.Empty(parity.Discrepancies);
        Assert.Equal(2, parity.LegacyCount);
        Assert.Equal(2, parity.ModuleCount);
    }

    [Fact]
    public async Task Pinned_Fields_Win_Over_Feed_In_Module_Reader()
    {
        await using var db = Ctx();
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Feed Court A", "CNR701", "CS/701/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi", "Details", "2023-05-01", "Pending", CaseRowId: "C-1")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "Pinned Field Co", "Director", cases);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var sc = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");

        // Analyst pins court to "Analyst Verified High Court"
        sc.PinnedFieldsJson = JsonSerializer.Serialize(new { Court = "Analyst Verified High Court" });
        await db.SaveChangesAsync();

        var reader = new LitigationModuleReader(db);
        var page = await reader.GetCasesAsync(new ScopeRef(ModuleScopeKind.Assignment, job.PreLoginReportJobId), new CaseFilter(), new PageRequest(), default);

        Assert.Single(page.Items);
        var item = page.Items[0];
        Assert.Equal("Analyst Verified High Court", item.Court); // Pinned value wins over feed!
    }

    [Fact]
    public async Task ReaderGate_Resolves_PreLogin_Modes()
    {
        await using var db = Ctx();
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "Gate Test Co", "Director", []);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        await backfillService.BackfillJobAsync(job.PreLoginReportJobId);

        // Legacy mode
        var optLegacy = Options.Create(new LitigationReaderOptions { PreLoginWorkspace = LitigationReaderMode.Legacy, PreLoginReport = LitigationReaderMode.Legacy });
        var gateLegacy = new LitigationReaderGate(db, optLegacy, Options.Create(new LitigationBridgeOptions()));
        Assert.Equal(CaseSetSource.Legacy, await gateLegacy.ResolvePreLoginWorkspaceAsync(job.PreLoginReportJobId, default));
        Assert.Equal(CaseSetSource.Legacy, await gateLegacy.ResolvePreLoginReportAsync(job.PreLoginReportJobId, default));

        // Module mode
        var optModule = Options.Create(new LitigationReaderOptions { PreLoginWorkspace = LitigationReaderMode.Module, PreLoginReport = LitigationReaderMode.Module });
        var gateModule = new LitigationReaderGate(db, optModule, Options.Create(new LitigationBridgeOptions()));
        Assert.Equal(CaseSetSource.Module, await gateModule.ResolvePreLoginWorkspaceAsync(job.PreLoginReportJobId, default));
        Assert.Equal(CaseSetSource.Module, await gateModule.ResolvePreLoginReportAsync(job.PreLoginReportJobId, default));

        // Shadow mode (serves Legacy while comparing)
        var optShadow = Options.Create(new LitigationReaderOptions { PreLoginWorkspace = LitigationReaderMode.Shadow, PreLoginReport = LitigationReaderMode.Shadow });
        var gateShadow = new LitigationReaderGate(db, optShadow, Options.Create(new LitigationBridgeOptions()));
        Assert.Equal(CaseSetSource.Legacy, await gateShadow.ResolvePreLoginWorkspaceAsync(job.PreLoginReportJobId, default));
        Assert.Equal(CaseSetSource.Legacy, await gateShadow.ResolvePreLoginReportAsync(job.PreLoginReportJobId, default));
    }

    [Fact]
    public async Task Director_Target_Matching_Handles_Overlapping_Names_And_Audit_Records_Director_Kind()
    {
        await using var db = Ctx();
        var job = new PreLoginReportJob
        {
            Cin = "U" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant(),
            SubmittedCompanyName = "Overlap Holdings Ltd",
            Format = "standard",
            Status = PreLoginReportJobStatus.Completed,
            CreatedUtc = DateTime.UtcNow,
            DataVersion = 1
        };
        db.PreLoginReportJobs.Add(job);
        await db.SaveChangesAsync();

        var borrower = new PreLoginLitigationTarget
        {
            PreLoginReportJobId = job.PreLoginReportJobId,
            TargetKind = PreLoginTargetKind.Borrower,
            TargetIndex = 0,
            TargetName = "Overlap Holdings Ltd",
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow
        };
        var dirAnn = new PreLoginLitigationTarget
        {
            PreLoginReportJobId = job.PreLoginReportJobId,
            TargetKind = PreLoginTargetKind.Director,
            TargetIndex = 1,
            TargetName = "Ann",
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow
        };
        var dirAnnLong = new PreLoginLitigationTarget
        {
            PreLoginReportJobId = job.PreLoginReportJobId,
            TargetKind = PreLoginTargetKind.Director,
            TargetIndex = 2,
            TargetName = "Ann Long",
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow
        };
        db.PreLoginLitigationTargets.AddRange(borrower, dirAnn, dirAnnLong);
        await db.SaveChangesAsync();

        var cases = new List<LegalCaseRecord>
        {
            // Case 1: Explicit marker with longer name "Ann Long"
            new("highcourt", "Delhi High Court", "CNR801", "WP/801/2023", "WP", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "Director: Ann Long (name-match only) | Commercial Suit", "2023-05-01", "Pending", CaseRowId: "C-1",
                Petitioner: "BANK", Respondent: "Ann Long"),
            // Case 2: Explicit marker with shorter name "Ann"
            new("highcourt", "Delhi High Court", "CNR802", "WP/802/2023", "WP", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "Director: Ann (name-match only) | Commercial Suit", "2023-05-01", "Pending", CaseRowId: "C-2",
                Petitioner: "BANK", Respondent: "Ann"),
            // Case 3: Without explicit marker, mentions "Ann Long" in case details
            new("district", "City Court", "CNR803", "CS/803/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "Civil Suit against Ann Long by Lender", "2023-05-01", "Pending", CaseRowId: "C-3",
                Petitioner: "LENDER", Respondent: "Ann Long"),
            // Case 4: Without explicit marker, mentions "Ann" as a distinct whole word
            new("district", "City Court", "CNR804", "CS/804/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "Civil Suit against Ann by Lender", "2023-05-01", "Pending", CaseRowId: "C-4",
                Petitioner: "LENDER", Respondent: "Ann"),
            // Case 5: Mentions "Annette" (contains "Ann", but not as whole name) -> should NOT match Ann, should fallback to Borrower
            new("district", "City Court", "CNR805", "CS/805/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi",
                "Civil Suit against Annette by Lender", "2023-05-01", "Pending", CaseRowId: "C-5",
                Petitioner: "LENDER", Respondent: "Annette")
        };

        job.DataJson = JsonSerializer.Serialize(new InstaReportData(
            new InstaCompany("Overlap Holdings Ltd", "-", "U12345", "Company", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"),
            [], [],
            new InstaLegalCases(0, 0, 0, 0, 0, 0, 0, 0, 0, cases, cases.Count + 1)));
        await db.SaveChangesAsync();

        var backfillService = new PreLoginAssignmentBackfillService(db);
        var r = await backfillService.BackfillJobAsync(job.PreLoginReportJobId);
        Assert.Equal(5, r.CasesBackfilled);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        var scCases = await db.Set<ModuleScopeCase>().Where(sc => sc.ScopeId == scope.ScopeId).ToDictionaryAsync(sc => sc.Alias);

        // Case 1 matched "Ann Long" despite "Ann" also being a target
        Assert.Equal(dirAnnLong.PreLoginLitigationTargetId, scCases["C-1"].TargetId);
        // Case 2 matched "Ann"
        Assert.Equal(dirAnn.PreLoginLitigationTargetId, scCases["C-2"].TargetId);
        // Case 3 matched "Ann Long" by longest-match whole-name rule
        Assert.Equal(dirAnnLong.PreLoginLitigationTargetId, scCases["C-3"].TargetId);
        // Case 4 matched "Ann" by whole-name rule
        Assert.Equal(dirAnn.PreLoginLitigationTargetId, scCases["C-4"].TargetId);
        // Case 5 "Annette" did NOT match "Ann" and fell back to borrower
        Assert.Equal(borrower.PreLoginLitigationTargetId, scCases["C-5"].TargetId);

        // Verify audit decisions record PreLoginTargetKind.Director for director cases
        var opt = Options.Create(new PreLoginAttributionOptions { Mode = PreLoginAttributionMode.Suggest });
        var attributionService = new PreLoginAttributionService(db, opt, new ServiceCollection().BuildServiceProvider());

        // Simulate analyst accept on director case C-1
        var sc1 = scCases["C-1"];
        sc1.AttributionParty = "Some Corp";
        sc1.AttributionReason = "Different party";
        sc1.AttributionRule = "rule-1";
        sc1.AttributionOutcome = LitigationAttributionOutcome.NotTheCompany;
        await db.SaveChangesAsync();

        await attributionService.AcceptSuggestionAsync(sc1.ScopeCaseId, "Analyst1");

        var decision = await db.PreLoginAttributionDecisions.FirstOrDefaultAsync(d => d.ScopeCaseId == sc1.ScopeCaseId && d.Action == PreLoginAttributionAction.Accepted);
        Assert.NotNull(decision);
        Assert.Equal(PreLoginTargetKind.Director, decision.TargetKind);
        Assert.Equal(dirAnnLong.PreLoginLitigationTargetId, decision.TargetId);
    }

    [Fact]
    public async Task ReRunning_Backfill_Reconciles_Changed_Legacy_DataJson_And_Preserves_Parity()
    {
        await using var db = Ctx();
        var casesInitial = new List<LegalCaseRecord>
        {
            new("district", "City Civil Court", "CNR901", "CS/901/2023", "CS", "2023", "PENDING", "-", "2023-01-01", "Delhi", "Delhi", "Details 1", "2023-05-01", "Pending", CaseRowId: "C-1"),
            new("highcourt", "Delhi High Court", "CNR902", "WP/902/2023", "WP", "2023", "DISPOSED", "-", "2023-02-01", "Delhi", "Delhi", "Details 2", "2023-06-01", "Disposed", CaseRowId: "C-2")
        };
        var (job, _, _) = await CreateJobWithTargetsAsync(db, "Reconcile Co Ltd", "Director Reconcile", casesInitial);

        var backfillService = new PreLoginAssignmentBackfillService(db);
        var r1 = await backfillService.BackfillJobAsync(job.PreLoginReportJobId);
        Assert.Equal(2, r1.CasesBackfilled);
        Assert.Equal(0, r1.UpdatedCases);

        var parity1 = await PreLoginModuleParity.CheckAsync(db, job.PreLoginReportJobId);
        Assert.True(parity1.IsClean);

        var scope = await db.Set<ModuleScope>().FirstAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == job.PreLoginReportJobId);
        Assert.Equal(1, scope.Version);

        // Simulate subsequent changes to legacy DataJson:
        // - C-1 gets excluded by an analyst with reason and note
        // - C-2 gets an updated analyst note marking it (not in latest feed)
        // - C-3 is an incoming new case
        var updatedCases = new List<LegalCaseRecord>
        {
            casesInitial[0] with { IsExcludedFromReport = true, ExclusionReason = "Not borrower entity", AnalystNote = "Analyst verified" },
            casesInitial[1] with { AnalystNote = "Old matter (not in latest feed)" },
            new("district", "District Court", "CNR903", "CS/903/2023", "CS", "2023", "PENDING", "-", "2023-03-01", "Delhi", "Delhi", "Details 3", "2023-07-01", "Pending", CaseRowId: "C-3")
        };

        job.DataJson = JsonSerializer.Serialize(new InstaReportData(
            new InstaCompany("Reconcile Co Ltd", "-", "U12345", "Company", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"),
            [], [],
            new InstaLegalCases(0, 0, 0, 0, 0, 0, 0, 0, 0, updatedCases, 4)));
        job.DataVersion++;
        await db.SaveChangesAsync();

        // Re-run backfill
        var r2 = await backfillService.BackfillJobAsync(job.PreLoginReportJobId);
        Assert.Equal(1, r2.CasesBackfilled); // C-3 added
        Assert.Equal(2, r2.UpdatedCases); // C-1 and C-2 reconciled

        await db.Entry(scope).ReloadAsync();
        Assert.Equal(2, scope.Version); // Version bumped on change
        Assert.Equal(4, scope.NextAliasIndex);

        var sc1 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-1");
        Assert.True(sc1.IsExcluded);
        Assert.False(sc1.IncludedInReport);
        Assert.Equal("Not borrower entity", sc1.ExclusionReason);
        Assert.Equal("Analyst verified", sc1.AnalystNote);

        var sc2 = await db.Set<ModuleScopeCase>().FirstAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-2");
        Assert.Equal("Old matter (not in latest feed)", sc2.AnalystNote);
        Assert.Null(sc2.FeedLastSeenUtc);

        var sc3 = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(s => s.ScopeId == scope.ScopeId && s.Alias == "C-3");
        Assert.NotNull(sc3);

        // Parity checker must report zero discrepancies after backfill reconciliation
        var parity2 = await PreLoginModuleParity.CheckAsync(db, job.PreLoginReportJobId);
        Assert.True(parity2.IsClean);
        Assert.Empty(parity2.Discrepancies);
        Assert.Equal(3, parity2.LegacyCount);
        Assert.Equal(3, parity2.ModuleCount);
    }
}

