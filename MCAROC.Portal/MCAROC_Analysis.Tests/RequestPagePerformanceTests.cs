using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit.Abstractions;

namespace MCAROC_Analysis.Tests;

// Opt-in measurement: no timing threshold in CI. The identical seed is used before/after.
public class RequestPagePerformanceTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Measure_initial_request_and_render()
    {
        var artifactDir = Environment.GetEnvironmentVariable("MCAROC_PAGE_PROBE");
        Skip.If(string.IsNullOrWhiteSpace(artifactDir), "Opt-in performance probe; set MCAROC_PAGE_PROBE to an output directory.");
        await using var seed = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString).Options);
        await TestDatabase.MigrateAsync(seed);
        var (id, _, _) = await Dossier.DossierTestSeed.SeedAsync(seed);
        var batch = new McaFilingBatch { RequestId = id, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow };
        seed.McaFilingBatches.Add(batch);
        await seed.SaveChangesAsync();
        for (var i = 0; i < 1000; i++)
        {
            var filing = new McaFiling { RequestId = id, BatchId = batch.BatchId, Srn = $"P{i}" };
            filing.Documents.Add(new McaFilingDocument { RequestId = id, BatchId = batch.BatchId,
                OriginalFileName = $"filing-{i}.pdf", FileHash = i.ToString("D64"),
                Category = FilingCategory.Unclassified, ProcessingStatus = FilingDocumentProcessingStatus.Completed });
            seed.McaFilings.Add(filing);
        }
        await seed.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        Directory.CreateDirectory(artifactDir!);
        var samples = new List<object>();
        for (var i = 0; i < 4; i++)
        {
            var counter = new QueryCounter();
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(TestDatabase.ConnectionString).AddInterceptors(counter).Options);
            #if REQUEST_PAGE_BASELINE
            var controller = new BaselineRequestPageController(db, null!, null!, null!, null!, null!,
            #else
            var controller = new RequestsController(db, null!, null!, null!, null!, null!,
            #endif
                new DossierCache(db, new DossierAssembler(db), cache), null!, new CorporateTimelineBuilder(db));
            var watch = Stopwatch.StartNew();
            var result = Assert.IsType<ViewResult>(await controller.Details(id, null));
            var model = Assert.IsType<RequestDetailsViewModel>(result.Model);
            var queryMs = watch.Elapsed.TotalMilliseconds;
            var renderer = typeof(DetailsHeaderRenderingTests).GetMethod("RenderDetailsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var html = await (Task<string>)renderer.Invoke(null, [model])!;
            var totalMs = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(1000, model.FilingCategoryCounts.Values.Sum());
            samples.Add(new { sample = i, queryMs, renderMs = totalMs - queryMs, totalMs, queries = counter.Count,
                tracked = db.ChangeTracker.Entries().Count(), htmlBytes = System.Text.Encoding.UTF8.GetByteCount(html) });
            if (i == 3) await File.WriteAllTextAsync(Path.Combine(artifactDir, "request.html"), html);
        }
        var json = JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "timings.json"), json);
        output.WriteLine(json);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            // Optional, identical before/after: a memory-constrained shared SQL Express host can
            // otherwise spend minutes waiting for memory grants for tiny sorts. Never used by the app.
            if (Environment.GetEnvironmentVariable("MCAROC_PROBE_LOW_MEMORY") == "1"
                && (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    || command.CommandText.StartsWith("-- Request page publication version", StringComparison.Ordinal)))
                command.CommandText += " OPTION (MAX_GRANT_PERCENT = 0, MAXDOP 1)";
            return ValueTask.FromResult(result);
        }
    }
}
