using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>The auto-fetch entry points: the form validates the identifier and creates a request + a
/// Queued job that the worker picks up; the status endpoint exposes that job; Retry re-queues a failed
/// one. The job's own pipeline (workbooks → ingestion → filings) needs the live reference tool and is
/// exercised manually, not here.</summary>
public class AutoFetchControllerTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static (AutoFetchController Controller, AutoFetchQueue Queue) NewController(AppDbContext db, bool configured)
    {
        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = configured ? "https://reference-tool.test" : "",
            SessionCookie = configured ? "PHPSESSID=abc" : ""
        });
        var client = new ReferenceToolClient(new HttpClient(new NoNetworkHandler()), options, NullLogger<ReferenceToolClient>.Instance);
        var queue = new AutoFetchQueue();
        var jobs = new AutoFetchJobService(db, client, options, new FileValidationService(new ExcelSheetReader()), null!, null!, null!,
            new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance);
        var httpContext = new DefaultHttpContext();
        var controller = new AutoFetchController(db, jobs, queue, client, options)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };
        return (controller, queue);
    }

    [Fact]
    public async Task Form_tells_the_user_when_auto_fetch_is_not_configured()
    {
        await using var db = CreateContext();
        var (controller, _) = NewController(db, configured: false);

        var view = Assert.IsType<ViewResult>(await controller.New());
        var vm = Assert.IsType<AutoFetchRequestViewModel>(view.Model);
        Assert.False(vm.IsConfigured);

        var requestsBefore = await db.Requests.CountAsync();
        var jobsBefore = await db.AutoFetchJobs.CountAsync();
        var post = Assert.IsType<ViewResult>(await controller.New(new AutoFetchRequestViewModel { ClientId = 1, Cin = "U45203OR1995PLC003982" }, CancellationToken.None));
        Assert.Contains("not configured", Assert.IsType<AutoFetchRequestViewModel>(post.Model).ErrorMessage);
        Assert.Equal(requestsBefore, await db.Requests.CountAsync());
        Assert.Equal(jobsBefore, await db.AutoFetchJobs.CountAsync());
    }

    [Theory]
    [InlineData("", EntityType.Company, "required")]
    [InlineData("NOT-A-CIN", EntityType.Company, "valid company CIN")]
    [InlineData("AAA-1234", EntityType.Company, "LLPIN")]
    [InlineData("U45203OR1995PLC003982", EntityType.LLP, "company CIN")]
    public async Task Rejects_bad_identifiers_without_creating_anything(string cin, EntityType entityType, string expectedError)
    {
        await using var db = CreateContext();
        var before = await db.Requests.CountAsync();
        var (controller, _) = NewController(db, configured: true);

        var result = Assert.IsType<ViewResult>(await controller.New(new AutoFetchRequestViewModel { ClientId = 1, Cin = cin, EntityType = entityType }, CancellationToken.None));

        Assert.Contains(expectedError, Assert.IsType<AutoFetchRequestViewModel>(result.Model).ErrorMessage);
        Assert.Equal(before, await db.Requests.CountAsync());
    }

    [Fact]
    public async Task Valid_form_creates_request_and_queued_job_and_redirects_to_details()
    {
        await using var db = CreateContext();
        var (controller, queue) = NewController(db, configured: true);

        var result = await controller.New(new AutoFetchRequestViewModel
        {
            ClientId = 1, Cin = " u45203or1995plc003982 ", Pan = "aabcc1234d", EntityType = EntityType.Company,
            IncludeFilings = true, MaxDocumentsPerSection = 50
        }, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Requests", redirect.ControllerName);
        var requestId = Assert.IsType<long>(redirect.RouteValues!["id"]);

        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);
        Assert.Equal("U45203OR1995PLC003982", request.Cin);
        Assert.Equal("AABCC1234D", request.Pan);
        Assert.Equal("U45203OR1995PLC003982", request.CompanyName); // placeholder until the tool/workbook names it
        Assert.Equal(RequestStatus.Created, request.RequestStatus);
        Assert.StartsWith("MCA-", request.RequestNumber);

        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.RequestId == requestId);
        Assert.Equal(AutoFetchJobStatus.Queued, job.Status);
        Assert.Equal("U45203OR1995PLC003982", job.Cin);
        Assert.Equal(ReferenceToolClient.ComputeBid("U45203OR1995PLC003982"), job.Bid);
        Assert.True(job.IncludeFilings);
        Assert.Equal(50, job.MaxDocumentsPerSection);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var queued in queue.ReadAllAsync(cts.Token))
        {
            Assert.Equal(job.AutoFetchJobId, queued);
            break;
        }
    }

    [Fact]
    public async Task Llp_form_stores_the_identifier_as_llpin_too()
    {
        await using var db = CreateContext();
        var (controller, _) = NewController(db, configured: true);

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.New(new AutoFetchRequestViewModel
        {
            ClientId = 1, Cin = "AAB-9876", EntityType = EntityType.LLP, CompanyName = "Some LLP", IncludeFilings = false
        }, CancellationToken.None));
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == (long)redirect.RouteValues!["id"]!);
        Assert.Equal("AAB-9876", request.Cin);
        Assert.Equal("AAB-9876", request.Llpin);
        Assert.Equal("Some LLP", request.CompanyName);
        Assert.False((await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.RequestId == request.RequestId)).IncludeFilings);
    }

    [Fact]
    public async Task Status_reports_the_job_and_retry_requeues_a_failed_one()
    {
        long requestId;
        await using (var seed = CreateContext())
        {
            var request = new McaRequest
            {
                ClientId = 1, EntityType = EntityType.Company, CompanyName = "Auto Co", Cin = "U45203OR1995PLC003982",
                RequestNumber = $"AF-{Guid.NewGuid():N}", RequestStatus = RequestStatus.ExtractionFailed, CreatedDate = DateTime.UtcNow
            };
            seed.Requests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.RequestId;
            seed.AutoFetchJobs.Add(new AutoFetchJob
            {
                RequestId = requestId, Cin = request.Cin!, Bid = "b", Status = AutoFetchJobStatus.Failed, ProgressPercent = 8,
                FailureReason = "The reference-tool session is not valid", WarningsJson = "[\"charge workbook missing\"]",
                FilesTotal = 10, FilesDownloaded = 3, CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var (controller, queue) = NewController(db, configured: true);

        var ok = Assert.IsType<OkObjectResult>(await controller.Status(requestId, CancellationToken.None));
        var dto = Assert.IsType<AutoFetchStatusDto>(ok.Value);
        Assert.Equal("Failed", dto.Status);
        Assert.True(dto.IsTerminal);
        Assert.Equal("The reference-tool session is not valid", dto.FailureReason);
        Assert.Equal(["charge workbook missing"], dto.Warnings);
        Assert.Equal(3, dto.FilesDownloaded);
        Assert.Equal("ExtractionFailed", dto.RequestStatus);
        Assert.IsType<NotFoundResult>(await controller.Status(-1, CancellationToken.None));

        var redirect = Assert.IsType<RedirectToActionResult>(await controller.Retry(requestId, CancellationToken.None));
        Assert.Equal("Details", redirect.ActionName);
        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.RequestId == requestId);
        Assert.Equal(AutoFetchJobStatus.Queued, job.Status);
        Assert.Null(job.FailureReason);
        Assert.Equal(3, job.FilesDownloaded); // checkpoints survive a retry
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var queued in queue.ReadAllAsync(cts.Token)) { Assert.Equal(job.AutoFetchJobId, queued); break; }

        Assert.IsType<NotFoundResult>(await controller.Retry(-1, CancellationToken.None));
    }

    [Fact]
    public async Task Search_returns_an_empty_list_when_the_tool_cannot_be_asked()
    {
        await using var db = CreateContext();
        var (unconfigured, _) = NewController(db, configured: false);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<ReferenceCompanyHint>>(Assert.IsType<OkObjectResult>(await unconfigured.Search("lodha", CancellationToken.None)).Value));

        var (configured, _) = NewController(db, configured: true); // NoNetworkHandler → HttpRequestException → empty, not 500
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<ReferenceCompanyHint>>(Assert.IsType<OkObjectResult>(await configured.Search("lodha", CancellationToken.None)).Value));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<ReferenceCompanyHint>>(Assert.IsType<OkObjectResult>(await configured.Search("ab", CancellationToken.None)).Value));
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in tests");
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
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
