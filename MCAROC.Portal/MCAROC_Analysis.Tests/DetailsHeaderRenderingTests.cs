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

/// <summary>Render coverage for the company-header CIN/PAN fields on Details.cshtml. Real-file E2E
/// testing (PRUKSA INDIA HOUSING PRIVATE LIMITED) found the header showing a blank PAN even though the
/// real value was fully ingested — the header read the optional New-Request-form field
/// (<c>Request.Pan</c>) instead of the authoritative value parsed from the ROC workbook
/// (<c>CompanyProfile.Pan</c>). Mirrors the harness in <see cref="CorporateTabRenderingTests"/>.</summary>
public class DetailsHeaderRenderingTests
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

    private static async Task<string> RenderDetailsAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        routeData.Values["action"] = "Details";
        // _DocumentsTab.cshtml's Url.Action(...) calls use the legacy IRouter-based UrlHelper path,
        // which throws unless RouteData has at least one router registered — an empty RouteCollection
        // satisfies that without needing real endpoints (mirrors DashboardIndexRenderingTests).
        routeData.Routers.Add(new RouteCollection());
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details", isMainPage: false);
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
    public async Task Header_PrefersIngestedCompanyProfilePan_OverBlankFormPan()
    {
        var vm = CreateViewModel();
        vm.Request.Pan = null; // left blank on the New Request form — common, PAN is optional there
        vm.CompanyProfile = new CompanyProfile { Pan = "AAGCP0406N", Cin = "U70102KA2009PTC048926" };

        var html = await RenderDetailsAsync(vm);

        Assert.Contains("AAGCP0406N", html);
        Assert.DoesNotContain("<b>PAN:</b> —", html);
    }

    [Fact]
    public async Task Header_PrefersIngestedCompanyProfileCin_OverBlankFormCin()
    {
        var vm = CreateViewModel();
        vm.Request.Cin = null;
        vm.CompanyProfile = new CompanyProfile { Cin = "U70102KA2009PTC048926", Pan = "AAGCP0406N" };

        var html = await RenderDetailsAsync(vm);

        Assert.Contains("U70102KA2009PTC048926", html);
        Assert.DoesNotContain("<b>CIN:</b> —", html);
    }

    [Fact]
    public async Task Header_FallsBackToFormPan_WhenNoCompanyProfileYet()
    {
        var vm = CreateViewModel();
        vm.Request.Pan = "FORMPAN1234";
        vm.CompanyProfile = null; // pre-ingestion state

        var html = await RenderDetailsAsync(vm);

        Assert.Contains("FORMPAN1234", html);
    }
}
