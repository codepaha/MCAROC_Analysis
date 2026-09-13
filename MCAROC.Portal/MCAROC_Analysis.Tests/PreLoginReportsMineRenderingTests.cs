using System.Diagnostics;
using MCAROC_Analysis.Data.Entities;
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

/// <summary>The pre-login pipeline's client-remembered "My Reports" list: a batch's <c>History</c> page
/// emits a JSON data island for <c>prelogin-reports-mine.js</c> to record locally, and <c>Mine.cshtml</c>
/// renders the remembered list purely from that browser's own localStorage. Nothing here is a
/// server-side enumeration of batches (#47) — these tests guard the two places that could quietly
/// reintroduce that: the island must never appear for a batch that resolves to zero jobs, and it must
/// never let an attacker-controlled value (e.g. a Cin) break out of its own &lt;script&gt; element.</summary>
public class PreLoginReportsMineRenderingTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("MCAROC.Portal not found in parent hierarchy");
    }

    private static string ReadView(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "MCAROC.Portal", "MCAROC_Analysis", relativePath));

    private static string ReadLayoutContent() => ReadView(Path.Combine("Views", "Shared", "_Layout.cshtml"));

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

    private static async Task<string> RenderHistoryAsync(IReadOnlyList<PreLoginReportJob> model, Guid batch)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "PreLoginReports";
        routeData.Values["action"] = "History";
        routeData.Routers.Add(new RouteCollection());
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/PreLoginReports/History.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success) viewResult = viewEngine.FindView(actionContext, "History", isMainPage: false);
        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<IReadOnlyList<PreLoginReportJob>>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };
        viewDictionary["Batch"] = batch;

        var tempData = new TempDataDictionary(actionContext.HttpContext, tempDataProvider);
        var viewContext = new ViewContext(actionContext, viewResult.View, viewDictionary, tempData, writer, new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static PreLoginReportJob Job(string cin, string format = "Sbi") => new()
    {
        PreLoginReportJobId = 1,
        Cin = cin,
        Format = format,
        Status = PreLoginReportJobStatus.Completed,
        CreatedUtc = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task History_with_at_least_one_job_emits_the_batch_summary_data_island()
    {
        var batch = Guid.NewGuid();
        var html = await RenderHistoryAsync([Job("U74899DL1991PTC043274")], batch);

        Assert.Contains("id=\"pi-batch-summary\"", html);
        Assert.Contains("type=\"application/json\"", html);
        Assert.Contains(batch.ToString(), html);
    }

    [Fact]
    public async Task History_with_zero_jobs_never_emits_the_batch_summary_island()
    {
        var html = await RenderHistoryAsync([], Guid.NewGuid());

        Assert.DoesNotContain("pi-batch-summary", html);
    }

    [Fact]
    public async Task History_batch_summary_island_cannot_be_broken_out_of_by_a_hostile_cin_value()
    {
        // Deliberately NOT scanning for the first "</script>" after the island's opening tag: if the
        // encoder ever regressed to emitting the hostile value's OWN literal "</script>" unescaped,
        // that would BE the first occurrence found, so a test that stops there and asserts "no
        // </script> in between" would trivially pass on the exact vulnerable output (this is the bug a
        // review caught in an earlier version of this test). Instead assert directly against the whole
        // page: the raw breakout sequence must never appear anywhere, and the value must still have
        // made it into the island, just safely escaped.
        const string hostileCin = "</script><script>alert(1)</script>";
        var html = await RenderHistoryAsync([Job(hostileCin)], Guid.NewGuid());

        Assert.Contains("id=\"pi-batch-summary\"", html);

        // The raw sequence that would terminate the surrounding <script type="application/json">
        // element early must not appear anywhere in the rendered page.
        Assert.DoesNotContain(hostileCin, html);
        Assert.DoesNotContain("</script><script>alert(1)", html);

        // Confirm the value was actually serialized into the island (not silently dropped/filtered),
        // just escaped — computed from the same JsonSerializer.Serialize the view itself calls, so this
        // assertion tracks whatever the current default encoder actually produces rather than a
        // hand-typed guess at its escape sequences.
        var serializedHostileCin = System.Text.Json.JsonSerializer.Serialize(hostileCin);
        var escapedInner = serializedHostileCin[1..^1]; // strip the surrounding JSON quotes
        Assert.Contains(escapedInner, html);
    }

    [Fact]
    public async Task History_links_to_the_mine_page()
    {
        var html = await RenderHistoryAsync([Job("U74899DL1991PTC043274")], Guid.NewGuid());

        Assert.Contains("asp-action=\"Mine\"", ReadView(Path.Combine("Views", "PreLoginReports", "History.cshtml")));
    }

    [Fact]
    public void Index_view_links_to_the_mine_page()
    {
        var content = ReadView(Path.Combine("Views", "PreLoginReports", "Index.cshtml"));
        Assert.Contains("asp-action=\"Mine\"", content);
    }

    [Fact]
    public void Mine_view_renders_table_skeleton_and_empty_state_placeholder()
    {
        var content = ReadView(Path.Combine("Views", "PreLoginReports", "Mine.cshtml"));

        Assert.Contains("id=\"pi-mine-rows\"", content);
        Assert.Contains("id=\"pi-mine-empty\"", content);
        Assert.Contains("pi-table-wrap", content);
        Assert.Contains("pi-results-table", content);
        Assert.Contains("Format", content);
    }

    [Fact]
    public void Mine_view_renders_noscript_fallback()
    {
        var content = ReadView(Path.Combine("Views", "PreLoginReports", "Mine.cshtml"));
        Assert.Contains("<noscript>", content);
    }

    [Fact]
    public void Mine_view_renders_clear_list_button_with_local_only_copy()
    {
        var content = ReadView(Path.Combine("Views", "PreLoginReports", "Mine.cshtml"));

        Assert.Contains("id=\"pi-mine-clear\"", content);
        Assert.Contains("type=\"button\"", content);
        Assert.Contains("does not delete any report", content);
    }

    [Fact]
    public void Mine_view_references_the_module_script()
    {
        var content = ReadView(Path.Combine("Views", "PreLoginReports", "Mine.cshtml"));

        Assert.Contains("src=\"~/js/prelogin-reports-mine.js\"", content);
        Assert.Contains("type=\"module\"", content);
        Assert.Contains("asp-append-version=\"true\"", content);
    }

    [Fact]
    public void Layout_renders_my_reports_sidebar_link_and_scopes_the_new_report_link_to_index()
    {
        var content = ReadLayoutContent();

        Assert.Contains("asp-controller=\"PreLoginReports\" asp-action=\"Mine\"", content);
        Assert.Contains("@Active(\"PreLoginReports\", \"Mine\")", content);
        Assert.Contains("@Active(\"PreLoginReports\", \"Index\")", content);
    }
}
