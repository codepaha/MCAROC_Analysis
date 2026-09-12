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

public class ChargesTabRenderingTests
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

    private static async Task<string> RenderChargesTabAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_ChargesTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_ChargesTab", isMainPage: false);
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
        LatestRun = new IngestionRun()
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
    public async Task All_missing_open_charge_amounts_renders_not_reported_never_zero()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge
            {
                RocChargeNumber = "CHG-101",
                LatestChargeHolderRaw = "State Bank of India",
                CurrentAmount = null,
                SatisfactionDate = null
            },
            new RocCharge
            {
                RocChargeNumber = "CHG-102",
                LatestChargeHolderRaw = "Punjab National Bank",
                CurrentAmount = null,
                SatisfactionDate = null
            }
        ];
        vm.CompanyProfile = new CompanyProfile { McaSumOfChargesCrore = 1000m };

        Assert.True(vm.AnyOpenChargeMissingAmount);
        Assert.Null(vm.TotalOpenChargeAmount);

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("Not reported / reconciliation incomplete", html);
        Assert.Contains("(recon incomplete)", html);
        Assert.DoesNotContain("<strong>0.0</strong>", html);
    }

    [Fact]
    public async Task Mixed_known_missing_open_charges_renders_not_reported_and_recon_incomplete()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge
            {
                RocChargeNumber = "CHG-201",
                LatestChargeHolderRaw = "Canara Bank",
                CurrentAmount = 500m,
                SatisfactionDate = null
            },
            new RocCharge
            {
                RocChargeNumber = "CHG-202",
                LatestChargeHolderRaw = "Bank of Baroda",
                CurrentAmount = null,
                SatisfactionDate = null
            }
        ];
        vm.CompanyProfile = new CompanyProfile { McaSumOfChargesCrore = 500m };

        Assert.True(vm.AnyOpenChargeMissingAmount);
        Assert.Null(vm.TotalOpenChargeAmount);

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("Not reported / reconciliation incomplete", html);
        Assert.Contains("(recon incomplete)", html);
        Assert.DoesNotContain("<strong>500.0</strong>", html);
    }

    [Fact]
    public async Task Fully_reported_open_charges_renders_exact_sum()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge
            {
                RocChargeNumber = "CHG-301",
                LatestChargeHolderRaw = "HDFC Bank",
                CurrentAmount = 500m,
                SatisfactionDate = null
            },
            new RocCharge
            {
                RocChargeNumber = "CHG-302",
                LatestChargeHolderRaw = "ICICI Bank",
                CurrentAmount = 250m,
                SatisfactionDate = null
            }
        ];
        vm.CompanyProfile = new CompanyProfile { McaSumOfChargesCrore = 750m };

        Assert.False(vm.AnyOpenChargeMissingAmount);
        Assert.Equal(750m, vm.TotalOpenChargeAmount);

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("<strong>750.0</strong>", html);
        Assert.DoesNotContain("Not reported / reconciliation incomplete", html);
        Assert.DoesNotContain("(recon incomplete)", html);
    }

    [Fact]
    public void Missing_amount_on_satisfied_charge_does_not_poison_open_charge_total()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge
            {
                RocChargeNumber = "CHG-401",
                LatestChargeHolderRaw = "Axis Bank",
                CurrentAmount = 300m,
                SatisfactionDate = null
            },
            new RocCharge
            {
                RocChargeNumber = "CHG-402",
                LatestChargeHolderRaw = "Closed Loan Lender",
                CurrentAmount = null,
                SatisfactionDate = new DateOnly(2020, 1, 1)
            }
        ];

        Assert.False(vm.AnyOpenChargeMissingAmount);
        Assert.Equal(300m, vm.TotalOpenChargeAmount);
    }

    // ── #114 (C4) — colour-as-signal annotations ──

    [Fact]
    public async Task Blank_charge_holder_renders_the_unknown_holder_badge_not_a_blank_cell()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge { RocChargeNumber = "CHG-501", LatestChargeHolderRaw = "", CurrentAmount = 10m, SatisfactionDate = null }
        ];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("<span class=\"mca-badge sev-watch\">Unknown Charge Holder</span>", html);
    }

    [Fact]
    public async Task Known_charge_holder_never_shows_the_unknown_holder_badge()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge { RocChargeNumber = "CHG-502", LatestChargeHolderRaw = "State Bank of India", CurrentAmount = 10m, SatisfactionDate = null }
        ];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("State Bank of India", html);
        Assert.DoesNotContain("Unknown Charge Holder", html);
    }

    [Fact]
    public async Task Renamed_charge_holder_shows_the_old_name_now_new_name_badge_in_the_drawer()
    {
        var vm = CreateViewModel();
        var charge = new RocCharge { ChargeId = 1, RocChargeNumber = "CHG-503", LatestChargeHolderRaw = "New Bank Ltd", CurrentAmount = 10m, SatisfactionDate = null };
        charge.Events.Add(new RocChargeEvent
        {
            RocChargeId = 1,
            EventType = ChargeEventType.Creation,
            HolderNameRaw = "Old Bank Ltd"
        });
        vm.Charges = [charge];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("<span class=\"mca-badge sev-watch\">Old Bank Ltd (now New Bank Ltd)</span>", html);
    }
}
