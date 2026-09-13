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

/// <summary>#120 (C9): rendering coverage for <c>Views/Shared/_HorizontalBars.cshtml</c> — the shared
/// chart contract's first <see cref="ChartCategorySeries"/> consumer.</summary>
public class HorizontalBarsRenderingTests
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

    private static async Task<string> RenderAsync(ChartCategorySeries model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Dashboard";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Shared/_HorizontalBars.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "_HorizontalBars", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<ChartCategorySeries>(
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

    private static ChartCategorySeries Series(params (string Category, decimal Value)[] points) =>
        ChartCategorySeries.Create("Test", MetricUnit.Count, ["Entity.Field"],
            points.Select(p => new ChartCategoryPoint(p.Category, p.Value, null, null)).ToList());

    [Fact]
    public async Task A_hostile_category_renders_html_encoded_never_executed()
    {
        var series = Series(("<script>alert(1)</script>", 5m));
        var html = await RenderAsync(series);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task The_largest_value_produces_the_widest_bar()
    {
        var series = Series(("Small", 2m), ("Large", 10m));
        var html = await RenderAsync(series);

        var widths = System.Text.RegularExpressions.Regex.Matches(html, "<rect[^>]*width=\"([\\d.]+)\"")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(2, widths.Count);
        Assert.True(widths[1] > widths[0]);
    }

    [Fact]
    public async Task Fallback_table_values_match_MetricUnitFormat_exactly()
    {
        var series = Series(("High", 12m));
        var html = await RenderAsync(series);

        Assert.Contains($"<td>{MetricUnitFormat.Format(12m, MetricUnit.Count)}</td>", html);
    }

    [Fact]
    public async Task A_zero_value_category_renders_a_real_zero_width_bar_and_a_real_zero_in_the_table_never_omitted()
    {
        var series = Series(("High", 5m), ("Low", 0m));
        var html = await RenderAsync(series);

        Assert.Contains("Low", html);
        Assert.Contains($"<td>Low</td><td>{MetricUnitFormat.Format(0m, MetricUnit.Count)}</td>", html);
    }

    [Fact]
    public async Task An_all_zero_series_renders_without_throwing_or_emitting_NaN_or_Infinity()
    {
        var series = Series(("High", 0m), ("Medium", 0m), ("Low", 0m));
        var html = await RenderAsync(series);

        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
    }

    [Fact]
    public async Task A_negative_value_throws_rather_than_rendering()
    {
        var series = Series(("High", -1m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RenderAsync(series));
    }

    [Fact]
    public async Task Coordinates_render_with_an_invariant_decimal_separator_under_a_comma_decimal_culture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            // A value chosen to force a fractional bar width (non-integer scale factor).
            var series = Series(("A", 1m), ("B", 3m));
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
