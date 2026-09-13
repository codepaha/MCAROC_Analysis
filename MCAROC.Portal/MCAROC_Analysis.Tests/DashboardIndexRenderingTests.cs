using System.Diagnostics;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
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

/// <summary>#120 (C9): the Dashboard's 3 "no data" guards must be independent — an all-zero trend, an
/// all-zero priority distribution, or an all-zero findings-by-section each show their own text state
/// without affecting whichever of the other two charts still has real data.</summary>
public class DashboardIndexRenderingTests
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
        services.AddRouting();
        services.AddControllersWithViews();

        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAsync(DashboardViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Dashboard";
        routeData.Values["action"] = "Index";
        // Index.cshtml's <form asp-action="Index"> and various asp-controller/asp-route-* anchors use
        // the legacy IRouter-based UrlHelper path, which throws unless RouteData has at least one
        // router registered — an empty RouteCollection satisfies that without needing real endpoints;
        // unresolvable links just render without an href, which is fine for this test's purposes.
        routeData.Routers.Add(new RouteCollection());
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Dashboard/Index.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Index", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<DashboardViewModel>(
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

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static DashboardViewModel CreateViewModel() => new()
    {
        Filters = new DashboardFilterCriteria(),
        Clients = [],
        Window = (new DateOnly(2026, 6, 1), new DateOnly(2026, 9, 1)),
        RequestTrend = [new TrendPoint(new DateOnly(2026, 8, 1), 5), new TrendPoint(new DateOnly(2026, 8, 8), 3)],
        TrendGranularity = TrendGranularity.Weekly,
        PriorityDistribution = new Dictionary<ReviewPriority, int> { [ReviewPriority.High] = 4 },
        FindingsBySection = [new SectionSeverityCount(FindingSection.Charges, FindingSeverity.Critical, 2)],
    };

    [Fact]
    public async Task All_zero_trend_shows_its_own_no_data_text_while_the_other_two_charts_still_render()
    {
        var vm = CreateViewModel();
        vm.RequestTrend = [new TrendPoint(new DateOnly(2026, 8, 1), 0)];

        var html = await RenderAsync(vm);

        Assert.Contains("No data for the selected filters.", html);
        Assert.DoesNotContain("mca-sparkline", html);
        // The other two charts still render real SVG markup.
        Assert.Contains("mca-hbars", html);
        Assert.Contains("mca-sbars", html);
    }

    [Fact]
    public async Task All_zero_priority_distribution_shows_its_own_no_data_text_while_the_other_two_charts_still_render()
    {
        var vm = CreateViewModel();
        vm.PriorityDistribution = new Dictionary<ReviewPriority, int> { [ReviewPriority.High] = 0 };

        var html = await RenderAsync(vm);

        Assert.Contains("No data for the selected filters.", html);
        Assert.DoesNotContain("mca-hbars", html);
        Assert.Contains("mca-sparkline", html);
        Assert.Contains("mca-sbars", html);
    }

    [Fact]
    public async Task All_zero_findings_by_section_shows_its_own_no_data_text_while_the_other_two_charts_still_render()
    {
        var vm = CreateViewModel();
        vm.FindingsBySection = [];

        var html = await RenderAsync(vm);

        Assert.Contains("No data for the selected filters.", html);
        Assert.DoesNotContain("mca-sbars", html);
        Assert.Contains("mca-sparkline", html);
        Assert.Contains("mca-hbars", html);
    }

    [Fact]
    public async Task Chart_js_is_never_referenced_by_the_rendered_page()
    {
        var html = await RenderAsync(CreateViewModel());

        Assert.DoesNotContain("chart.js", html);
        Assert.DoesNotContain("chart.umd", html);
        Assert.DoesNotContain("dashboard.js", html);
        Assert.DoesNotContain("<canvas", html);
    }
}
