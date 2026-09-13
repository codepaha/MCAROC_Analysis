using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers #164 PR1's provenance resolver against the real DossierTestSeed graph — a field-string
/// input resolves to real ExtractedEntityBase-derived rows (never a fabricated reference), and an unknown
/// entity type resolves to nothing rather than throwing.</summary>
public class CalculationSourceRowRefResolverTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Resolves_CompanyProfile_input_to_the_real_profile_row()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        var profile = await db.CompanyProfiles.FirstAsync(p => p.IngestionRunId == ingestionRunId);

        var refs = CalculationSourceRowRefResolver.Resolve(["CompanyProfile.PaidUpCapital"], model!, profile);

        var single = Assert.Single(refs);
        Assert.Equal("CompanyProfile", single.EntityType);
        Assert.Equal(profile.CompanyProfileId, single.EntityId);
    }

    [Fact]
    public async Task Resolves_FinancialYearData_input_to_the_latest_and_prior_year()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);

        var refs = CalculationSourceRowRefResolver.Resolve(["FinancialYearData.Revenue"], model!, null);

        // Seed has standalone years 2023/2024/2025 — latest (2025) and prior (2024) are cited; the
        // resolver deliberately does not try to parse a metric's free-text Period to find every year a
        // multi-year aggregate touched (see the resolver's own doc comment).
        Assert.Equal(2, refs.Count);
        var years = model!.Financials.Standalone.OrderBy(f => f.FinancialYear).ToList();
        Assert.Contains(refs, r => r.EntityType == "FinancialYearData" && r.EntityId == years[^1].FinancialId);
        Assert.Contains(refs, r => r.EntityType == "FinancialYearData" && r.EntityId == years[^2].FinancialId);
    }

    [Fact]
    public async Task Resolves_RocCharge_input_to_every_charge_row()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);

        var refs = CalculationSourceRowRefResolver.Resolve(["RocCharge.CurrentAmount"], model!, null);

        Assert.Equal(model!.Charges.All.Count, refs.Count);
        Assert.All(refs, r => Assert.Equal("RocCharge", r.EntityType));
    }

    [Fact]
    public async Task Resolves_a_bracketed_FinancialFact_label_input_to_the_matching_row()
    {
        // The seed carries a real FinancialFact row for FY2025: "Reserves and Surplus" = -186.2.
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);

        var refs = CalculationSourceRowRefResolver.Resolve(["FinancialFact['Reserves and Surplus']"], model!, null);

        var single = Assert.Single(refs);
        Assert.Equal("FinancialFact", single.EntityType);
    }

    [Fact]
    public async Task Unknown_entity_type_resolves_to_nothing_rather_than_throwing()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);

        var refs = CalculationSourceRowRefResolver.Resolve(["SomeFutureEntity.SomeField"], model!, null);

        Assert.Empty(refs);
    }
}
