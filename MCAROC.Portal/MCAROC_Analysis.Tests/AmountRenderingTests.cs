using System.Diagnostics;
using System.Globalization;
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

/// <summary>Direct rendering coverage for <c>Details/_Amount.cshtml</c> (#123, C5b) — the single place
/// every one of the 43 call-site amounts is rendered, so its own correctness (null/zero/negative,
/// culture handling, and delegating to <see cref="DetailsFormat.Money"/> rather than a second,
/// parallel format string) is what the whole toggle rests on.</summary>
public class AmountRenderingTests
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

    private static async Task<string> RenderAmountAsync(AmountViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_Amount.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_Amount", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<AmountViewModel>(
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

    [Fact]
    public async Task Null_value_renders_the_dash_with_no_data_attribute()
    {
        var html = await RenderAmountAsync(new AmountViewModel(null));

        Assert.Equal("<span class=\"mca-amount\">—</span>", html);
        Assert.DoesNotContain("data-amount-crore", html);
    }

    [Fact]
    public async Task Zero_renders_as_a_real_zero_with_the_data_attribute_present()
    {
        // decimal.ToString(InvariantCulture) preserves the value's own scale (not a fixed 2dp), so a
        // 0.00m literal — matching a real decimal(18,2)-ish money column — round-trips as "0.00".
        var html = await RenderAmountAsync(new AmountViewModel(0.00m));

        Assert.Contains("data-amount-crore=\"0.00\"", html);
        Assert.Contains("₹0.00 Cr", html);
        Assert.DoesNotContain(">—<", html);
    }

    [Fact]
    public async Task Negative_value_keeps_its_sign_in_both_the_attribute_and_the_visible_text()
    {
        var html = await RenderAmountAsync(new AmountViewModel(-12.50m));

        Assert.Contains("data-amount-crore=\"-12.50\"", html);
        Assert.Contains("₹-12.50 Cr", html);
    }

    [Fact]
    public async Task Visible_text_always_equals_DetailsFormatMoney_for_the_same_value()
    {
        decimal?[] values = [null, 0m, 12.5m, -3.75m, 98765432.10m];

        foreach (var v in values)
        {
            var html = await RenderAmountAsync(new AmountViewModel(v));
            var expected = DetailsFormat.Money(v);
            // The <span> has no other text node, so its inner text is exactly Money()'s own output —
            // proving the partial delegates rather than reimplementing "₹{v:N2} Cr" a second time.
            Assert.EndsWith($">{expected}</span>", html);
        }
    }

    [Fact]
    public async Task Data_attribute_is_always_invariant_culture_even_when_the_visible_text_is_not()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            // de-DE uses ',' as the decimal separator — if the attribute ever stopped being explicitly
            // invariant, it would break here first.
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var html = await RenderAmountAsync(new AmountViewModel(1234.56m));

            Assert.Contains("data-amount-crore=\"1234.56\"", html); // plain invariant '.', no grouping
            Assert.DoesNotContain("data-amount-crore=\"1234,56\"", html);
            // The visible text is whatever Money() renders under this culture — decoupled on purpose.
            var expected = DetailsFormat.Money(1234.56m);
            Assert.EndsWith($">{expected}</span>", html);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
