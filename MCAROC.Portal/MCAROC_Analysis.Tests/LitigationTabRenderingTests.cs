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
        => await RenderViewAsync("/Views/Requests/Details/_LitigationTab.cshtml", model);

    internal static Task<string> RenderCaseListAsync(PartialViewResult result)
        => RenderViewAsync("/Views/Requests/Details/_LitigationCaseList.cshtml", result.Model!, result.ViewData);

    private static async Task<string> RenderViewAsync(string viewPath, object model, ViewDataDictionary? data = null)
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
            viewResult = viewEngine.FindView(actionContext, "Details/_LitigationTab", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };
        if (data is not null)
            foreach (var entry in data) viewDictionary[entry.Key] = entry.Value;

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
            Compliance: new DossierCompliance([], [], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
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

    // ── One source: the litigation service (owner, 2026-10-26) ──

    [Fact]
    public async Task The_cases_badge_counts_the_companys_cases_before_any_filter()
    {
        var vm = CreateViewModel();
        vm.LitigationDataLake = new LitigationTabViewModel
        {
            Request = vm.Request,
            AuthoritativeSnapshot = new LitigationReportSnapshot { LitigationReportSnapshotId = 10, Status = LitigationReportSnapshotStatus.Completed },
            SourceCoverage = new LitigationSourceCoverageViewModel { UniqueCasesCount = 28 },
            TotalCaseCount = 6   // what the page's current status filter lists
        };

        var html = await RenderLitigationTabAsync(vm);

        Assert.Equal(28, vm.LitigationCaseCount);
        Assert.Contains("Cases <span class=\"badge\">28</span>", html);
        Assert.DoesNotContain("Financial Disputes", html);
        Assert.DoesNotContain("Legal history", html);
    }

    [Fact]
    public void Before_a_search_completes_there_is_no_case_count()
    {
        var vm = CreateViewModel();
        Assert.Null(vm.LitigationCaseCount);
        vm.LitigationDataLake = new LitigationTabViewModel { Request = vm.Request };
        Assert.Null(vm.LitigationCaseCount);
    }

    // ── #291 — Reused report provenance banner ──

    [Fact]
    public async Task Reused_litigation_report_renders_provenance_banner_and_header_badge()
    {
        var vm = CreateViewModel();
        var retrievedUtc = new DateTime(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);
        vm.LitigationDataLake = new LitigationTabViewModel
        {
            Request = vm.Request,
            SearchJob = new LitigationSearchJob
            {
                LitigationSearchJobId = 1,
                Status = LitigationSearchJobStatus.Completed
            },
            AuthoritativeSnapshot = new LitigationReportSnapshot
            {
                LitigationReportSnapshotId = 10,
                LitigationSearchJobId = 1,
                RequestId = 2,
                ReusedFromRequestId = 1,
                ReusedFromSnapshotId = 5,
                OriginSnapshotId = 5,
                RetrievedUtc = retrievedUtc,
                Status = LitigationReportSnapshotStatus.Completed
            },
            ReusedFromRequestNumber = "REQ-SRC-001"
        };

        var html = await RenderLitigationTabAsync(vm);

        Assert.Contains("litigation-reuse-provenance-banner", html);
        Assert.Contains("Report retrieved", html);
        Assert.Contains("reused from another request", html);
        Assert.Contains("request #REQ-SRC-001", html);
        Assert.Contains("snapshot #5", html);
        Assert.Contains("governed reuse admission", html);
        Assert.DoesNotContain("within the 7-day freshness window", html);
        Assert.Contains("Reused Report", html);
    }

    [Fact]
    public async Task Freshly_purchased_litigation_report_does_not_render_reuse_provenance_banner()
    {
        var vm = CreateViewModel();
        vm.LitigationDataLake = new LitigationTabViewModel
        {
            Request = vm.Request,
            SearchJob = new LitigationSearchJob
            {
                LitigationSearchJobId = 1,
                Status = LitigationSearchJobStatus.Completed
            },
            AuthoritativeSnapshot = new LitigationReportSnapshot
            {
                LitigationReportSnapshotId = 10,
                LitigationSearchJobId = 1,
                RequestId = 1,
                ReusedFromRequestId = null,
                ReusedFromSnapshotId = null,
                OriginSnapshotId = null,
                RetrievedUtc = DateTime.UtcNow,
                Status = LitigationReportSnapshotStatus.Completed
            }
        };

        var html = await RenderLitigationTabAsync(vm);

        Assert.DoesNotContain("litigation-reuse-provenance-banner", html);
        Assert.DoesNotContain("reused from another request", html);
    }

    // ── charged-property banner and case marker ──────────────────────────────────────────────────────────

    private static RequestDetailsViewModel LakeModel(MCAROC_Analysis.Services.LitigationData.ChargeLitigationSummary? summary, bool withCard = false)
    {
        var vm = CreateViewModel();
        var lake = new LitigationTabViewModel { Request = new McaRequest { RequestId = 7, CompanyName = "Test Co" } };
        if (withCard) lake.Cases.Add(new LitigationCaseCardViewModel { LitigationCaseId = 10, CaseNumber = "WP(C) 11227/2019" });
        vm.LitigationDataLake = lake;
        vm.ChargeLitigation = summary;
        return vm;
    }

    [Fact]
    public async Task Case_register_precedes_expandable_report_details_and_encodes_party_names()
    {
        var vm = LakeModel(null, withCard: true);
        var lake = vm.LitigationDataLake!;
        lake.TotalCaseCount = 3000;
        lake.Cases[0].Petitioners = ["<script>alert('party')</script>"];
        lake.SearchJob = new LitigationSearchJob { Status = LitigationSearchJobStatus.Completed };
        lake.AuthoritativeSnapshot = new LitigationReportSnapshot { Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow };
        var html = await RenderLitigationTabAsync(vm);
        Assert.True(html.IndexOf("litigation-case-table", StringComparison.Ordinal) < html.IndexOf("litigation-report-details", StringComparison.Ordinal));
        Assert.Contains("1–25", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert('party')</script>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "name=\"outcome\""));
        Assert.Contains("value=\"100\"", html);
        Assert.Contains("returnUrl=", html);
        Assert.DoesNotContain("Orders &amp; Judgments", html);
    }

    [Fact]
    public async Task Litigation_tab_shows_a_banner_listing_the_charges_a_case_touches_with_the_caveats()
    {
        var link = new MCAROC_Analysis.Services.LitigationData.ChargeLitigationLink(
            1, "CHG-101", "State Bank of India", MCAROC_Analysis.Services.LitigationData.ChargeLitigationSignal.ImmovableAddress,
            new MCAROC_Analysis.Services.LitigationData.LinkedLitigation("Court records", 10, null, "WP(C) 11227/2019", "High Court of Orissa", "Pending", null, null),
            "The order names the property.", 500, 2, "excerpt", "Order");
        var vm = LakeModel(new MCAROC_Analysis.Services.LitigationData.ChargeLitigationSummary([link], 1, 1, 2, true), withCard: true);

        var html = await RenderLitigationTabAsync(vm);

        Assert.Contains("charge-litigation-banner", html);
        Assert.Contains("Litigation touches charged property", html);
        Assert.Contains("CHG-101", html);
        Assert.Contains("order names the charged property", html);
        Assert.Contains("/Requests/0/Litigation/Cases/10", html);
        Assert.Contains("Only strong matches are shown", html);
        Assert.Contains("NCLT/NCLAT orders are not scanned", html);
        Assert.Contains("2 order(s) had no extractable text", html);
        // and the linked case's card carries a marker, worded for the signal (this link is the property itself)
        Assert.Contains("Touches charged property", html);
        Assert.DoesNotContain("Charge holder's recovery case", html);
    }

    [Fact]
    public async Task Litigation_tab_with_no_links_explains_what_was_compared_instead_of_implying_clean()
    {
        var vm = LakeModel(new MCAROC_Analysis.Services.LitigationData.ChargeLitigationSummary([], 3, 2, 1, true), withCard: true);

        var html = await RenderLitigationTabAsync(vm);

        Assert.DoesNotContain("charge-litigation-banner", html);
        Assert.Contains("charge-litigation-none", html);
        Assert.Contains("2 order(s) with text were compared", html);
        Assert.Contains("1 had no extractable text", html);
        Assert.Contains("NCLT/NCLAT orders are not scanned", html);
        Assert.DoesNotContain("Touches charged property", html);
    }

    [Fact]
    public async Task Litigation_tab_without_a_charge_comparison_shows_neither_banner_nor_note()
    {
        var html = await RenderLitigationTabAsync(LakeModel(null, withCard: true));

        Assert.DoesNotContain("charge-litigation-banner", html);
        Assert.DoesNotContain("charge-litigation-none", html);
    }
}
