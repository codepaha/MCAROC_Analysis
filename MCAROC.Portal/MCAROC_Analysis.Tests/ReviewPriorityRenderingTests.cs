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

/// <summary>Render coverage for #118 (C8) — the Review-Priority badge now carries a one-line explanation
/// next to it (e.g. "Medium — a Critical finding"), derived fresh at view time by
/// ReviewPriorityCalculator.Explain over the run's own persisted findings, never a stored/AI-suppliable
/// value.</summary>
public class ReviewPriorityRenderingTests
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

    private static async Task<string> RenderAiAnalysisTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_AiAnalysisTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_AiAnalysisTab", isMainPage: false);
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
    public async Task Review_priority_badge_shows_its_primary_reason()
    {
        var vm = CreateViewModel();
        vm.LatestAnalysisRun = new AnalysisRun { Status = AnalysisRunStatus.Completed, RunNumber = 1, OverallReviewPriority = ReviewPriority.Medium };
        vm.AnalysisFindings =
        [
            new AnalysisFinding
            {
                Section = FindingSection.Financial, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
                Code = "FIN_X", Title = "Finding X", SummaryText = "..."
            },
            new AnalysisFinding
            {
                Section = FindingSection.Msme, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
                Code = "MSME_X", Title = "Finding Y", SummaryText = "..."
            }
        ];

        var html = await RenderAiAnalysisTabAsync(vm);

        Assert.Contains("Review Priority: Medium", html);
        Assert.Contains("multiple Review findings", html);
    }

    [Fact]
    public async Task Low_priority_badge_shows_no_reason_clause()
    {
        var vm = CreateViewModel();
        vm.LatestAnalysisRun = new AnalysisRun { Status = AnalysisRunStatus.Completed, RunNumber = 1, OverallReviewPriority = ReviewPriority.Low };
        vm.AnalysisFindings = [];

        var html = await RenderAiAnalysisTabAsync(vm);

        Assert.Contains("Review Priority: Low", html);
        Assert.DoesNotContain("no escalating condition", html);
    }

    [Fact]
    public async Task Ai_added_cross_section_finding_never_changes_the_rendered_reason()
    {
        var vm = CreateViewModel();
        vm.LatestAnalysisRun = new AnalysisRun { Status = AnalysisRunStatus.Completed, RunNumber = 1, OverallReviewPriority = ReviewPriority.Low };
        vm.AnalysisFindings =
        [
            new AnalysisFinding
            {
                Section = FindingSection.CrossSection, Severity = FindingSeverity.Critical, TemporalStatus = TemporalStatus.Current,
                Code = "AI_CROSS_abc123", Title = "AI-added", SummaryText = "..."
            }
        ];

        var html = await RenderAiAnalysisTabAsync(vm);

        Assert.Contains("Review Priority: Low", html);
        Assert.DoesNotContain("a cross-section Critical finding", html);
    }
}
