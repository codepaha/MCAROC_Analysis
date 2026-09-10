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
}
