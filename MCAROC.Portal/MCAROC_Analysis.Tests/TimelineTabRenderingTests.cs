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

/// <summary>Render coverage for the Timeline tab (#97) — proves the generated HTML actually shows each
/// event's provenance, category, and empty state, not just that the data was constructed correctly
/// (that's <see cref="CorporateTimelineBuilderTests"/>'s job). Mirrors the harness in
/// <see cref="LitigationTabRenderingTests"/>.</summary>
public class TimelineTabRenderingTests
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

    private static Task<string> RenderTimelineTabAsync(RequestDetailsViewModel model) => RenderAsync(model, "Details/_TimelineTab");

    private static async Task<string> RenderAsync(RequestDetailsViewModel model, string view)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = $"/Views/Requests/{view}.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, view, isMainPage: false);
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

    [Fact]
    public async Task A_change_of_name_links_the_certificate_page_it_is_read_from()
    {
        var vm = CreateViewModel();
        vm.Timeline =
        [
            new CorporateTimelineEvent(
                new DateOnly(2010, 8, 11), TimelineEventCategory.Corporate, "Renamed from \"A PRIVATE LIMITED\" to \"A LIMITED\"",
                "The certificate of change of name is dated 11 Aug 2010; the name history gives 1 May 1995.",
                new TimelineEventProvenance(nameof(CompanyNameHistory), 1, "Highlights", 12, null),
                "Certificate of change of name, page 1", "/Requests/4/documents/135/view#page=1")
        ];

        var html = await RenderTimelineTabAsync(vm);

        Assert.Contains("the name history gives 1 May 1995.", html);
        Assert.Contains("<a href=\"/Requests/4/documents/135/view#page=1\" target=\"_blank\" rel=\"noopener\">Certificate of change of name, page 1</a>", html);
    }

    /// <summary>#497: the Corporate tab's Name History lists each change with its date and where it is from: the certificate's page (and the
    /// history's other date, when they differ), or the name history alone.</summary>
    [Fact]
    public async Task The_name_history_table_shows_each_change_with_its_certificate_or_the_history()
    {
        var vm = CreateViewModel();
        vm.Request.RequestId = 4;
        vm.Request.CompanyName = "Coastal Projects Limited";
        vm.NameHistory =
        [
            new CompanyNameHistory { CompanyNameHistoryId = 1, DisplayOrder = 1, PreviousName = "COASTAL PROJECTS PRIVATE LIMITED", TillDate = new DateOnly(1995, 5, 1) },
            new CompanyNameHistory { CompanyNameHistoryId = 2, DisplayOrder = 2, PreviousName = "EAST COAST CARRIERS PVT LTD", TillDate = new DateOnly(2014, 6, 19) },
        ];
        vm.NameChangeCertificates = [new MCAROC_Analysis.Services.McaFilings.CertificateNameChange("COASTAL PROJECTS PRIVATE LIMITED", "COASTALPROJECTS LIMITED", new DateOnly(2010, 8, 11), 135, 1)];

        var html = await RenderAsync(vm, "Details/_CorporateTab");

        var table = html[html.IndexOf("id=\"mcaNameHistory\"", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("</table>", StringComparison.Ordinal)];
        Assert.Contains("<td>COASTAL PROJECTS PRIVATE LIMITED</td>", table);
        Assert.Contains("<td>Coastal Projects Limited</td>", table);
        Assert.Contains("<td>11 Aug 2010</td>", table);
        Assert.Contains("href=\"/Requests/4/documents/135/view#page=1\"", table);
        Assert.Contains("The name history gives 1 May 1995", table);
        Assert.Contains("<td>19 Jun 2014</td>", table);
        Assert.Contains("Name history", table[table.IndexOf("EAST COAST CARRIERS PVT LTD", StringComparison.Ordinal)..]);
    }

    /// <summary>Review of #535: a certificate whose date could not be read is linked, but the date beside it is marked as the name history's.</summary>
    [Fact]
    public async Task The_name_history_table_marks_a_date_the_certificate_does_not_confirm()
    {
        var vm = CreateViewModel();
        vm.Request.RequestId = 4;
        vm.Request.CompanyName = "Coastal Projects Limited";
        vm.NameHistory = [new CompanyNameHistory { CompanyNameHistoryId = 1, DisplayOrder = 1, PreviousName = "COASTAL PROJECTS PRIVATE LIMITED", TillDate = new DateOnly(1995, 5, 1) }];
        vm.NameChangeCertificates = [new MCAROC_Analysis.Services.McaFilings.CertificateNameChange("COASTAL PROJECTS PRIVATE LIMITED", "COASTALPROJECTS LIMITED", null, 135, 1)];

        var html = await RenderAsync(vm, "Details/_CorporateTab");

        var table = html[html.IndexOf("id=\"mcaNameHistory\"", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("</table>", StringComparison.Ordinal)];
        Assert.Contains("<td>1 May 1995</td>", table);
        Assert.Contains("href=\"/Requests/4/documents/135/view#page=1\"", table);
        Assert.Contains("Certificate date not readable; date from the name history", table);
    }

    [Fact]
    public async Task Event_renders_its_source_sheet_and_row_provenance()
    {
        var vm = CreateViewModel();
        vm.Timeline =
        [
            new CorporateTimelineEvent(
                new DateOnly(2010, 4, 1), TimelineEventCategory.Corporate, "Company incorporated", null,
                new TimelineEventProvenance(nameof(CompanyProfile), 1, "About the Company", 5, null))
        ];

        var html = await RenderTimelineTabAsync(vm);

        Assert.Contains("Company incorporated", html);
        Assert.Contains("Source: About the Company, row 5", html);
    }

    [Fact]
    public async Task Event_with_no_row_number_shows_only_the_sheet_name()
    {
        var vm = CreateViewModel();
        vm.Timeline =
        [
            new CorporateTimelineEvent(
                new DateOnly(2010, 4, 1), TimelineEventCategory.Corporate, "Company incorporated", null,
                new TimelineEventProvenance(nameof(CompanyProfile), 1, "About the Company", null, null))
        ];

        var html = await RenderTimelineTabAsync(vm);

        Assert.Contains("Source: About the Company</small>", html);
        Assert.DoesNotContain("row ", html);
    }

    [Fact]
    public async Task Empty_timeline_shows_the_empty_state_and_no_provenance()
    {
        var html = await RenderTimelineTabAsync(CreateViewModel());

        Assert.Contains("No dated corporate events were found for this request.", html);
        Assert.DoesNotContain("Source:", html);
    }

    [Fact]
    public async Task Category_filter_chips_render_one_per_present_category_with_a_count()
    {
        var vm = CreateViewModel();
        vm.Timeline =
        [
            new CorporateTimelineEvent(new DateOnly(2010, 1, 1), TimelineEventCategory.Corporate, "Company incorporated", null,
                new TimelineEventProvenance(nameof(CompanyProfile), 1, "About the Company", 1, null)),
            new CorporateTimelineEvent(new DateOnly(2016, 3, 18), TimelineEventCategory.Charges, "Charge creation (SBI)", "₹610.00 Cr",
                new TimelineEventProvenance(nameof(RocChargeEvent), 1, "Open Charges Sequence", 3, null))
        ];

        var html = await RenderTimelineTabAsync(vm);

        Assert.Contains("data-timeline-filter=\"Corporate\"", html);
        Assert.Contains("data-timeline-filter=\"Charges\"", html);
        Assert.Contains("Charge creation (SBI)", html);
        Assert.Contains("₹610.00 Cr", html);
    }
}
