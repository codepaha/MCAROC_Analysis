using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Tests;

/// <summary>The internal Clients page is a read-only list: its one setting (the per-client dossier Litigation switch) was retired when the
/// dossier stopped carrying litigation for everyone, so there is no edit action any more.</summary>
public class ClientsControllerTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Index_lists_clients_ordered_by_name()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await new ClientsController(db).Index();

        var view = Assert.IsType<ViewResult>(result);
        var clients = Assert.IsAssignableFrom<IReadOnlyList<MCAROC_Analysis.Data.Entities.Client>>(view.Model);
        Assert.NotEmpty(clients);
        Assert.Equal(clients.OrderBy(c => c.ClientName).Select(c => c.ClientId), clients.Select(c => c.ClientId));
    }

    [Fact]
    public void The_controller_no_longer_edits_anything()
    {
        Assert.DoesNotContain(typeof(ClientsController).GetMethods(), m => m.Name == "Edit");
    }
}
