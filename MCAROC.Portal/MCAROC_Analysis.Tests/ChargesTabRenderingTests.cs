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

        // Registered Open Amount now renders via the _Amount partial (#123, C5b), which delegates to
        // DetailsFormat.Money() — "₹750.00 Cr", not the old bare "750.0".
        Assert.Contains("₹750.00 Cr", html);
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
            new RocCharge { RocChargeNumber = "CHG-502", LatestChargeHolderRaw = "State Bank of India", LatestChargeHolderNormalized = "STATE BANK OF INDIA", CurrentAmount = 10m, SatisfactionDate = null }
        ];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("State Bank of India", html);
        Assert.DoesNotContain("Unknown Charge Holder", html);
    }

    [Fact]
    public async Task Differing_event_holder_shows_factual_recorded_vs_latest_wording_not_a_rename_claim()
    {
        // Deliberately not "(now X)" — an event's holder differing from the charge's latest recorded
        // holder only proves those two facts, not that the same entity was directly renamed (a charge can
        // carry several holder changes across its lifecycle events).
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

        Assert.Contains(
            "<span class=\"mca-badge sev-watch\">Recorded holder: Old Bank Ltd; latest recorded holder: New Bank Ltd</span>",
            html);
        Assert.DoesNotContain("(now New Bank Ltd)", html);
    }

    [Fact]
    public async Task Multiple_holder_changes_each_event_states_its_own_recorded_holder_not_one_rename_pair()
    {
        var vm = CreateViewModel();
        var charge = new RocCharge { ChargeId = 2, RocChargeNumber = "CHG-504", LatestChargeHolderRaw = "Third Bank Ltd", CurrentAmount = 10m, SatisfactionDate = null };
        charge.Events.Add(new RocChargeEvent { RocChargeId = 2, EventType = ChargeEventType.Creation, HolderNameRaw = "First Bank Ltd" });
        charge.Events.Add(new RocChargeEvent { RocChargeId = 2, EventType = ChargeEventType.Modification, HolderNameRaw = "Second Bank Ltd" });
        vm.Charges = [charge];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("Recorded holder: First Bank Ltd; latest recorded holder: Third Bank Ltd", html);
        Assert.Contains("Recorded holder: Second Bank Ltd; latest recorded holder: Third Bank Ltd", html);
    }

    // ── #115 (C5a) — group charges by holder ──

    [Fact]
    public async Task Two_charges_for_the_same_holder_render_as_one_group_with_a_count_of_two()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge { RocChargeNumber = "CHG-601", LatestChargeHolderRaw = "State Bank of India", LatestChargeHolderNormalized = "STATE BANK OF INDIA", CurrentAmount = 10m, SatisfactionDate = null },
            new RocCharge { RocChargeNumber = "CHG-602", LatestChargeHolderRaw = "State Bank of India", LatestChargeHolderNormalized = "STATE BANK OF INDIA", CurrentAmount = 20m, SatisfactionDate = null }
        ];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("<strong>State Bank of India</strong>", html);
        Assert.Contains("2 charge(s)", html);
        Assert.Contains("CHG-601", html);
        Assert.Contains("CHG-602", html);
    }

    [Fact]
    public async Task Two_charges_for_different_holders_render_as_two_separate_groups()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge { RocChargeNumber = "CHG-611", LatestChargeHolderRaw = "Axis Bank", LatestChargeHolderNormalized = "AXIS BANK", CurrentAmount = 10m, SatisfactionDate = null },
            new RocCharge { RocChargeNumber = "CHG-612", LatestChargeHolderRaw = "HDFC Bank", LatestChargeHolderNormalized = "HDFC BANK", CurrentAmount = 20m, SatisfactionDate = null }
        ];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("<strong>Axis Bank</strong>", html);
        Assert.Contains("<strong>HDFC Bank</strong>", html);
        // Each its own single-charge group.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "1 charge\\(s\\)").Count);
    }

    [Fact]
    public async Task Blank_holder_charges_group_together_under_unknown_charge_holder()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge { RocChargeNumber = "CHG-621", LatestChargeHolderRaw = "", LatestChargeHolderNormalized = "", CurrentAmount = 10m, SatisfactionDate = null },
            new RocCharge { RocChargeNumber = "CHG-622", LatestChargeHolderRaw = "", LatestChargeHolderNormalized = "", CurrentAmount = 20m, SatisfactionDate = null },
            new RocCharge { RocChargeNumber = "CHG-623", LatestChargeHolderRaw = "Axis Bank", LatestChargeHolderNormalized = "AXIS BANK", CurrentAmount = 30m, SatisfactionDate = null }
        ];

        var html = await RenderChargesTabAsync(vm);

        // One "Unknown Charge Holder" badge for the group header, plus one per blank-holder row's own
        // Holder cell AND one more inside that row's _ChargeDrawer "Charge Holder" field (#114's two
        // per-charge fallbacks) - 1 (header) + 2 charges x 2 renders each = 5 total.
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "mca-badge sev-watch\">Unknown Charge Holder</span>").Count);
        Assert.Contains("2 charge(s)", html); // the unknown-holder group
        Assert.Contains("1 charge(s)", html); // Axis Bank's group
    }

    [Fact]
    public async Task Group_toggle_is_a_keyboard_focusable_accessible_control()
    {
        var vm = CreateViewModel();
        vm.Charges = [new RocCharge { RocChargeNumber = "CHG-631", LatestChargeHolderRaw = "Axis Bank", LatestChargeHolderNormalized = "AXIS BANK", CurrentAmount = 10m, SatisfactionDate = null }];

        var html = await RenderChargesTabAsync(vm);

        // A real <a> (natively focusable + Enter-activatable), not a non-interactive element with only a
        // click handler, and wired for Bootstrap's collapse + assistive-tech state.
        Assert.Contains("<a class=\"mca-holder-group-toggle\" data-bs-toggle=\"collapse\" href=\"#open-holder-0\" role=\"button\" aria-expanded=\"false\" aria-controls=\"open-holder-0\">", html);
    }

    [Fact]
    public async Task Charge_drawer_still_opens_correctly_from_within_a_holder_group()
    {
        var vm = CreateViewModel();
        var charge = new RocCharge { ChargeId = 42, RocChargeNumber = "CHG-641", LatestChargeHolderRaw = "Axis Bank", LatestChargeHolderNormalized = "AXIS BANK", CurrentAmount = 10m, SatisfactionDate = null };
        vm.Charges = [charge];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("href=\"#charge-42\"", html);
        Assert.Contains("id=\"charge-42\"", html);
    }

    [Fact]
    public async Task Satisfied_charges_are_also_grouped_by_holder()
    {
        var vm = CreateViewModel();
        vm.Charges =
        [
            new RocCharge { RocChargeNumber = "CHG-651", LatestChargeHolderRaw = "Canara Bank", LatestChargeHolderNormalized = "CANARA BANK", CurrentAmount = 5m, SatisfactionDate = new DateOnly(2021, 1, 1) },
            new RocCharge { RocChargeNumber = "CHG-652", LatestChargeHolderRaw = "Canara Bank", LatestChargeHolderNormalized = "CANARA BANK", CurrentAmount = 7m, SatisfactionDate = new DateOnly(2021, 6, 1) }
        ];

        var html = await RenderChargesTabAsync(vm);

        Assert.Contains("satisfied-holder-0", html);
        Assert.Contains("<strong>Canara Bank</strong>", html);
        Assert.Contains("2 charge(s)", html);
    }

    [Fact]
    public async Task Open_charge_drawer_row_is_nested_inside_its_holder_groups_collapsible_tbody()
    {
        // The ?charge=<id> deep link (Details.cshtml) walks up from the drawer row via
        // row.closest('tbody.collapse') to find and expand its holder group before opening the drawer
        // itself - this pins the markup contract that lookup depends on.
        var vm = CreateViewModel();
        vm.Charges = [new RocCharge { ChargeId = 71, RocChargeNumber = "CHG-701", LatestChargeHolderRaw = "Axis Bank", LatestChargeHolderNormalized = "AXIS BANK", CurrentAmount = 10m, SatisfactionDate = null }];

        var html = await RenderChargesTabAsync(vm);

        var groupOpenIdx = html.IndexOf("<tbody class=\"collapse\" id=\"open-holder-0\">");
        var rowIdx = html.IndexOf("id=\"charge-71\"");
        var groupCloseIdx = html.IndexOf("</tbody>", rowIdx);

        Assert.True(groupOpenIdx >= 0 && groupOpenIdx < rowIdx && rowIdx < groupCloseIdx,
            "Expected the charge's drawer row to be nested inside its holder group's collapsible <tbody>.");
    }

    [Fact]
    public async Task Satisfied_charge_drawer_row_is_nested_inside_its_holder_groups_collapsible_tbody()
    {
        var vm = CreateViewModel();
        vm.Charges = [new RocCharge { ChargeId = 72, RocChargeNumber = "CHG-702", LatestChargeHolderRaw = "Axis Bank", LatestChargeHolderNormalized = "AXIS BANK", CurrentAmount = 10m, SatisfactionDate = new DateOnly(2022, 1, 1) }];

        var html = await RenderChargesTabAsync(vm);

        var groupOpenIdx = html.IndexOf("<tbody class=\"collapse\" id=\"satisfied-holder-0\">");
        var rowIdx = html.IndexOf("id=\"charge-72\"");
        var groupCloseIdx = html.IndexOf("</tbody>", rowIdx);

        Assert.True(groupOpenIdx >= 0 && groupOpenIdx < rowIdx && rowIdx < groupCloseIdx,
            "Expected the charge's drawer row to be nested inside its holder group's collapsible <tbody>.");
    }
}
