using System.Diagnostics;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.ObjectPool;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Render coverage for the Timeline tab (#97) — proves the generated HTML actually shows each
/// event's provenance, category, and empty state, not just that the data was constructed correctly
/// (that's <see cref="CorporateTimelineBuilderTests"/>'s job). Mirrors the harness in
/// <see cref="LitigationTabRenderingTests"/>.</summary>
public class TimelineTabRenderingTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("MCAROC.Portal not found in parent hierarchy");
    }

    private static IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var repoRoot = FindRepoRoot();
        var webAppDir = Path.Combine(repoRoot, "MCAROC.Portal", "MCAROC_Analysis");
        var webRoot = Path.Combine(webAppDir, "wwwroot");

        var env = new TestWebHostEnvironment
        {
            ApplicationName = "MCAROC_Analysis",
            ContentRootPath = webAppDir,
            WebRootPath = webRoot,
            ContentRootFileProvider = new PhysicalFileProvider(webAppDir),
            WebRootFileProvider = new PhysicalFileProvider(webRoot)
        };
        services.AddSingleton<IWebHostEnvironment>(env);
        services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        var diag = new DiagnosticListener("Microsoft.AspNetCore");
        services.AddSingleton<DiagnosticSource>(diag);
        services.AddSingleton<DiagnosticListener>(diag);
        services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
        services.AddLogging();
        services.AddControllersWithViews();

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderTimelineTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_TimelineTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_TimelineTab", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<RequestDetailsViewModel>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };

        var tempData = new TempDataDictionary(actionContext.HttpContext, tempDataProvider);
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            tempData,
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private static RequestDetailsViewModel CreateViewModel() => new()
    {
        Request = new McaRequest { CompanyName = "Test Co", RequestNumber = "REQ-1" },
        Documents = [],
        Litigations = []
    };

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task Event_renders_its_source_sheet_and_row_provenance()
    {
        var vm = CreateViewModel();
        vm.Timeline =
        [
            new CorporateTimelineEvent(
                new DateOnly(2010, 4, 1), TimelineEventCategory.Corporate, "Company incorporated", null,
                new TimelineEventProvenance(nameof(CompanyProfile), 1, "About the Company", 5, null))
        ];

        var html = await RenderTimelineTabAsync(vm);

        Assert.Contains("Company incorporated", html);
        Assert.Contains("Source: About the Company, row 5", html);
    }

    [Fact]
    public async Task Event_with_no_row_number_shows_only_the_sheet_name()
    {
        var vm = CreateViewModel();
        vm.Timeline =
        [
            new CorporateTimelineEvent(
                new DateOnly(2010, 4, 1), TimelineEventCategory.Corporate, "Company incorporated", null,
                new TimelineEventProvenance(nameof(CompanyProfile), 1, "About the Company", null, null))
        ];

        var html = await RenderTimelineTabAsync(vm);

        Assert.Contains("Source: About the Company</small>", html);
        Assert.DoesNotContain("row ", html);
    }

    [Fact]
    public async Task Empty_timeline_shows_the_empty_state_and_no_provenance()
    {
        var html = await RenderTimelineTabAsync(CreateViewModel());

        Assert.Contains("No dated corporate events were found for this request.", html);
        Assert.DoesNotContain("Source:", html);
    }

    [Fact]
    public async Task Category_filter_chips_render_one_per_present_category_with_a_count()
    {
        var vm = CreateViewModel();
        vm.Timeline =
        [
            new CorporateTimelineEvent(new DateOnly(2010, 1, 1), TimelineEventCategory.Corporate, "Company incorporated", null,
                new TimelineEventProvenance(nameof(CompanyProfile), 1, "About the Company", 1, null)),
            new CorporateTimelineEvent(new DateOnly(2016, 3, 18), TimelineEventCategory.Charges, "Charge creation (SBI)", "₹610.00 Cr",
                new TimelineEventProvenance(nameof(RocChargeEvent), 1, "Open Charges Sequence", 3, null))
        ];

        var html = await RenderTimelineTabAsync(vm);

        Assert.Contains("data-timeline-filter=\"Corporate\"", html);
        Assert.Contains("data-timeline-filter=\"Charges\"", html);
        Assert.Contains("Charge creation (SBI)", html);
        Assert.Contains("₹610.00 Cr", html);
    }
}
