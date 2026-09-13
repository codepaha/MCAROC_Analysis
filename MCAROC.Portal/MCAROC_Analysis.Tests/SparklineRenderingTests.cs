using System.Diagnostics;
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

/// <summary>#116 (C6): rendering coverage for <c>Details/_Sparkline.cshtml</c> — one per edge case
/// named in the plan's review (all-null, one point, mixed-null gaps, zero as a real value, negative
/// values with a baseline, positive-only with none, HTML-escaped hostile labels, and the paired
/// fallback table matching the SVG's own values).</summary>
public class SparklineRenderingTests
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

    private static async Task<string> RenderSparklineAsync(ChartSeries model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_Sparkline.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_Sparkline", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<ChartSeries>(
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

    private static ChartSeries Series(params decimal?[] values)
    {
        var points = values.Select((v, i) => new ChartTimePoint(ChartPeriod.ForFinancialYear(2020 + i), v)).ToList();
        return ChartSeries.Create("Revenue", MetricUnit.Crore, ["FinancialYearData.Revenue"], points);
    }

    [Fact]
    public async Task All_null_points_renders_not_enough_data_text_and_zero_svg_markup()
    {
        var html = await RenderSparklineAsync(Series(null, null));

        Assert.Contains("Not enough data", html);
        Assert.DoesNotContain("<svg", html);
    }

    [Fact]
    public async Task Exactly_one_non_null_point_renders_a_single_dot_and_no_polyline()
    {
        var html = await RenderSparklineAsync(Series(5m));

        Assert.Contains("<circle", html);
        Assert.DoesNotContain("<polyline", html);
    }

    [Fact]
    public async Task Mixed_nulls_render_a_lone_dot_and_a_separate_two_point_line_never_one_continuous_line()
    {
        var html = await RenderSparklineAsync(Series(1m, null, 2m, 3m));

        // One polyline (for the [2,3] run) plus 3 circles total (1 lone dot + 2 on the line).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<polyline"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<circle").Count);
    }

    [Fact]
    public async Task Zero_is_a_real_plotted_value_and_shows_0_in_the_fallback_table_not_a_dash()
    {
        var html = await RenderSparklineAsync(Series(1m, 0m, -1m));

        // All 3 points plotted as one continuous segment (0 is not a gap).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<polyline"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<circle").Count);
        Assert.Contains($"<td>{MetricUnitFormat.Format(0m, MetricUnit.Crore)}</td>", html); // "₹0 Cr", a real value
        Assert.DoesNotContain("<td>—</td>", html); // no null anywhere in this series — never a fabricated gap
    }

    [Fact]
    public async Task Values_crossing_sign_render_a_baseline_line_element()
    {
        var html = await RenderSparklineAsync(Series(-2m, 3m));
        Assert.Contains("<line ", html);
    }

    [Fact]
    public async Task Entirely_positive_values_render_no_baseline_line_element()
    {
        var html = await RenderSparklineAsync(Series(1m, 2m, 3m));
        Assert.DoesNotContain("<line ", html);
    }

    [Fact]
    public async Task A_hostile_period_label_renders_html_encoded_never_executed()
    {
        var points = new List<ChartTimePoint> { new(ChartPeriod.ForDate(new DateOnly(2026, 1, 1), "<script>alert(1)</script>"), 5m) };
        var series = ChartSeries.Create("Revenue", MetricUnit.Crore, ["FinancialYearData.Revenue"], points);

        var html = await RenderSparklineAsync(series);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Fallback_table_states_the_same_raw_values_and_unit_the_graphic_represents()
    {
        var html = await RenderSparklineAsync(Series(12.5m, null, 7m));

        Assert.Contains($"<td>{MetricUnitFormat.Format(12.5m, MetricUnit.Crore)}</td>", html);
        Assert.Contains("<td>—</td>", html); // the null point
        Assert.Contains($"<td>{MetricUnitFormat.Format(7m, MetricUnit.Crore)}</td>", html);
        Assert.Contains("visually-hidden", html);
    }
}
