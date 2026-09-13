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

    // ── #106 — render-audit: fields already captured but dropped from the UI ──

    [Fact]
    public async Task About_section_shows_last_agm_date_and_lei_status()
    {
        var vm = CreateViewModel();
        vm.CompanyProfile = new CompanyProfile
        {
            CompanyName = "Test Co",
            LastAgmDate = new DateOnly(2023, 8, 30),
            LeiStatus = "ISSUED"
        };

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Date of Last AGM", html);
        Assert.Contains("30 Aug 2023", html);
        Assert.Contains("LEI Status", html);
        Assert.Contains("ISSUED", html);
    }

    [Fact]
    public async Task Director_row_shows_present_role_since_and_flags()
    {
        var vm = CreateViewModel();
        vm.Directors =
        [
            new Director
            {
                NameRaw = "A DIRECTOR",
                Din = "00415231",
                Designation = "Managing Director",
                DesignationAppointmentDate = new DateOnly(2020, 6, 1),
                OriginalAppointmentDate = new DateOnly(2015, 1, 1),
                Flags = "Disqualified"
            }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Present Role Since", html);
        Assert.Contains("1 Jun 2020", html);
        Assert.Contains("Disqualified", html);
    }

    [Fact]
    public async Task Other_directorship_row_shows_date_of_incorporation_and_active_compliance()
    {
        var vm = CreateViewModel();
        vm.DirectorAssociations =
        [
            new DirectorAssociation
            {
                DirectorNameRaw = "A DIRECTOR",
                ConnectedCompanyRaw = "OTHER CO LIMITED",
                ConnectedCompanyNormalized = "OTHER CO LIMITED",
                DateOfIncorporation = new DateOnly(2010, 4, 12),
                ActiveCompliance = "Active Compliant"
            }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("12 Apr 2010", html);
        Assert.Contains("Active Compliant", html);
    }

    [Fact]
    public async Task Major_shareholder_row_shows_location_capital_charges_and_incorporation_date()
    {
        var vm = CreateViewModel();
        vm.Shareholdings =
        [
            new Shareholding
            {
                FinancialYear = 2017,
                ShareholderNameRaw = "A CORPORATE SHAREHOLDER",
                SourceType = ShareholdingSourceType.MajorShareholding,
                Location = "MUMBAI, MAHARASHTRA",
                PaidUpCapitalCrore = 12.5m,
                SumOfChargesCrore = 3.2m,
                DateOfIncorporation = new DateOnly(2005, 8, 20)
            }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("MUMBAI, MAHARASHTRA", html);
        Assert.Contains("12.50", html);
        Assert.Contains("3.20", html);
        Assert.Contains("20 Aug 2005", html);
    }

    [Fact]
    public async Task Related_corporate_row_shows_financial_year_ending_and_date_of_incorporation()
    {
        var vm = CreateViewModel();
        vm.RelatedCorporates =
        [
            new RelatedCorporate
            {
                FinancialYearEnding = new DateOnly(2017, 3, 31),
                EntityNameRaw = "JALPOWER CORPORATION LIMITED",
                EntityNameNormalized = "JALPOWER CORPORATION LIMITED",
                RelationshipType = RelationshipType.Subsidiary,
                DateOfIncorporation = new DateOnly(2008, 11, 3)
            }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("31 Mar 2017", html);
        Assert.Contains("3 Nov 2008", html);
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

    [Fact]
    public async Task Directors_key_indicators_render_in_management_subtab()
    {
        var vm = CreateViewModel();
        vm.KeyMetrics =
        [
            new MetricGroup("Directors",
            [
                MetricResult.Ok("Active director count", 3, MetricUnit.Count, "3 active directors", "Director.CessationDate"),
                MetricResult.Ok("Average board tenure", 1.0m, MetricUnit.Years, "mean over 3 active directors", "Director.OriginalAppointmentDate")
            ])
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Active director count", html);
        Assert.Contains("Average board tenure", html);
    }

    [Fact]
    public async Task Directors_key_indicators_render_when_zero_directors()
    {
        var vm = CreateViewModel();
        vm.Directors = [];
        vm.KeyMetrics =
        [
            new MetricGroup("Directors",
            [
                MetricResult.Ok("Active director count", 0, MetricUnit.Count, "0 directors on record", "Director.CessationDate"),
                MetricResult.Insufficient("Average board tenure", MetricUnit.Years, "0 active directors on record", "Director.CessationDate")
            ])
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Active director count", html);
        Assert.Contains("Average board tenure", html);
        Assert.Contains("0 active directors on record", html);
    }

    // ── #114 (C4) — invalid-format email flagged distinctly from "unreachable" ──

    [Fact]
    public async Task Malformed_email_shows_invalid_format_badge()
    {
        var vm = CreateViewModel();
        vm.CompanyProfile = new CompanyProfile();
        vm.CompanyEmails = [new CompanyEmail { EmailAddress = "not-an-email", IsReachable = null }];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("invalid format", html);
        Assert.DoesNotContain("unreachable", html);
    }

    [Fact]
    public async Task Well_formed_but_unreachable_email_shows_only_the_unreachable_badge()
    {
        var vm = CreateViewModel();
        vm.CompanyProfile = new CompanyProfile();
        vm.CompanyEmails = [new CompanyEmail { EmailAddress = "info@example.com", IsReachable = false }];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("unreachable", html);
        Assert.DoesNotContain("invalid format", html);
    }

    [Fact]
    public async Task Well_formed_and_reachable_email_shows_neither_badge()
    {
        var vm = CreateViewModel();
        vm.CompanyProfile = new CompanyProfile();
        vm.CompanyEmails = [new CompanyEmail { EmailAddress = "info@example.com", IsReachable = true }];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("info@example.com", html);
        Assert.DoesNotContain("invalid format", html);
        Assert.DoesNotContain("unreachable", html);
    }

    // ── #123 (C5b) — the divergence badge's title= can't hold markup, so its dynamic amounts
    //    were dropped entirely; the badge's own visible text (already _Amount-wrapped) repeats the value ──

    [Fact]
    public async Task Charge_register_divergence_tooltip_names_no_dynamic_amount_but_the_visible_badge_still_does()
    {
        var vm = CreateViewModel();
        vm.CompanyProfile = new CompanyProfile { McaSumOfChargesCrore = 100m };
        vm.Charges =
        [
            new RocCharge { RocChargeNumber = "CHG-801", CurrentAmount = 80m, SatisfactionDate = null }
        ];

        var html = await RenderCorporateTabAsync(vm);

        Assert.Contains("Divergence (Computed Open:", html);
        Assert.Contains("₹80.00 Cr", html); // the visible, _Amount-wrapped computed total
        // The tooltip itself must name no specific figure — a title attribute can't hold markup, so it
        // can't stay toggle-consistent; the fix drops the dynamic amounts rather than leaving a stale one.
        const string titlePrefix = "title=\"";
        var titleStart = html.IndexOf(titlePrefix + "MCA-stated", StringComparison.Ordinal);
        Assert.True(titleStart >= 0, "Expected the static divergence tooltip text to be present.");
        var valueStart = titleStart + titlePrefix.Length;
        var titleEnd = html.IndexOf('"', valueStart);
        var titleValue = html[valueStart..titleEnd];
        Assert.DoesNotContain("80", titleValue);
        Assert.DoesNotContain("100", titleValue);
        Assert.DoesNotContain("₹", titleValue);
    }
}
