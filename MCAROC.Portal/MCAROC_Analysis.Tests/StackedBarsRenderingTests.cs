using System.Diagnostics;
using System.Globalization;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Viz;
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

/// <summary>#120 (C9): rendering coverage for <c>Views/Shared/_StackedBars.cshtml</c> — the shared
/// chart contract's <see cref="ChartStackedCategorySeries"/> reference partial.</summary>
public class StackedBarsRenderingTests
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

    private static async Task<string> RenderAsync(ChartStackedCategorySeries model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Dashboard";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Shared/_StackedBars.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "_StackedBars", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<ChartStackedCategorySeries>(
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

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static readonly string[] SegmentLabels = ["Critical", "Review", "Watch"];

    private static ChartStackedCategoryPoint Point(string category, decimal critical, decimal review, decimal watch) =>
        new(category,
        [
            new ChartStackedSegment("Critical", critical, ChartAccent.Danger),
            new ChartStackedSegment("Review", review, ChartAccent.Warning),
            new ChartStackedSegment("Watch", watch, ChartAccent.Muted),
        ]);

    private static ChartStackedCategorySeries Series(params ChartStackedCategoryPoint[] points) =>
        ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], SegmentLabels, points);

    [Fact]
    public async Task A_hostile_category_renders_html_encoded_never_executed()
    {
        var series = Series(Point("<script>alert(1)</script>", 1, 0, 0));
        var html = await RenderAsync(series);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task The_largest_total_produces_the_widest_bar()
    {
        var series = Series(Point("Small", 1, 0, 0), Point("Large", 3, 2, 1));
        var html = await RenderAsync(series);

        // Every point renders exactly SegmentLabels.Count (3) rects regardless of value — a 0-value
        // segment still gets its own zero-width rect, never omitted. So "Small" contributes the first 3
        // rects and "Large" the next 3; the sum of each point's own 3 widths is its bar's total width.
        var widths = System.Text.RegularExpressions.Regex.Matches(html, "<rect[^>]*width=\"([\\d.]+)\"")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(6, widths.Count);
        var smallBarWidth = widths.Take(3).Sum();
        var largeBarWidth = widths.Skip(3).Take(3).Sum();
        Assert.True(largeBarWidth > smallBarWidth);
    }

    [Fact]
    public async Task Fallback_table_states_every_segment_and_the_total_matching_MetricUnitFormat()
    {
        var series = Series(Point("Charges", 3, 2, 1));
        var html = await RenderAsync(series);

        Assert.Contains($"<td>{MetricUnitFormat.Format(3m, MetricUnit.Count)}</td>", html);
        Assert.Contains($"<td>{MetricUnitFormat.Format(2m, MetricUnit.Count)}</td>", html);
        Assert.Contains($"<td>{MetricUnitFormat.Format(1m, MetricUnit.Count)}</td>", html);
        Assert.Contains($"<td>{MetricUnitFormat.Format(6m, MetricUnit.Count)}</td>", html); // the Total column
    }

    [Fact]
    public async Task A_point_whose_every_segment_is_zero_renders_a_zero_width_bar_never_a_fabricated_gap()
    {
        var series = Series(Point("Charges", 5, 0, 0), Point("Gst", 0, 0, 0));
        var html = await RenderAsync(series);

        Assert.Contains("Gst", html);
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
    }

    [Fact]
    public async Task An_all_zero_series_renders_without_throwing_or_emitting_NaN_or_Infinity()
    {
        var series = Series(Point("Charges", 0, 0, 0));
        var html = await RenderAsync(series);

        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
    }

    [Fact]
    public async Task Legend_shows_every_segment_label_in_the_series_own_stack_order()
    {
        var series = Series(Point("Charges", 1, 1, 1));
        var html = await RenderAsync(series);

        var criticalIdx = html.IndexOf("Critical", StringComparison.Ordinal);
        var reviewIdx = html.IndexOf("Review", StringComparison.Ordinal);
        var watchIdx = html.IndexOf("Watch", StringComparison.Ordinal);

        Assert.True(criticalIdx >= 0 && criticalIdx < reviewIdx && reviewIdx < watchIdx);
    }

    [Fact]
    public async Task Coordinates_render_with_an_invariant_decimal_separator_under_a_comma_decimal_culture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var series = Series(Point("A", 1, 1, 1), Point("B", 3, 2, 1));
            var html = await RenderAsync(series);

            var widthAttrs = System.Text.RegularExpressions.Regex.Matches(html, "width=\"([^\"]+)\"").Select(m => m.Groups[1].Value);
            Assert.All(widthAttrs, w => Assert.DoesNotContain(",", w));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
