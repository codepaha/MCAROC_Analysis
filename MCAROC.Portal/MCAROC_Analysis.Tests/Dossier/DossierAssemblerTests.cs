using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>Guards two things Codex flagged on PR #27:
/// <list type="bullet">
/// <item>the assembler must never pair an analysis run with an ingestion run it was not computed from
/// (stale findings over fresh data);</item>
/// <item>the "Full source" path must carry the verbatim Layer-0 rows, in workbook/sheet/row order,
/// with nothing clipped.</item>
/// </list></summary>
public class DossierAssemblerTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Builds_when_a_completed_analysis_matches_the_latest_ingestion()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);

        Assert.NotNull(model);
        Assert.Equal(ingestionRunId, model!.IngestionRunId);
        Assert.Equal(analysisRunId, model.AnalysisRunId);
    }

    [Fact]
    public async Task Returns_null_when_the_latest_ingestion_has_no_analysis_of_its_own()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        // A newer completed ingestion run — the seeded analysis belongs to the previous one.
        await using var mutate = DossierGoldenMasterTests.CreateContext();
        var reIngest = new IngestionRun
        {
            RequestId = requestId, RunNumber = 2, StartedDate = DateTime.UtcNow,
            CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean
        };
        mutate.IngestionRuns.Add(reIngest);
        await mutate.SaveChangesAsync();
        await mutate.Requests.Where(r => r.RequestId == requestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LatestCompletedIngestionRunId, reIngest.IngestionRunId));

        await using var db = DossierGoldenMasterTests.CreateContext();
        Assert.Null(await new DossierAssembler(db).BuildAsync(requestId));
    }

    [Fact]
    public async Task Returns_null_when_the_only_matching_analysis_is_still_running()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, analysisRunId) = await DossierTestSeed.SeedAsync(seedDb);

        await using var mutate = DossierGoldenMasterTests.CreateContext();
        await mutate.AnalysisRuns.Where(a => a.AnalysisRunId == analysisRunId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AnalysisRunStatus.Running));

        await using var db = DossierGoldenMasterTests.CreateContext();
        Assert.Null(await new DossierAssembler(db).BuildAsync(requestId));
    }

    [Fact]
    public async Task Loads_the_verbatim_source_rows_in_workbook_sheet_row_order()
    {
        await using var seedDb = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seedDb);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var sheets = model!.SourceSheets;
        Assert.Equal(
            new[] { ("RocReport", "Company Information"), ("RocReport", "Directors"), ("ChargeReport", "Charges") },
            sheets.Select(s => (s.WorkbookRole, s.SheetName)).ToArray());

        var charges = sheets.Single(s => s.SheetName == "Charges");
        Assert.Equal(new[] { 1, 2 }, charges.Rows.Select(r => r.RowNumber).ToArray());

        // The long property-particulars cell is carried whole — no Clip(...) on this path.
        var longCell = charges.Rows[1].Cells[3];
        Assert.NotNull(longCell);
        Assert.True(longCell!.Length > 120);
        Assert.EndsWith("pari passu inter se.", longCell);
        Assert.DoesNotContain("…", longCell);
    }
}
