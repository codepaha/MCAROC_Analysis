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

/// <summary>Render coverage for the A6 / #37 column-drop fix on the Corporate tab (Shareholding +
/// RelatedCorporate). Mirrors the harness in <see cref="ComplianceTabRenderingTests"/>.</summary>
public class CorporateTabRenderingTests
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

    private static async Task<string> RenderCorporateTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_CorporateTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_CorporateTab", isMainPage: false);
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

    // ── A6 / #37 — column-drop fixes ──

    [Fact]
    public async Task Shareholder_row_shows_designation_and_cessation_date()
    {
        var vm = CreateViewModel();
        vm.Shareholdings =
        [
            new Shareholding
            {
                FinancialYear = 2018,
                ShareholderNameRaw = "A FORMER DIRECTOR",
                SourceType = ShareholdingSourceType.DirectorShareholding,
                Designation = "Managing Director",
                CessationDate = new DateOnly(2018, 1, 15)
            }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Managing Director", html);
        Assert.Contains("Ceased", html);
    }

    [Fact]
    public async Task Related_corporate_row_shows_active_compliance_and_remarks()
    {
        var vm = CreateViewModel();
        vm.RelatedCorporates =
        [
            new RelatedCorporate
            {
                EntityNameRaw = "JALPOWER CORPORATION LIMITED",
                EntityNameNormalized = "JALPOWER CORPORATION LIMITED",
                RelationshipType = RelationshipType.Subsidiary,
                ActiveCompliance = "Active Compliant",
                Remarks = "Group holding company"
            }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Active Compliant", html);
        Assert.Contains("Group holding company", html);
    }

    // ── A8 / #50 — Related Party Transactions ──

    [Fact]
    public async Task Related_party_transaction_row_renders_entity_type_and_amount()
    {
        var vm = CreateViewModel();
        vm.RelatedPartyTransactions =
        [
            new RelatedPartyTransaction
            {
                FinancialYearEnding = new DateOnly(2017, 3, 31),
                EntityType = "Company",
                EntityNameRaw = "GRANDEUR POWER PROJECTS PRIVATE LIMITED",
                EntityNameNormalized = "GRANDEUR POWER PROJECTS PRIVATE LIMITED",
                RelationshipRaw = "SUBSIDIARY CORPORATES",
                TransactionType = "Revenue",
                AmountCrore = 12.5m
            }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Related Party Transactions", html);
        Assert.Contains("GRANDEUR POWER PROJECTS PRIVATE LIMITED", html);
        Assert.Contains("Revenue", html);
        Assert.Contains("12.50", html);
    }
}
