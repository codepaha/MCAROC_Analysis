using System.Text.Json;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>The refactored <see cref="DossierAssembler"/> must reproduce the golden-master snapshot
/// exactly — same charge maths, lender concentration, litigation role attribution, FY split, finding
/// order — as the pre-refactor <c>RequestDetailsViewModel</c> (captured in <see cref="DossierGoldenMasterTests"/>).</summary>
public class DossierAssemblerParityTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Assembler_reproduces_the_golden_master()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var snapshot = GoldenMasterSnapshot.From(model!);
        var expected = JsonSerializer.Deserialize<GoldenMasterSnapshot>(
            await File.ReadAllTextAsync(DossierGoldenMasterTests.FixturePath()), DossierGoldenMasterTests.Json)!;

        Assert.Equal(
            JsonSerializer.Serialize(expected, DossierGoldenMasterTests.Json),
            JsonSerializer.Serialize(snapshot, DossierGoldenMasterTests.Json));
    }

    /// <summary>The portal and the dossier PDF must never show different computed metrics — both read
    /// the assembled <see cref="Models.Dossier.DossierModel"/> the <c>DossierCache</c> hands them.
    /// Held byte-for-byte equal (empty today; every Wave-4 D-issue is guarded from the moment it lands).</summary>
    [Fact]
    public async Task Portal_and_dossier_expose_the_same_computed_metrics()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var assemblerDb = DossierGoldenMasterTests.CreateContext();
        var dossierMetrics = (await new DossierAssembler(assemblerDb).BuildAsync(requestId))!.Metrics;

        await using var portalDb = DossierGoldenMasterTests.CreateContext();
        var vm = await DossierGoldenMasterTests.LoadViewModelAsync(portalDb, requestId);

        Assert.Equal(
            JsonSerializer.Serialize(dossierMetrics, DossierGoldenMasterTests.Json),
            JsonSerializer.Serialize(vm.KeyMetrics, DossierGoldenMasterTests.Json));
    }
}
