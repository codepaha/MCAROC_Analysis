using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Integration tests for StructuredFactsProvider against the real .\SQLEXPRESS test database
/// (same pattern as DocumentRetrieverTests). Each test seeds its own request graph with a unique
/// RequestNumber so the shared test DB stays collision-free.</summary>
public class StructuredFactsProviderTests : IAsyncLifetime
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

    private static async Task<(long RequestId, long RunId)> SeedRequestAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Test Co",
            RequestNumber = $"SFP-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var run = new IngestionRun
        {
            RequestId = request.RequestId,
            RunNumber = 1,
            StartedDate = DateTime.UtcNow,
            Status = IngestionRunStatus.CompletedClean
        };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();

        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        await db.SaveChangesAsync();
        return (request.RequestId, run.IngestionRunId);
    }

    [Fact]
    public async Task Charges_detailed_but_all_satisfied_long_ago_still_emits_a_headline_fact()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);

        db.RocCharges.Add(new RocCharge
        {
            RequestId = requestId,
            IngestionRunId = runId,
            RocChargeNumber = "C1",
            LatestChargeHolderRaw = "Old Bank",
            ChargeStatus = "Satisfied",
            CurrentAmount = 10m,
            SatisfactionDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-5))
        });
        await db.SaveChangesAsync();

        var provider = new StructuredFactsProvider(db);
        // FilingCategory.Charge makes the digest treat Charges as a "detailed" domain.
        var hints = new QuestionHints(FilingCategory.Charge, null, null, null, null);

        var facts = await provider.BuildDigestAsync(requestId, hints, CancellationToken.None);

        Assert.Contains(facts, f => f.DomainKey == "Charges");
    }

    [Fact]
    public async Task Charges_detailed_with_a_recent_charge_emits_per_row_detail()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);

        db.RocCharges.Add(new RocCharge
        {
            RequestId = requestId,
            IngestionRunId = runId,
            RocChargeNumber = "C2",
            LatestChargeHolderRaw = "Current Bank",
            ChargeStatus = "Open",
            CurrentAmount = 25m,
            SatisfactionDate = null
        });
        await db.SaveChangesAsync();

        var provider = new StructuredFactsProvider(db);
        var hints = new QuestionHints(FilingCategory.Charge, null, null, null, null);

        var facts = await provider.BuildDigestAsync(requestId, hints, CancellationToken.None);

        Assert.Contains(facts, f => f.DomainKey == "Charges" && f.EntityType == "RocCharge");
    }
}
