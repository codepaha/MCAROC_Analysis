using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>Captures the current RequestDetailsViewModel computed values for the seed graph to a
/// checked-in fixture. When the fixture is absent the test writes it and fails once ("commit it and
/// re-run"); thereafter it asserts nothing has drifted. DossierAssemblerParityTests holds the refactored
/// assembler to the same fixture.</summary>
public class DossierGoldenMasterTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    internal static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis.Tests", "Dossier", "dossier-golden-master.json");
    }

    internal static async Task<RequestDetailsViewModel> LoadViewModelAsync(AppDbContext db, long requestId)
    {
        var controller = new RequestsController(db, null!, null!, null!, null!, null!, null!);
        var result = await controller.Details(requestId, charge: null);
        return (RequestDetailsViewModel)Assert.IsType<ViewResult>(result).Model!;
    }

    [Fact]
    public async Task Current_view_model_matches_the_golden_master_fixture()
    {
        await using var db = CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(db);

        await using var read = CreateContext();
        var snapshot = GoldenMasterSnapshot.From(await LoadViewModelAsync(read, requestId));

        var path = FixturePath();
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, Json));
            Assert.Fail($"Golden-master fixture created at {path}. Commit it and re-run.");
        }

        var expected = JsonSerializer.Deserialize<GoldenMasterSnapshot>(await File.ReadAllTextAsync(path), Json)!;
        Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(snapshot, Json));
    }
}
