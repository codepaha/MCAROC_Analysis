using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>#514 follow-up (2026-10-16): a job's attempt count is what caps automatic Fetch retries, and it counted every claim, so a job that parked for a
/// refresh or an unlock approval, or was picked up again after a restart, reached RETRIES_EXHAUSTED after two real failures. Only a run that ends Failed counts.</summary>
public sealed class AutoFetchAttemptCountTests : IAsyncLifetime
{
    private readonly FakeTime _time = new(DateTimeOffset.UtcNow);
    private readonly string _cin = $"U{Random.Shared.Next(10000, 99999)}DL2019PTC{Random.Shared.Next(100000, 999999)}";
    private string Bid => ReferenceToolClient.ComputeBid(_cin);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        FakeReferenceTool.ExportWhileRefreshingDefault = false; // a stale company waits for its refresh: the park this test is about
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        FakeReferenceTool.ExportWhileRefreshingDefault = true;
        await using var db = CreateContext();
        await db.AutoFetchJobs.Where(j => j.Cin == _cin).ExecuteDeleteAsync();
        await db.Requests.Where(r => r.Cin == _cin).ExecuteDeleteAsync();
        await db.CompanyReportLifecycles.Where(l => l.Identifier == _cin).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Parking_for_a_refresh_does_not_count_as_an_attempt_however_often_it_happens()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = _time.GetUtcNow().AddMonths(-2), DataAsOf = _time.GetUtcNow().AddDays(-3) };
        var jobId = await SeedJobAsync();

        await ProcessAsync(tool, jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, (await JobAsync(jobId)).Status);
        await SetStatusAsync(jobId, AutoFetchJobStatus.Queued);   // re-queued (the refresh worker's poll, or a restart's recovery sweep)
        await ProcessAsync(tool, jobId);

        var job = await JobAsync(jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, job.Status);
        Assert.Equal(0, job.AttemptCount);
    }

    [Fact]
    public async Task Waiting_for_an_unlock_approval_does_not_count_as_an_attempt()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = null, PreviewCin = _cin };
        var jobId = await SeedJobAsync();

        await ProcessAsync(tool, jobId, withUnlock: true);

        var job = await JobAsync(jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(0, tool.Count("addAsset"));
    }

    [Fact]
    public async Task A_run_that_fails_counts_one_attempt()
    {
        // Data current, so the export is tried; this fake's export answers 404, which fails the run.
        var tool = new FakeReferenceTool(Bid) { AddedAt = _time.GetUtcNow().AddMonths(-2), DataAsOf = _time.GetUtcNow().AddHours(-1) };
        var jobId = await SeedJobAsync();

        await ProcessAsync(tool, jobId);
        var failed = await JobAsync(jobId);
        Assert.Equal(AutoFetchJobStatus.Failed, failed.Status);
        Assert.Equal(1, failed.AttemptCount);

        await SetStatusAsync(jobId, AutoFetchJobStatus.Queued);   // retried
        await ProcessAsync(tool, jobId);
        Assert.Equal(2, (await JobAsync(jobId)).AttemptCount);
    }

    private async Task ProcessAsync(FakeReferenceTool tool, long jobId, bool withUnlock = false)
    {
        var sp = tool.BuildServices(_time, new AutoFetchQueue(), extra: services => services.AddScoped(s => new AutoFetchJobService(
            s.GetRequiredService<AppDbContext>(), tool.NewClient(), FakeReferenceTool.Options(), new FileValidationService(new ExcelSheetReader()),
            null!, new AnalysisQueue(), new FilingProcessingQueue(), null!, new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance,
            refresh: s.GetRequiredService<CompanyRefreshService>(), unlock: withUnlock ? s.GetRequiredService<CompanyUnlockService>() : null)));
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AutoFetchJobService>().ProcessAsync(jobId, CancellationToken.None);
    }

    private async Task<long> SeedJobAsync()
    {
        await using var db = CreateContext();
        var request = new McaRequest
        {
            Client = new Client { ClientCode = "ATC" + Guid.NewGuid().ToString("N")[..7], ClientName = "Attempt Count Co", CreatedDate = DateTime.UtcNow },
            EntityType = EntityType.Company, CompanyName = "Attempt Count Company", Cin = _cin,
            RequestNumber = $"ATC-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new AutoFetchJob
        {
            RequestId = request.RequestId, Cin = _cin, Bid = Bid, Status = AutoFetchJobStatus.Queued,
            IncludeFilings = false, WarningsJson = "[]", CreatedUtc = DateTime.UtcNow
        };
        db.AutoFetchJobs.Add(job);
        await db.SaveChangesAsync();
        return job.AutoFetchJobId;
    }

    private static async Task<AutoFetchJob> JobAsync(long jobId)
    {
        await using var db = CreateContext();
        return await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
    }

    private static async Task SetStatusAsync(long jobId, AutoFetchJobStatus status)
    {
        await using var db = CreateContext();
        await db.AutoFetchJobs.Where(j => j.AutoFetchJobId == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status));
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
