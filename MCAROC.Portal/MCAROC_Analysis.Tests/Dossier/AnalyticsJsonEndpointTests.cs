using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Models.Dossier;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>`GET /Requests/{id}/analytics.json` returns the computed-metrics layer verbatim — the same
/// `DossierModel.Metrics` the portal panel and the PDF render — so it can be reconciled against the
/// source with no risk of a missed / added / mismatched figure.</summary>
public class AnalyticsJsonEndpointTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static RequestsController NewController(Data.AppDbContext db) =>
        new(db, null!, null!, null!, null!, null!, DossierGoldenMasterTests.CreateCache(), null!);

    [Fact]
    public async Task Unknown_request_is_404()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        var result = await NewController(db).AnalyticsJson(999_999_999);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Analyzed_request_returns_the_metric_groups_and_the_run_ids()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var json = Assert.IsType<JsonResult>(await NewController(db).AnalyticsJson(requestId));

        // Anonymous payload — assert via reflection so the shape stays enforced.
        var v = json.Value!;
        var t = v.GetType();
        Assert.Equal(requestId, t.GetProperty("requestId")!.GetValue(v));
        Assert.Equal(ingestionRunId, t.GetProperty("ingestionRunId")!.GetValue(v));
        var groups = Assert.IsAssignableFrom<IReadOnlyList<MetricGroup>>(t.GetProperty("metricGroups")!.GetValue(v));
        // With D1 landed, the Charge register metric group is present on the analyzed request.
        Assert.NotEmpty(groups);
        Assert.Contains(groups, g => g.Title == "Charge register");
    }
}
