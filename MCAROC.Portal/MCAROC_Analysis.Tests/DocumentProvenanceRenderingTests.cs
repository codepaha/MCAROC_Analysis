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

/// <summary>Render coverage for #113 (C3) — document/workbook provenance. Proves each of the 4
/// provenance branches actually renders in HTML: (a) direct entity rows show sheet/row via
/// _WorkbookProvenance.cshtml, (b) computed AnalysisFinding cards show their input set or "Computed
/// value", (c) an entity row with no recorded lineage shows "Source not recorded" rather than a blank or
/// fabricated citation, and (d) AI chat citations distinguish a real filed-PDF page from parsed structured
/// data and never claim a page number the source doesn't have.</summary>
public class DocumentProvenanceRenderingTests
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

    private static async Task<string> RenderAsync<TModel>(string viewPath, string findViewName, TModel model)
    {
        var sp = CreateServices();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        routeData.Routers.Add(new RouteCollection()); // satisfies UrlHelper.Router for asp-controller/asp-action form tag helpers
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, findViewName, isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<TModel>(
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

    private static Task<string> RenderDirectorTableAsync(List<Director> model) =>
        RenderAsync("/Views/Requests/Details/_DirectorTable.cshtml", "Details/_DirectorTable", model);

    private static Task<string> RenderChargeDrawerAsync(ChargeDrawerViewModel model) =>
        RenderAsync("/Views/Requests/Details/_ChargeDrawer.cshtml", "Details/_ChargeDrawer", model);

    private static Task<string> RenderAiAnalysisTabAsync(RequestDetailsViewModel model) =>
        RenderAsync("/Views/Requests/Details/_AiAnalysisTab.cshtml", "Details/_AiAnalysisTab", model);

    private static Task<string> RenderDocumentsTabAsync(RequestDetailsViewModel model) =>
        RenderAsync("/Views/Requests/Details/_DocumentsTab.cshtml", "Details/_DocumentsTab", model);

    private static Task<string> RenderChatPanelAsync(RequestDetailsViewModel model) =>
        RenderAsync("/Views/Requests/Details/_ChatPanel.cshtml", "Details/_ChatPanel", model);

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

    // ── (a) Direct entity rows — workbook lineage ──

    [Fact]
    public async Task Director_row_shows_its_source_sheet_and_row()
    {
        var html = await RenderDirectorTableAsync(
        [
            new Director { NameRaw = "Jane Doe", SourceSheetName = "Director Signatory Details", SourceRowNumber = 4 }
        ]);

        Assert.Contains("Source: Director Signatory Details, row 4", html);
    }

    // ── (c) Genuinely unresolvable lineage ──

    [Fact]
    public async Task Director_row_with_no_recorded_sheet_shows_source_not_recorded_not_a_blank()
    {
        var html = await RenderDirectorTableAsync(
        [
            new Director { NameRaw = "Jane Doe", SourceSheetName = null, SourceRowNumber = null }
        ]);

        Assert.Contains("Source not recorded", html);
        Assert.DoesNotContain("Source: ,", html);
    }

    [Fact]
    public async Task Charge_lifecycle_event_shows_its_source_sheet_and_row()
    {
        var charge = new RocCharge { RocChargeNumber = "CHG-1", LatestChargeHolderRaw = "State Bank of India" };
        charge.Events.Add(new RocChargeEvent
        {
            RocChargeId = 0,
            EventType = ChargeEventType.Creation,
            SourceSheetName = "Open Charges Sequence",
            SourceRowNumber = 9
        });

        var html = await RenderChargeDrawerAsync(new ChargeDrawerViewModel(charge, []));

        Assert.Contains("Source: Open Charges Sequence, row 9", html);
    }

    // ── (b) Aggregates / computed findings ──

    [Fact]
    public async Task Finding_with_real_source_reference_shows_its_input_record_count()
    {
        var vm = CreateViewModel();
        vm.LatestAnalysisRun = new AnalysisRun { Status = AnalysisRunStatus.Completed, RunNumber = 1 };
        vm.AnalysisFindings =
        [
            new AnalysisFinding
            {
                Section = FindingSection.Litigation,
                Severity = FindingSeverity.Review,
                TemporalStatus = TemporalStatus.Current,
                Code = "LIT_PENDING_AGAINST",
                Title = "Pending Litigation Against Company",
                SummaryText = "2 pending confirmed cases.",
                SourceReferenceJson = """{"entityType":"Litigation","entityIds":[1,2]}"""
            }
        ];

        var html = await RenderAiAnalysisTabAsync(vm);

        Assert.Contains("Computed from: 2 Litigation records", html);
    }

    [Fact]
    public async Task Finding_with_no_source_reference_shows_computed_value_not_a_fabricated_citation()
    {
        var vm = CreateViewModel();
        vm.LatestAnalysisRun = new AnalysisRun { Status = AnalysisRunStatus.Completed, RunNumber = 1 };
        vm.AnalysisFindings =
        [
            new AnalysisFinding
            {
                Section = FindingSection.Financial,
                Severity = FindingSeverity.Watch,
                TemporalStatus = TemporalStatus.Trend,
                Code = "FIN_REVENUE_DECLINE_2Y",
                Title = "Revenue declining",
                SummaryText = "Revenue fell for 2 consecutive years.",
                SourceReferenceJson = null
            }
        ];

        var html = await RenderAiAnalysisTabAsync(vm);

        Assert.Contains("Computed value", html);
        Assert.DoesNotContain("Source: ,", html);
    }

    // ── (d) Filed-PDF AI citations vs. parsed-structured-data citations ──

    [Fact]
    public async Task Document_chunk_citation_shows_document_name_and_page()
    {
        var vm = CreateViewModel();
        vm.ChatMessages =
        [
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                MessageText = "The company was incorporated in 1995.",
                CitedSourcesJson = """[{"SourceType":"DocumentChunk","ChunkId":5,"DocumentName":"MGT-7_2023.pdf","PageNumber":3,"EntityType":null,"EntityId":null,"Label":"MGT-7_2023.pdf · Page 3"}]"""
            }
        ];

        var html = await RenderChatPanelAsync(vm);

        Assert.Contains("Source: MGT-7_2023.pdf, page 3", html);
    }

    [Fact]
    public async Task Structured_fact_citation_never_claims_a_page_number()
    {
        var vm = CreateViewModel();
        vm.ChatMessages =
        [
            new ChatMessage
            {
                Role = ChatRole.Assistant,
                MessageText = "The company has 3 open charges.",
                CitedSourcesJson = """[{"SourceType":"StructuredFact","ChunkId":null,"DocumentName":null,"PageNumber":null,"EntityType":"RocCharge","EntityId":7,"Label":"RocCharge #7"}]"""
            }
        ];

        var html = await RenderChatPanelAsync(vm);

        Assert.Contains("Computed from parsed data (RocCharge)", html);
        Assert.DoesNotContain("page", html);
    }

    [Fact]
    public async Task No_citations_renders_no_sources_block()
    {
        var vm = CreateViewModel();
        vm.ChatMessages =
        [
            new ChatMessage { Role = ChatRole.Assistant, MessageText = "I could not verify this from the uploaded records.", CitedSourcesJson = null }
        ];

        var html = await RenderChatPanelAsync(vm);

        Assert.DoesNotContain("Sources:", html);
    }
}
