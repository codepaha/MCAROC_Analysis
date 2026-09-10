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

public class ComplianceTabRenderingTests
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

    private static async Task<string> RenderComplianceTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_ComplianceTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_ComplianceTab", isMainPage: false);
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
    public async Task All_null_cibil_amounts_never_show_zero()
    {
        var vm = CreateViewModel();
        vm.ComplianceRecords =
        [
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 3, 31)
            },
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "PUNJAB NATIONAL BANK",
                DefaulterType = "Suit Filed",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 6, 30)
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.DoesNotContain("₹0", html);
        Assert.DoesNotContain("₹0.00", html);
        Assert.Contains("Not reported", html);
        Assert.Contains("unstated amounts across institutions", html);
        Assert.Contains("Not reported / incomplete", html);
    }

    [Fact]
    public async Task Mixed_known_missing_cibil_amounts_show_partial_coverage_warning()
    {
        var vm = CreateViewModel();
        vm.ComplianceRecords =
        [
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = 42.50m,
                RecordDate = new DateOnly(2022, 3, 31)
            },
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 6, 30)
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("₹42.50 Cr*", html);
        Assert.Contains("incomplete coverage (*unstated filings)", html);
        Assert.Contains("*coverage incomplete (unstated amounts)", html);
    }

    [Fact]
    public async Task Partial_or_all_null_epfo_contributions_never_show_fabricated_aggregate()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution
            {
                EstablishmentId = "EST001",
                EstablishmentName = "Coastal Regional Office",
                WageMonth = "2023-01",
                EmployeeCount = 100,
                ContributionAmountCrore = 1.25m
            },
            new EpfoContribution
            {
                EstablishmentId = "EST001",
                EstablishmentName = "Coastal Regional Office",
                WageMonth = "2023-02",
                EmployeeCount = 105,
                ContributionAmountCrore = null
            },
            new EpfoContribution
            {
                EstablishmentId = "EST002",
                EstablishmentName = "Coastal Head Office",
                WageMonth = "2023-01",
                EmployeeCount = 50,
                ContributionAmountCrore = null
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        // Neither EST001 nor EST002 should show a fabricated sum
        Assert.DoesNotContain("₹1.25 Cr", html);
        Assert.DoesNotContain("₹0", html);
        Assert.Contains("Not reported", html);
        Assert.Contains("incomplete coverage", html);
    }

    [Fact]
    public async Task Individual_missing_epfo_contribution_values_render_as_dash()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution
            {
                EstablishmentId = "EST001",
                EstablishmentName = "Coastal Unit 1",
                WageMonth = "2023-03",
                EmployeeCount = 80,
                ContributionAmountCrore = null,
                PaymentStatus = "Paid"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        // Individual missing contribution amount renders as em-dash "—"
        Assert.True(
            html.Contains("<td class=\"text-end\">—</td>") || html.Contains("<td class=\"text-end\">&#x2014;</td>"),
            "Expected individual missing contribution amount to render as em-dash '—' in <td class=\"text-end\">");
    }

    [Fact]
    public async Task Mixed_known_missing_across_different_banks_shows_partial_coverage_warning()
    {
        var vm = CreateViewModel();
        vm.ComplianceRecords =
        [
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "STATE BANK OF INDIA",
                DefaulterType = "Wilful Defaulter",
                AmountCrore = 42.50m,
                RecordDate = new DateOnly(2022, 3, 31)
            },
            new ComplianceRecord
            {
                RecordType = ComplianceRecordType.SuitFiled,
                Bank = "PUNJAB NATIONAL BANK",
                DefaulterType = "Suit Filed",
                AmountCrore = null,
                RecordDate = new DateOnly(2022, 6, 30)
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("₹42.50 Cr*", html);
        Assert.Contains("incomplete coverage (*unstated filings)", html);
        Assert.Contains("*coverage incomplete (unstated amounts)", html);
    }

    // ── A5 / #36 — EPFO establishment metadata + TRRN ──

    [Fact]
    public async Task Epfo_establishment_metadata_renders_even_with_no_monthly_annexure()
    {
        var vm = CreateViewModel();
        vm.EpfoEstablishments =
        [
            new EpfoEstablishment
            {
                EstablishmentId = "ORBBS0006003000",
                Name = "COASTAL PROJECTS LTD.",
                City = "BHUBANESWAR",
                DateOfSetup = new DateOnly(1995, 5, 5),
                PrincipalBusinessActivities = "BUILDING AND CONSTRUCTION INDUSTRY",
                ExemptionStatus = "PF: UNEXEMPTED",
                WorkingStatus = "LIVE ESTABLISHMENT",
            }
        ];
        // No EpfoContributions — the "EPFO Establishments" summary sheet was present, the annexure was not.

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("Establishment Profiles", html);
        Assert.Contains("BHUBANESWAR", html);
        Assert.Contains("BUILDING AND CONSTRUCTION INDUSTRY", html);
        Assert.Contains("ORBBS0006003000", html);
        Assert.Contains("No monthly contribution history for this establishment in this upload.", html);
    }

    [Fact]
    public async Task Epfo_contribution_history_shows_the_trrn_column()
    {
        var vm = CreateViewModel();
        vm.EpfoContributions =
        [
            new EpfoContribution
            {
                EstablishmentId = "ORBBS0006003000",
                EstablishmentName = "COASTAL PROJECTS LTD.",
                WageMonth = "May, 2026",
                Trrn = "2606360162374",
                EmployeeCount = 5,
                ContributionAmountCrore = 0.12m,
                PaymentStatus = "Paid on Time"
            }
        ];

        var html = await RenderComplianceTabAsync(vm);

        Assert.Contains("<th>TRRN</th>", html);
        Assert.Contains("2606360162374", html);
    }
}
