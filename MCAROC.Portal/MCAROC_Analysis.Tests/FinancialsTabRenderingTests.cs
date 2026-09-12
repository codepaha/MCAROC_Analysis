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

public class FinancialsTabRenderingTests
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

    private static async Task<string> RenderFinancialsTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_FinancialsTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_FinancialsTab", isMainPage: false);
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
        Documents = []
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
    public async Task Financials_summary_renders_h6_revenue_per_epfo_employee()
    {
        var vm = CreateViewModel();
        vm.FinancialYears =
        [
            new FinancialYearData { FinancialYear = 2026, Revenue = 100m, NetWorth = 50m, Pat = 10m }
        ];

        var metrics = new List<MetricResult>
        {
            MetricResult.Ok("PF remittance on-time rate", 95.0m, MetricUnit.Percent, "19 on-time", "EpfoContribution.PaymentDate"),
            MetricResult.Ok("Revenue per EPFO employee", 25.0m, MetricUnit.Crore, "FY2026 revenue vs May 2026 headcount", "FinancialYearData.Revenue"),
            MetricResult.Ok("Establishment count + locations + flags", 1m, MetricUnit.Count, "1 live", "EpfoEstablishment.WorkingStatus")
        };

        vm.KeyMetrics = [new MetricGroup("EPFO / labour", metrics)];

        var html = await RenderFinancialsTabAsync(vm);

        Assert.Contains("Revenue per EPFO employee", html);
        Assert.Contains("25", html);

        // H1 and H7 must NOT render on Financials tab summary
        Assert.DoesNotContain("PF remittance on-time rate", html);
        Assert.DoesNotContain("Establishment count", html);
    }

    [Fact]
    public async Task Financials_peers_subtab_renders_peer_comparison_key_indicators()
    {
        var vm = CreateViewModel();
        vm.FinancialYears =
        [
            new FinancialYearData { FinancialYear = 2026, Revenue = 100m, NetWorth = 50m, Pat = 10m }
        ];

        var metrics = new List<MetricResult>
        {
            MetricResult.Ok("Rank in source closest-peer list", 3m, MetricUnit.Count, "Rank 3 of 5", "PeerCompany.Rank"),
            MetricResult.Ok("Count of peers in sample", 30m, MetricUnit.Count, "FY2017", "PeerComparisonMetric.PeerCount"),
            MetricResult.Ok("EBITDA Margin (%) vs peer median", 31.9m, MetricUnit.Percent, "FY2017", "PeerComparisonMetric.CompanyValue")
        };

        vm.KeyMetrics = [new MetricGroup("Peer comparison", metrics)];

        var html = await RenderFinancialsTabAsync(vm);

        // Rendered in sec-financials-peers
        var peersIdx = html.IndexOf("id=\"sec-financials-peers\"");
        Assert.True(peersIdx >= 0);
        var peersSection = html.Substring(peersIdx);

        Assert.Contains("Rank in source closest-peer list", peersSection);
        Assert.Contains("Count of peers in sample", peersSection);
        Assert.Contains("EBITDA Margin (%) vs peer median", peersSection);

        // Must NOT render in sec-financials-summary
        var summaryIdx = html.IndexOf("id=\"sec-financials-summary\"");
        var summarySection = html.Substring(summaryIdx, peersIdx - summaryIdx);
        Assert.DoesNotContain("Rank in source closest-peer list", summarySection);
        Assert.DoesNotContain("EBITDA Margin (%) vs peer median", summarySection);
    }
}

