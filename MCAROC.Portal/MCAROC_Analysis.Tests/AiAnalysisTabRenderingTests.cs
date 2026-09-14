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

// #152 follow-up — the AI Analysis tab now shows each domain's computed MetricGroups (Model.KeyMetrics)
// alongside its AI findings, so a reviewer sees calculations and narrative together in one glance.
public class AiAnalysisTabRenderingTests
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
        Documents = [],
        LatestAnalysisRun = new AnalysisRun
        {
            AnalysisRunId = 1,
            RequestId = 1,
            Status = AnalysisRunStatus.Completed,
            CriticalFindingsCount = 0,
            ReviewFindingsCount = 0,
            WatchFindingsCount = 0,
            PositiveFindingsCount = 0
        }
    };

    private static AnalysisFinding Finding(FindingSection section, FindingSeverity severity, string title) => new()
    {
        FindingId = Random.Shared.NextInt64(),
        AnalysisRunId = 1,
        RequestId = 1,
        Section = section,
        Severity = severity,
        TemporalStatus = TemporalStatus.Current,
        Code = $"TEST_{title}",
        Title = title,
        SummaryText = $"{title} summary"
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
    public async Task Each_domain_subtab_renders_its_own_key_indicators_alongside_findings()
    {
        var vm = CreateViewModel();
        vm.KeyMetrics =
        [
            new MetricGroup("Directors", [MetricResult.Ok("Director count", 3m, MetricUnit.Count, "As of FY2026", "Director.Name")]),
            new MetricGroup("Financial trend & leverage", [MetricResult.Ok("Revenue CAGR", 12.5m, MetricUnit.Percent, "FY2024-FY2026", "FinancialYearData.Revenue")]),
            new MetricGroup("Charge register", [MetricResult.Ok("Open charges", 2m, MetricUnit.Count, "Current", "Charge.Status")]),
            new MetricGroup("GST compliance", [MetricResult.Ok("GSTIN cancellation count", 0m, MetricUnit.Count, "Lifetime", "GstRegistration.Status")]),
            new MetricGroup("Legal history", [MetricResult.Ok("Open litigations", 1m, MetricUnit.Count, "Current", "Litigation.Status")])
        ];
        vm.AnalysisFindings =
        [
            Finding(FindingSection.Directors, FindingSeverity.Watch, "Director change"),
            Finding(FindingSection.Financial, FindingSeverity.Review, "Revenue decline"),
        ];

        var html = await RenderAiAnalysisTabAsync(vm);

        var corporateIdx = html.IndexOf("id=\"sec-ai-corporate\"", StringComparison.Ordinal);
        var financialIdx = html.IndexOf("id=\"sec-ai-financial\"", StringComparison.Ordinal);
        var chargesIdx = html.IndexOf("id=\"sec-ai-charges\"", StringComparison.Ordinal);
        var complianceIdx = html.IndexOf("id=\"sec-ai-compliance\"", StringComparison.Ordinal);
        var litigationIdx = html.IndexOf("id=\"sec-ai-litigation\"", StringComparison.Ordinal);
        var xsecIdx = html.IndexOf("id=\"sec-ai-xsec\"", StringComparison.Ordinal);
        Assert.True(corporateIdx >= 0 && financialIdx > corporateIdx && chargesIdx > financialIdx
            && complianceIdx > chargesIdx && litigationIdx > complianceIdx && xsecIdx > litigationIdx);

        var corporateSection = html[corporateIdx..financialIdx];
        var financialSection = html[financialIdx..chargesIdx];
        var chargesSection = html[chargesIdx..complianceIdx];
        var complianceSection = html[complianceIdx..litigationIdx];
        var litigationSection = html[litigationIdx..xsecIdx];
        var xsecSection = html[xsecIdx..];

        Assert.Contains("Director count", corporateSection);
        Assert.Contains("Director change", corporateSection); // finding card still renders too

        Assert.Contains("Revenue CAGR", financialSection);
        Assert.Contains("Revenue decline", financialSection);

        Assert.Contains("Open charges", chargesSection);
        Assert.Contains("GSTIN cancellation count", complianceSection);
        Assert.Contains("Open litigations", litigationSection);

        // Cross-Section has no mapped MetricGroups — no Key Indicators block should render there.
        Assert.DoesNotContain("Key Indicators", xsecSection);

        // Metrics for one domain must not bleed into another domain's panel.
        Assert.DoesNotContain("Revenue CAGR", corporateSection);
        Assert.DoesNotContain("Director count", financialSection);
    }

    [Fact]
    public async Task Metrics_render_even_when_a_domain_has_zero_findings()
    {
        // Regression guard: the calculations block must render unconditionally, above the
        // "No findings in this section" branch — not nested inside the has-findings branch, which
        // would silently hide calculations whenever a domain has metrics but no AI findings yet.
        var vm = CreateViewModel();
        vm.KeyMetrics = [new MetricGroup("Legal history", [MetricResult.Ok("Open litigations", 4m, MetricUnit.Count, "Current", "Litigation.Status")])];
        vm.AnalysisFindings = [];

        var html = await RenderAiAnalysisTabAsync(vm);

        var litigationIdx = html.IndexOf("id=\"sec-ai-litigation\"", StringComparison.Ordinal);
        Assert.True(litigationIdx >= 0);
        var nextPanelIdx = html.IndexOf("tab-pane", litigationIdx + 1, StringComparison.Ordinal);
        var litigationSection = nextPanelIdx > litigationIdx ? html[litigationIdx..nextPanelIdx] : html[litigationIdx..];

        Assert.Contains("Open litigations", litigationSection);
        Assert.Contains("No findings in this section.", litigationSection);
    }
}
