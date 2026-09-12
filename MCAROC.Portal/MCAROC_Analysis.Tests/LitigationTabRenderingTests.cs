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

/// <summary>Render coverage for the Litigation tab: D5/#60's Key Indicators block (zero and populated
/// litigation) and the A10/#52 "Financial Disputes" section. Mirrors the harness in
/// <see cref="ComplianceTabRenderingTests"/>.</summary>
public class LitigationTabRenderingTests
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

    private static async Task<string> RenderLitigationTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_LitigationTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_LitigationTab", isMainPage: false);
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

    private static DossierModel CreateEmptyDossier()
    {
        var reportDate = new DateTime(2024, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], []),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
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

    // ── D5 / #60 — Key Indicators block ──

    [Fact]
    public async Task Zero_litigation_renders_key_indicators_block_and_empty_state()
    {
        var vm = CreateViewModel();
        vm.Litigations = [];

        var emptyDossier = CreateEmptyDossier();
        var legalMetrics = DossierComputations.LitigationMetrics(emptyDossier);
        vm.KeyMetrics = [legalMetrics];

        var html = await RenderLitigationTabAsync(vm);

        // Empty state message is present
        Assert.Contains("No litigation was matched to this entity.", html);

        // Key Indicators block is present even with zero litigations
        Assert.Contains("Key Indicators", html);
        Assert.Contains("Legal history", html);
        Assert.Contains("Confirmed pending case count", html);
        Assert.Contains("0 confirmed cases on record", html);
        Assert.Contains("Cases filed against vs by the company", html);
        Assert.Contains("NCLT / insolvency case count", html);
        Assert.Contains("DRT case count", html);
        Assert.Contains("Pending vs disposed ratio", html);
        Assert.True(
            html.Contains("Probable + Uncertain exposure count") ||
            html.Contains("Probable &#x2B; Uncertain exposure count"),
            "D8 Probable + Uncertain exposure count metric label should be present in HTML");

        // Highlight grid should not be rendered when 0 litigations
        Assert.DoesNotContain("mca-highlight-grid", html);
    }

    [Fact]
    public async Task Populated_litigation_renders_both_highlight_grid_and_key_indicators()
    {
        var vm = CreateViewModel();
        vm.Litigations =
        [
            new Litigation
            {
                LitigationId = 1,
                MatchStatus = LitigationMatchStatus.Confirmed,
                CaseStatus = "Pending",
                CaseCategory = "Civil Cases",
                Court = "High Court of Bombay"
            }
        ];

        var dossier = CreateEmptyDossier() with
        {
            Litigation = new DossierLitigation(vm.Litigations, [], new Dictionary<long, LitigationRole> { [1] = LitigationRole.FiledAgainst })
        };
        vm.KeyMetrics = [DossierComputations.LitigationMetrics(dossier)];

        var html = await RenderLitigationTabAsync(vm);

        Assert.Contains("mca-highlight-grid", html);
        Assert.Contains("Total Litigations", html);
        Assert.Contains("Key Indicators", html);
        Assert.Contains("Legal history", html);
        Assert.Contains("Confirmed pending case count", html);
        Assert.DoesNotContain("No litigation was matched to this entity.", html);
    }

    // ── A10 / #52 — Financial Disputes section ──

    [Fact]
    public async Task Financial_dispute_row_shows_direction_amount_and_verdict()
    {
        var vm = CreateViewModel();
        vm.FinancialDisputeCases =
        [
            new FinancialDisputeCase
            {
                Direction = "Payable",
                DisputeType = "Trade Payable",
                Currency = "INR",
                AmountUnderDefault = 12.5m,
                Verdict = "ALLOWED",
                Court = "NCLT",
                Litigants = "ACME BANK vs. TEST CORP",
                CaseNumber = "CP123",
                DateOfDefault = new DateOnly(2020, 1, 1)
            }
        ];

        var html = await RenderLitigationTabAsync(vm);

        Assert.Contains("Financial Disputes", html);
        Assert.Contains("Payable", html);
        Assert.Contains("ALLOWED", html);
        Assert.Contains("ACME BANK vs. TEST CORP", html);
        Assert.Contains("12.50", html);
    }

    [Fact]
    public async Task Empty_financial_disputes_shows_empty_state()
    {
        var html = await RenderLitigationTabAsync(CreateViewModel());
        Assert.Contains("No financial dispute cases were reported", html);
    }
}
