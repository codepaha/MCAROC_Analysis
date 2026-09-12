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

public class ChatPanelRenderingTests
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

    private static async Task<string> RenderChatPanelAsync(RequestDetailsViewModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_ChatPanel.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_ChatPanel", isMainPage: false);
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

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static RequestDetailsViewModel CreateBaseModel()
    {
        var request = new McaRequest
        {
            RequestId = 42,
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Acme Global Ltd",
            RequestNumber = "REQ-4242",
            RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow
        };

        return new RequestDetailsViewModel
        {
            Request = request,
            Documents = [],
            ChatMessages = []
        };
    }

    [Fact]
    public async Task Render_IndexingNotStarted_RendersInfoWarning()
    {
        var model = CreateBaseModel();
        model.FilingBatch = new McaFilingBatch { BatchId = 1, RequestId = 42, SourceDocumentId = 10, StartedDate = DateTime.UtcNow };
        model.ChunkableDocumentCount = 0;
        model.ChunkedDocumentCount = 0;

        var html = await RenderChatPanelAsync(model);

        Assert.Contains("mcaChatIndexingWarning", html);
        Assert.Contains("Document indexing has not started yet — questions can still be answered from structured data.", html);
    }

    [Fact]
    public async Task Render_PartialIndexing_RendersIndexedCountWarning()
    {
        var model = CreateBaseModel();
        model.FilingBatch = new McaFilingBatch { BatchId = 1, RequestId = 42, SourceDocumentId = 10, StartedDate = DateTime.UtcNow };
        model.ChunkableDocumentCount = 12;
        model.ChunkedDocumentCount = 4;

        var html = await RenderChatPanelAsync(model);

        Assert.Contains("mcaChatIndexingWarning", html);
        Assert.Contains("Document search is still indexing (indexed 4 of 12 documents) — answers may not yet cover all uploaded filings.", html);
    }

    [Fact]
    public async Task Render_IndexingComplete_DoesNotRenderIndexingWarning()
    {
        var model = CreateBaseModel();
        model.FilingBatch = new McaFilingBatch { BatchId = 1, RequestId = 42, SourceDocumentId = 10, StartedDate = DateTime.UtcNow };
        model.ChunkableDocumentCount = 10;
        model.ChunkedDocumentCount = 10;

        var html = await RenderChatPanelAsync(model);

        Assert.DoesNotContain("mcaChatIndexingWarning", html);
    }

    [Fact]
    public async Task Render_NoFilingBatch_DoesNotRenderIndexingWarning()
    {
        var model = CreateBaseModel();
        model.FilingBatch = null;

        var html = await RenderChatPanelAsync(model);

        Assert.DoesNotContain("mcaChatIndexingWarning", html);
    }
}
