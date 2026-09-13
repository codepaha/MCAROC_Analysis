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

public class ContentsNavRenderingTests
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

    private static async Task<string> RenderPartialAsync(string viewPath, RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, viewPath, isMainPage: false);
        }
        if (!viewResult.Success)
        {
            throw new InvalidOperationException($"View {viewPath} not found. Searched: {string.Join(", ", viewResult.SearchedLocations)}");
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
        Request = new McaRequest { CompanyName = "Acme Corp Ltd", RequestNumber = "REQ-101", Cin = "U12345MH2020PTC123456" },
        Documents = [],
        LatestRun = new IngestionRun(),
        CompanyProfile = new CompanyProfile { CompanyStatus = "Active" }
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
    public async Task FinancialsTab_renders_contents_nav_and_all_canonical_sections_without_subtabs()
    {
        var vm = CreateViewModel();
        var html = await RenderPartialAsync("/Views/Requests/Details/_FinancialsTab.cshtml", vm);

        // Sticky contents nav header
        Assert.Contains("class=\"mca-contents-nav\"", html);
        Assert.Contains("id=\"contents-nav-financials\"", html);
        Assert.Contains("aria-label=\"Financials contents\"", html);
        Assert.Contains("CONTENTS", html);

        // Jump links for all 7 canonical sections
        Assert.Contains("href=\"#sec-financials-summary\"", html);
        Assert.Contains("href=\"#sec-financials-pl\"", html);
        Assert.Contains("href=\"#sec-financials-bs\"", html);
        Assert.Contains("href=\"#sec-financials-cf\"", html);
        Assert.Contains("href=\"#sec-financials-ratios\"", html);
        Assert.Contains("href=\"#sec-financials-indicators\"", html);
        Assert.Contains("href=\"#sec-financials-peers\"", html);

        // Stacked continuous sections
        Assert.Contains("id=\"sec-financials-summary\"", html);
        Assert.Contains("aria-labelledby=\"head-financials-summary\"", html);
        Assert.Contains("id=\"head-financials-summary\">Summary</h3>", html);

        Assert.Contains("id=\"sec-financials-pl\"", html);
        Assert.Contains("aria-labelledby=\"head-financials-pl\"", html);
        Assert.Contains("id=\"head-financials-pl\">Profit &amp; Loss</h3>", html);

        Assert.Contains("id=\"sec-financials-bs\"", html);
        Assert.Contains("aria-labelledby=\"head-financials-bs\"", html);
        Assert.Contains("id=\"head-financials-bs\">Balance Sheet</h3>", html);

        Assert.Contains("id=\"sec-financials-cf\"", html);
        Assert.Contains("aria-labelledby=\"head-financials-cf\"", html);
        Assert.Contains("id=\"head-financials-cf\">Cash Flow</h3>", html);

        Assert.Contains("id=\"sec-financials-ratios\"", html);
        Assert.Contains("aria-labelledby=\"head-financials-ratios\"", html);
        Assert.Contains("id=\"head-financials-ratios\">Financial Ratios</h3>", html);

        Assert.Contains("id=\"sec-financials-indicators\"", html);
        Assert.Contains("aria-labelledby=\"head-financials-indicators\"", html);
        Assert.Contains("id=\"head-financials-indicators\">Additional Indicators</h3>", html);

        Assert.Contains("id=\"sec-financials-peers\"", html);
        Assert.Contains("aria-labelledby=\"head-financials-peers\"", html);
        Assert.Contains("id=\"head-financials-peers\">Peer Comparison</h3>", html);

        // No legacy subtab pills or tab-pane containers
        Assert.DoesNotContain("mca-subtabs", html);
        Assert.DoesNotContain("class=\"tab-content\"", html);
        Assert.DoesNotContain("tab-pane", html);
        Assert.DoesNotContain("data-mca-sections=\"financials\"", html);
    }

    [Fact]
    public async Task ChargesTab_renders_contents_nav_and_all_canonical_sections_without_subtabs()
    {
        var vm = CreateViewModel();
        var html = await RenderPartialAsync("/Views/Requests/Details/_ChargesTab.cshtml", vm);

        // Sticky contents nav header
        Assert.Contains("class=\"mca-contents-nav\"", html);
        Assert.Contains("id=\"contents-nav-charges\"", html);
        Assert.Contains("aria-label=\"Charges contents\"", html);
        Assert.Contains("CONTENTS", html);

        // Jump links for all 3 canonical sections
        Assert.Contains("href=\"#sec-charges-summary\"", html);
        Assert.Contains("href=\"#sec-charges-open\"", html);
        Assert.Contains("href=\"#sec-charges-satisfied\"", html);

        // Stacked continuous sections
        Assert.Contains("id=\"sec-charges-summary\"", html);
        Assert.Contains("aria-labelledby=\"head-charges-summary\"", html);
        Assert.Contains("id=\"head-charges-summary\">Summary</h3>", html);

        Assert.Contains("id=\"sec-charges-open\"", html);
        Assert.Contains("aria-labelledby=\"head-charges-open\"", html);
        Assert.Contains("id=\"head-charges-open\">Open Charges</h3>", html);

        Assert.Contains("id=\"sec-charges-satisfied\"", html);
        Assert.Contains("aria-labelledby=\"head-charges-satisfied\"", html);
        Assert.Contains("id=\"head-charges-satisfied\">Satisfied Charges</h3>", html);

        // No legacy subtab pills or tab-pane containers
        Assert.DoesNotContain("mca-subtabs", html);
        Assert.DoesNotContain("class=\"tab-content\"", html);
        Assert.DoesNotContain("tab-pane", html);
        Assert.DoesNotContain("data-mca-sections=\"charges\"", html);
    }

    [Fact]
    public async Task ComplianceTab_renders_contents_nav_and_all_canonical_sections_without_subtabs()
    {
        var vm = CreateViewModel();
        var html = await RenderPartialAsync("/Views/Requests/Details/_ComplianceTab.cshtml", vm);

        // Sticky contents nav header
        Assert.Contains("class=\"mca-contents-nav\"", html);
        Assert.Contains("id=\"contents-nav-compliance\"", html);
        Assert.Contains("aria-label=\"Compliance contents\"", html);
        Assert.Contains("CONTENTS", html);

        // Jump links for all 6 canonical sections
        Assert.Contains("href=\"#sec-compliance-mca\"", html);
        Assert.Contains("href=\"#sec-compliance-gst\"", html);
        Assert.Contains("href=\"#sec-compliance-epfo\"", html);
        Assert.Contains("href=\"#sec-compliance-msme\"", html);
        Assert.Contains("href=\"#sec-compliance-auditor\"", html);
        Assert.Contains("href=\"#sec-compliance-credit-ratings\"", html);

        // Stacked continuous sections
        Assert.Contains("id=\"sec-compliance-mca\"", html);
        Assert.Contains("aria-labelledby=\"head-compliance-mca\"", html);
        Assert.Contains("id=\"head-compliance-mca\">MCA / Regulatory</h3>", html);

        Assert.Contains("id=\"sec-compliance-gst\"", html);
        Assert.Contains("aria-labelledby=\"head-compliance-gst\"", html);
        Assert.Contains("id=\"head-compliance-gst\">GST</h3>", html);

        Assert.Contains("id=\"sec-compliance-epfo\"", html);
        Assert.Contains("aria-labelledby=\"head-compliance-epfo\"", html);
        Assert.Contains("id=\"head-compliance-epfo\">EPFO</h3>", html);

        Assert.Contains("id=\"sec-compliance-msme\"", html);
        Assert.Contains("aria-labelledby=\"head-compliance-msme\"", html);
        Assert.Contains("id=\"head-compliance-msme\">MSME</h3>", html);

        Assert.Contains("id=\"sec-compliance-auditor\"", html);
        Assert.Contains("aria-labelledby=\"head-compliance-auditor\"", html);
        Assert.Contains("id=\"head-compliance-auditor\">Auditor Observations</h3>", html);

        Assert.Contains("id=\"sec-compliance-credit-ratings\"", html);
        Assert.Contains("aria-labelledby=\"head-compliance-credit-ratings\"", html);
        Assert.Contains("id=\"head-compliance-credit-ratings\">Credit Ratings</h3>", html);

        // No legacy subtab pills or tab-pane containers
        Assert.DoesNotContain("mca-subtabs", html);
        Assert.DoesNotContain("class=\"tab-content\"", html);
        Assert.DoesNotContain("tab-pane", html);
        Assert.DoesNotContain("data-mca-sections=\"compliance\"", html);
    }

    [Fact]
    public async Task LitigationTab_renders_contents_nav_and_exact_plural_financial_disputes_without_subtabs()
    {
        var vm = CreateViewModel();
        var html = await RenderPartialAsync("/Views/Requests/Details/_LitigationTab.cshtml", vm);

        // Sticky contents nav header
        Assert.Contains("class=\"mca-contents-nav\"", html);
        Assert.Contains("id=\"contents-nav-litigation\"", html);
        Assert.Contains("aria-label=\"Litigation contents\"", html);
        Assert.Contains("CONTENTS", html);

        // Jump links for all 5 canonical sections
        Assert.Contains("href=\"#sec-litigation-summary\"", html);
        Assert.Contains("href=\"#sec-litigation-confirmed\"", html);
        Assert.Contains("href=\"#sec-litigation-probable\"", html);
        Assert.Contains("href=\"#sec-litigation-unverified\"", html);
        Assert.Contains("href=\"#sec-litigation-financial-disputes\"", html);

        // Stacked continuous sections
        Assert.Contains("id=\"sec-litigation-summary\"", html);
        Assert.Contains("aria-labelledby=\"head-litigation-summary\"", html);
        Assert.Contains("id=\"head-litigation-summary\">Summary</h3>", html);

        Assert.Contains("id=\"sec-litigation-confirmed\"", html);
        Assert.Contains("aria-labelledby=\"head-litigation-confirmed\"", html);
        Assert.Contains("id=\"head-litigation-confirmed\">Confirmed Cases</h3>", html);

        Assert.Contains("id=\"sec-litigation-probable\"", html);
        Assert.Contains("aria-labelledby=\"head-litigation-probable\"", html);
        Assert.Contains("id=\"head-litigation-probable\">Probable Cases</h3>", html);

        Assert.Contains("id=\"sec-litigation-unverified\"", html);
        Assert.Contains("aria-labelledby=\"head-litigation-unverified\"", html);
        Assert.Contains("id=\"head-litigation-unverified\">Unverified Cases</h3>", html);

        // Exact plural: sec-litigation-financial-disputes
        Assert.Contains("id=\"sec-litigation-financial-disputes\"", html);
        Assert.Contains("aria-labelledby=\"head-litigation-financial-disputes\"", html);
        Assert.Contains("id=\"head-litigation-financial-disputes\">Financial Disputes</h3>", html);

        // No legacy subtab pills or tab-pane containers
        Assert.DoesNotContain("mca-subtabs", html);
        Assert.DoesNotContain("class=\"tab-content\"", html);
        Assert.DoesNotContain("tab-pane", html);
        Assert.DoesNotContain("data-mca-sections=\"litigation\"", html);
    }

    [Fact]
    public async Task CorporateTab_retains_mca_subtabs_and_tab_panes()
    {
        var vm = CreateViewModel();
        var html = await RenderPartialAsync("/Views/Requests/Details/_CorporateTab.cshtml", vm);

        // Corporate tab MUST retain its existing subtabs and tab-pane structure
        Assert.Contains("class=\"nav mca-subtabs\"", html);
        Assert.Contains("data-mca-sections=\"corporate\"", html);
        Assert.Contains("class=\"tab-content\"", html);
        Assert.Contains("class=\"tab-pane", html);
        Assert.DoesNotContain("class=\"mca-contents-nav\"", html);
    }
}
