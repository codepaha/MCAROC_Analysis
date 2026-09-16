using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Confirms the exact EF query AnalysisOrchestrator.RunAnalysisAsync uses for the charges narrative
/// (RocCharges.Include(Events), filtered by RequestId/IngestionRunId, no ChargeStatus filter in the SQL)
/// translates correctly against a real SQL Server test database — independent of the in-memory
/// selection/ordering logic already covered by AiChargesNarrativeServiceValidationTests. Same boundary as
/// AnalysisClaimAndRecoveryTests: AnalysisOrchestrator itself can't be constructed in-process because
/// AiCrossSectionAnalysisService's constructor eagerly loads Google Cloud credentials from disk. Requires
/// .\SQLEXPRESS.</summary>
public class AiChargesNarrativeQueryTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheOrchestratorsChargesQuery_ReturnsOnlyThisRunsCharges_WithEventsIncluded()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        // Mirrors AnalysisOrchestrator.RunAnalysisAsync's charges-narrative query exactly — no ChargeStatus
        // filter in the SQL; "open" is decided afterward, in memory, via DossierComputations.IsOpenCharge.
        var charges = await db.RocCharges.Include(c => c.Events)
            .Where(c => c.RequestId == requestId && c.IngestionRunId == ingestionRunId).ToListAsync();

        // The seed's 3 charges: C1 (open, modified, 2 events), C2 (open, 1 event), C3 (satisfied, 2 events).
        Assert.Equal(3, charges.Count);
        var c1 = Assert.Single(charges, c => c.RocChargeNumber == "C1");
        Assert.Equal(2, c1.Events.Count);
        var c3 = Assert.Single(charges, c => c.RocChargeNumber == "C3");
        Assert.Equal("Satisfied", c3.ChargeStatus);

        var openCharges = DossierComputations.OpenChargesByAmount(charges);
        Assert.Equal(2, openCharges.Count);
        Assert.DoesNotContain(openCharges, c => c.RocChargeNumber == "C3");
        // Sorted by CurrentAmount descending: C1 (610) before C2 (400).
        Assert.Equal("C1", openCharges[0].RocChargeNumber);

        var selected = AiChargesNarrativeService.SelectChargesForNarrative(openCharges);
        Assert.Equal(2, selected.Count);
        // Neither seeded charge's events carry PropertyParticulars text, so both resolve to no
        // representative event — proving the method degrades gracefully rather than throwing.
        Assert.All(selected, s => Assert.Null(s.RepresentativeEvent));
    }
}
