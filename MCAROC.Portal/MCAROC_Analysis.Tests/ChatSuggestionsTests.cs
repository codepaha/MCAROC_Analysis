using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#499: the chat panel suggests only questions this request's data answers (it used to offer the same five to every company, EPFO included).</summary>
public class ChatSuggestionsTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(long RequestId, long RunId)> RequestAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "CS" + Guid.NewGuid().ToString("N")[..8], ClientName = "Suggestions", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "Suggestions Co", RequestNumber = $"CS-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        return (request.RequestId, run.IngestionRunId);
    }

    [Fact]
    public async Task A_request_with_no_data_gets_no_suggestions()
    {
        await using var db = Ctx();
        var (requestId, runId) = await RequestAsync(db);
        Assert.Empty(await new ChatSuggestions(db).BuildAsync(requestId, runId, default));
    }

    [Fact]
    public async Task Suggestions_follow_the_data_the_request_holds()
    {
        await using var db = Ctx();
        var (requestId, runId) = await RequestAsync(db);
        db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "100" + Random.Shared.Next(1000, 9999), LatestChargeHolderRaw = "STATE BANK OF INDIA", LatestChargeHolderNormalized = "STATE BANK OF INDIA", ChargeStatus = "Open" });
        db.CompanyNameHistories.Add(new CompanyNameHistory { RequestId = requestId, IngestionRunId = runId, PreviousName = "OLD NAME PRIVATE LIMITED", DisplayOrder = 1 });
        db.LitigationCases.Add(new LitigationCase { RequestId = requestId, CaseNumber = "OA/1/2024", Court = "DRT 1", CaseStatus = "PENDING", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow, AttributionOutcome = LitigationAttributionOutcome.Attributed,
            PetitionersJson = """[{"name":"STATE BANK OF INDIA"}]""", RespondentsJson = """[{"name":"SUGGESTIONS CO"}]""" });   // the company is the respondent
        await db.SaveChangesAsync();

        var s = await new ChatSuggestions(db).BuildAsync(requestId, runId, default);
        Assert.Contains("Which charges are still open, and with which lenders?", s);
        Assert.Contains("When did the company change its name, and what was it called before?", s);
        Assert.Contains("Which pending cases are against the company, and in which courts?", s);
        Assert.DoesNotContain("Are there any delayed EPFO payments?", s);   // no EPFO data
        Assert.DoesNotContain(s, q => q.StartsWith("Show the", StringComparison.Ordinal));   // no filed documents to show
        Assert.True(s.Count <= ChatSuggestions.MaxSuggestions);
    }

    [Fact]
    public async Task A_case_that_is_not_the_companys_suggests_nothing_about_litigation()
    {
        await using var db = Ctx();
        var (requestId, runId) = await RequestAsync(db);
        db.LitigationCases.Add(new LitigationCase { RequestId = requestId, CaseNumber = "OA/2/2024", Court = "DRT 1", CaseStatus = "PENDING", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow, AttributionOutcome = LitigationAttributionOutcome.NotTheCompany });
        await db.SaveChangesAsync();
        Assert.Empty(await new ChatSuggestions(db).BuildAsync(requestId, runId, default));
    }

    [Fact]
    public async Task Each_suggestion_needs_the_data_that_answers_it_exactly()
    {
        // Review of #508: a pending case the company brought is not "against the company"; two rows of one year are not two years; an EPFO row paid on
        // time is not a delay.
        await using var db = Ctx();
        var (requestId, runId) = await RequestAsync(db);
        db.LitigationCases.Add(new LitigationCase { RequestId = requestId, CaseNumber = "OA/3/2024", Court = "DRT 1", CaseStatus = "PENDING", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow, AttributionOutcome = LitigationAttributionOutcome.Attributed,
            PetitionersJson = """[{"name":"SUGGESTIONS CO"}]""", RespondentsJson = """[{"name":"A BORROWER"}]""" });   // brought by the company
        db.FinancialYearData.Add(new FinancialYearData { RequestId = requestId, IngestionRunId = runId, FinancialYear = 2024, Basis = FinancialBasis.Standalone });
        db.FinancialYearData.Add(new FinancialYearData { RequestId = requestId, IngestionRunId = runId, FinancialYear = 2024, Basis = FinancialBasis.Consolidated });
        db.EpfoContributions.Add(new EpfoContribution { RequestId = requestId, IngestionRunId = runId, EstablishmentId = "DLCPM0000001", WageMonth = "Mar-2024",
            PaymentDueDate = new DateOnly(2024, 4, 15), PaymentDate = new DateOnly(2024, 4, 10) });
        await db.SaveChangesAsync();

        var s = await new ChatSuggestions(db).BuildAsync(requestId, runId, default);
        Assert.Contains("Which cases are still pending, and in which courts?", s);
        Assert.DoesNotContain(s, q => q.Contains("against the company"));
        Assert.DoesNotContain(s, q => q.Contains("three years") || q.Contains("two years"));   // one year, two bases
        Assert.DoesNotContain(s, q => q.Contains("EPFO"));                                     // paid on time

        db.FinancialYearData.Add(new FinancialYearData { RequestId = requestId, IngestionRunId = runId, FinancialYear = 2023, Basis = FinancialBasis.Standalone });
        db.EpfoContributions.Add(new EpfoContribution { RequestId = requestId, IngestionRunId = runId, EstablishmentId = "DLCPM0000001", WageMonth = "Apr-2024",
            PaymentDueDate = new DateOnly(2024, 5, 15), PaymentDate = new DateOnly(2024, 6, 2) });
        await db.SaveChangesAsync();
        s = await new ChatSuggestions(db).BuildAsync(requestId, runId, default);
        Assert.Contains("How did revenue, profit and borrowings change between the last two years?", s);
        Assert.Contains("Which EPFO payments were paid late, and by how many days?", s);
    }
}
