using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.Dashboard;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Pipeline;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // legacy .xls encodings

// QuestPDF Community licence — valid for the client "Due Diligence Dossier" PDF: Cubictree's annual
// gross revenue is well under the US$1M Community threshold (confirmed with the owner, 2026-09).
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

const long MaxUploadBytes = 2_000_000_000; // matches ArchiveSafetyLimits.MaxArchiveSizeBytes — the real sample corpus is ~700MB

var analystOperationalCommand = args.FirstOrDefault() is { } command
    && (string.Equals(command, AnalystProvisioningCommand.Argument, StringComparison.Ordinal)
        || string.Equals(command, AnalystAssignmentCommand.Argument, StringComparison.Ordinal));
var builderArgs = analystOperationalCommand ? args.Skip(1).ToArray() : args;
var builder = WebApplication.CreateBuilder(builderArgs);

// The default multipart/Kestrel body-size limits (128MB / effectively Kestrel's own default) are far below
// the size of a real MCA Filings archive (~700MB) — both need raising for the New Search upload to work.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
});

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<AuditLogFilter>();
});
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<AuditLogFilter>();
builder.Services.AddSingleton<IAnalystPasswordHasher, AnalystPasswordHasher>();
builder.Services.AddScoped<AnalystProvisioningService>();
builder.Services.AddScoped<AnalystAssignmentOperationService>();
builder.Services.AddScoped<IAnalystRequestAccessService, AnalystRequestAccessService>();
builder.Services.AddScoped<AnalystDashboardQueryService>();
builder.Services.AddScoped<IAuthorizationHandler, AnalystRequestAuthorizationHandler>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<InstaFinancialsClient>(client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.Configure<InstaFinancialsOptions>(builder.Configuration.GetSection(InstaFinancialsOptions.SectionName));
builder.Services.Configure<LargeArchiveUploadOptions>(builder.Configuration.GetSection(LargeArchiveUploadOptions.SectionName));
builder.Services.AddScoped<IStorageReservationManager, StorageReservationManager>();
builder.Services.AddScoped<IOperationalSlotLeaseService, OperationalSlotLeaseService>();
builder.Services.AddScoped<ChunkStreamingService>();
builder.Services.AddScoped<FinalizationRecoveryService>();
builder.Services.AddScoped<PreLoginReportService>();
builder.Services.AddSingleton<PreLoginReportQueue>();
builder.Services.AddScoped<PreLoginReportJobService>();
builder.Services.AddHostedService<PreLoginReportWorker>();

// Auto-fetch (reference tool) — a request created from just a CIN/LLPIN: workbooks + every filing PDF
// are pulled from the reference tool and pushed through the same ingestion / analysis / filings pipelines
// the manual New-request flow uses. Inert until ReferenceTool:BaseUrl + SessionCookie are configured.
builder.Services.Configure<ReferenceToolOptions>(builder.Configuration.GetSection(ReferenceToolOptions.SectionName));
// Singleton: the session cookie/signing key/user id must be shared across every job/request scope, not
// re-logged-in per scope — see ReferenceToolSession's own doc comment for why this bit us before.
builder.Services.AddSingleton<ReferenceToolSession>();
builder.Services.AddHttpClient<ReferenceToolClient>(client => client.Timeout = TimeSpan.FromMinutes(10))
    // The session cookie is sent as an explicit header per request; the handler's own cookie container
    // must stay off or it silently drops that header.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });
builder.Services.AddSingleton<AutoFetchQueue>();
builder.Services.AddScoped<AutoFetchJobService>();
builder.Services.AddHostedService<AutoFetchWorker>();
// Circuit breaker (docs/pipeline-automation-plan.md §5.4) — scoped because it writes through the
// request/job-scoped AppDbContext; the probe (a singleton hosted service) creates its own scope per tick.
builder.Services.AddScoped<IIntegrationHealthService, IntegrationHealthService>();
builder.Services.AddHostedService<ReferenceToolHealthProbe>();

// Litigation data lake (#239, LIT-01) — authenticate/register/poll against the BPR Litigation Data API and
// retain the raw report for #242 to persist. Inert until BprLitigation:BaseUrl/Id/SecretKey are configured
// (user-secrets/environment only — see BprLitigationOptions).
builder.Services.Configure<BprLitigationOptions>(builder.Configuration.GetSection(BprLitigationOptions.SectionName));
builder.Services.AddHttpClient<BprLitigationClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<BprLitigationOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
        client.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
    client.Timeout = TimeSpan.FromMinutes(2);
})
// Redirects disabled: DownloadOrderDocumentAsync's host-allowlist/private-address checks only ever see the
// URL a vendor report asserts — an auto-followed 3xx would silently re-target the request to a host those
// checks never validated, defeating them. ConnectCallback closes a second, independent gap: SocketsHttpHandler
// re-resolves the hostname itself when it actually opens the connection, so a DNS record that changes between
// BprLitigationClient's own resolve-and-validate and this second resolution (DNS rebinding) could otherwise
// still land on a private address the check believed it had already ruled out — see
// BprLitigationClient.CreateSafeConnectCallback's own remarks. See BprLitigationClientTests for the
// regressions both of these protect (mirror any change here in that test's own handler construction).
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    ConnectCallback = BprLitigationClient.CreateSafeConnectCallback()
});
builder.Services.AddSingleton<LitigationSearchQueue>();
builder.Services.AddScoped<LitigationSearchJobService>();
builder.Services.AddHostedService<LitigationSearchWorker>();
builder.Services.AddSingleton<LitigationCasePersistenceQueue>();
builder.Services.AddScoped<LitigationCasePersistenceService>();
builder.Services.AddHostedService<LitigationCasePersistenceWorker>();
// #243 LIT-03 — all-orders retrieval, text retention, ZIP delivery. Reuses the already-registered
// PdfTextExtractor/IStorageReservationManager (McaFilings pipeline) rather than standing up parallel infra.
builder.Services.AddSingleton<LitigationOrderDocumentQueue>();
builder.Services.AddScoped<LitigationOrderDocumentService>();
builder.Services.AddHostedService<LitigationOrderDocumentWorker>();
// #244 LIT-04 — chunk/embed each order document's extracted text for the MCA ROC Copilot. Reuses the
// already-registered EmbeddingService (Services.Chat, Phase 4) unmodified.
builder.Services.AddSingleton<LitigationOrderChunkingQueue>();
builder.Services.AddScoped<LitigationOrderChunkingOrchestrator>();
builder.Services.AddHostedService<LitigationOrderChunkingWorker>();
// #245 LIT-05 â€” durable evidence-grounded Gemini case analysis. This is a dedicated litigation worker,
// not a CRA worker; a missing Vertex configuration fails only a requested analysis run, never startup.
builder.Services.Configure<LitigationAiAnalysisOptions>(builder.Configuration.GetSection(LitigationAiAnalysisOptions.SectionName));
builder.Services.AddSingleton<LitigationAiAnalysisQueue>();
builder.Services.AddSingleton<ILitigationAiAnalysisClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["GoogleCloud:ProjectId"] ?? throw new InvalidOperationException("GoogleCloud:ProjectId is required for litigation AI analysis.");
    var location = config["GoogleCloud:Location"] ?? "us-central1";
    var credentialsPath = config["GoogleCloud:CredentialsPath"] ?? throw new InvalidOperationException("GoogleCloud:CredentialsPath is required for litigation AI analysis.");
    return new VertexLitigationAiAnalysisClient(projectId, location, credentialsPath, sp.GetRequiredService<ILogger<VertexLitigationAiAnalysisClient>>());
});
builder.Services.AddScoped<LitigationAiAnalysisOrchestrator>();
builder.Services.AddHostedService<LitigationAiAnalysisWorker>();
builder.Services.AddScoped<LitigationReportAssembler>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<IExcelSheetReader, ExcelSheetReader>();
builder.Services.AddScoped<IWorkbookDerivativeService, WorkbookDerivativeService>();
builder.Services.AddScoped<FileValidationService>();
builder.Services.AddScoped<IngestionOrchestrator>();
builder.Services.Configure<MCAROC_Analysis.Services.Registry.RegistrySnapshotStoreOptions>(
    builder.Configuration.GetSection(MCAROC_Analysis.Services.Registry.RegistrySnapshotStoreOptions.SectionName));
builder.Services.AddSingleton<MCAROC_Analysis.Services.Registry.IRegistrySnapshotStore, MCAROC_Analysis.Services.Registry.FileRegistrySnapshotStore>();
builder.Services.AddSingleton<MCAROC_Analysis.Services.Registry.IRegistryPromotionCoordinator, MCAROC_Analysis.Services.Registry.RegistryPromotionCoordinator>();
builder.Services.AddScoped<MCAROC_Analysis.Services.Registry.CompanyRegistryQueryService>();

var keysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys");
Directory.CreateDirectory(keysPath);
var dpBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("MCAROC_Analysis");

if (OperatingSystem.IsWindows())
{
    dpBuilder.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddSingleton<MCAROC_Analysis.Services.Documents.ISignedDownloadTokenService, MCAROC_Analysis.Services.Documents.TimeLimitedSignedDownloadTokenService>();

// MCA Filings (PDF) pipeline
builder.Services.AddSingleton<FilingProcessingQueue>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var tesseractPath = config["McaFilings:TesseractExePath"] ?? @"C:\Program Files\Tesseract-OCR\tesseract.exe";
    var minChars = config.GetValue("McaFilings:MinCharsPerPageForNativeText", 80);
    return new PdfTextExtractor(sp.GetRequiredService<ILogger<PdfTextExtractor>>(), tesseractPath, minChars);
});
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["GoogleCloud:ProjectId"] ?? throw new InvalidOperationException("GoogleCloud:ProjectId is not configured.");
    var location = config["GoogleCloud:Location"] ?? "us-central1";
    var credentialsPath = config["GoogleCloud:CredentialsPath"] ?? throw new InvalidOperationException("GoogleCloud:CredentialsPath is not configured.");
    return new VertexAiExtractionService(projectId, location, credentialsPath, sp.GetRequiredService<ILogger<VertexAiExtractionService>>());
});
builder.Services.AddScoped(sp => new FilingBatchProcessor(
    sp.GetRequiredService<AppDbContext>(),
    sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
    sp.GetRequiredService<PdfTextExtractor>(),
    sp.GetRequiredService<VertexAiExtractionService>(),
    sp.GetRequiredService<FilingProcessingQueue>(),
    sp.GetRequiredService<DocumentChunkingQueue>(),
    sp.GetRequiredService<ILogger<FilingBatchProcessor>>(),
    sp.GetRequiredService<IOperationalSlotLeaseService>(),
    sp.GetRequiredService<IOptions<LargeArchiveUploadOptions>>()));
builder.Services.AddHostedService<FilingProcessingWorker>();

// Rule engine + AI cross-section analysis pipeline
builder.Services.AddSingleton<AnalysisQueue>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["GoogleCloud:ProjectId"] ?? throw new InvalidOperationException("GoogleCloud:ProjectId is not configured.");
    var location = config["GoogleCloud:Location"] ?? "us-central1";
    var credentialsPath = config["GoogleCloud:CredentialsPath"] ?? throw new InvalidOperationException("GoogleCloud:CredentialsPath is not configured.");
    return new AiCrossSectionAnalysisService(projectId, location, credentialsPath, sp.GetRequiredService<ILogger<AiCrossSectionAnalysisService>>());
});
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["GoogleCloud:ProjectId"] ?? throw new InvalidOperationException("GoogleCloud:ProjectId is not configured.");
    var location = config["GoogleCloud:Location"] ?? "us-central1";
    var credentialsPath = config["GoogleCloud:CredentialsPath"] ?? throw new InvalidOperationException("GoogleCloud:CredentialsPath is not configured.");
    return new AiChargesNarrativeService(projectId, location, credentialsPath, sp.GetRequiredService<ILogger<AiChargesNarrativeService>>());
});
builder.Services.AddScoped<AnalysisOrchestrator>();
builder.Services.AddHostedService<AnalysisWorker>();

// #164 Calculation assurance — ledger persistence + deterministic checks + AI second-line review worker +
// dossier delivery gate (reviewer UI lands in a later PR). A no-op at runtime while
// CalculationAssurance:Mode is Off (the default) or, for the AI worker specifically, while AiAuditEnabled
// is false.
builder.Services.AddScoped<MCAROC_Analysis.Services.CalculationAssurance.CalculationLedgerService>();
builder.Services.AddScoped<MCAROC_Analysis.Services.CalculationAssurance.CalculationCheckRunnerService>();
builder.Services.AddSingleton<MCAROC_Analysis.Services.CalculationAssurance.CalculationAiAuditQueue>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["GoogleCloud:ProjectId"] ?? throw new InvalidOperationException("GoogleCloud:ProjectId is not configured.");
    var location = config["GoogleCloud:Location"] ?? "us-central1";
    var credentialsPath = config["GoogleCloud:CredentialsPath"] ?? throw new InvalidOperationException("GoogleCloud:CredentialsPath is not configured.");
    return new MCAROC_Analysis.Services.CalculationAssurance.CalculationAiAuditService(
        projectId, location, credentialsPath, sp.GetRequiredService<ILogger<MCAROC_Analysis.Services.CalculationAssurance.CalculationAiAuditService>>());
});
builder.Services.AddScoped<MCAROC_Analysis.Services.CalculationAssurance.CalculationAiAuditOrchestrator>();
builder.Services.AddHostedService<MCAROC_Analysis.Services.CalculationAssurance.CalculationAiAuditWorker>();
builder.Services.AddScoped<MCAROC_Analysis.Services.CalculationAssurance.CalculationArtifactGateService>();
builder.Services.AddScoped<MCAROC_Analysis.Services.CalculationAssurance.CalculationDiscrepancyWorkflowService>();

// Phase 5: operations & risk intelligence dashboard + Search History
builder.Services.AddScoped<DashboardQueryService>();
builder.Services.AddScoped<RequestListQueryService>();

// Phase 4: document chunking/embedding + "Ask Documents" chat
builder.Services.AddSingleton<DocumentChunkingQueue>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["GoogleCloud:ProjectId"] ?? throw new InvalidOperationException("GoogleCloud:ProjectId is not configured.");
    var location = config["GoogleCloud:Location"] ?? "us-central1";
    var credentialsPath = config["GoogleCloud:CredentialsPath"] ?? throw new InvalidOperationException("GoogleCloud:CredentialsPath is not configured.");
    return new EmbeddingService(projectId, location, credentialsPath, sp.GetRequiredService<ILogger<EmbeddingService>>());
});
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectId = config["GoogleCloud:ProjectId"] ?? throw new InvalidOperationException("GoogleCloud:ProjectId is not configured.");
    var location = config["GoogleCloud:Location"] ?? "us-central1";
    var credentialsPath = config["GoogleCloud:CredentialsPath"] ?? throw new InvalidOperationException("GoogleCloud:CredentialsPath is not configured.");
    return new ChatCompletionService(projectId, location, credentialsPath, sp.GetRequiredService<ILogger<ChatCompletionService>>());
});
builder.Services.AddScoped<DocumentChunkingOrchestrator>();
builder.Services.AddHostedService<DocumentChunkingWorker>();
builder.Services.AddScoped<StructuredFactsProvider>();
builder.Services.AddScoped(sp => new DocumentRetriever(sp.GetRequiredService<AppDbContext>(), ChatRetrievalOptions.Default));
builder.Services.AddScoped(sp => new LitigationDocumentRetriever(sp.GetRequiredService<AppDbContext>(), ChatRetrievalOptions.Default));
builder.Services.AddScoped<RetrievalContextBuilder>();
builder.Services.AddScoped<ChatService>();

// Phase 7 — client "Due Diligence Dossier" (assembled model shared by the PDF and the portal).
builder.Services.AddMemoryCache(o => o.SizeLimit = 256);
builder.Services.AddScoped<DossierAssembler>();
builder.Services.AddScoped<DossierCache>();
builder.Services.AddScoped<CorporateTimelineBuilder>();
builder.Services.AddSingleton<DossierPdfRenderer>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

// Issue #235: Company Master automated delta sync, proxy rotation, and observability
builder.Services.AddSingleton<MCAROC_Analysis.Services.CompanyMaster.IProxyPoolService, MCAROC_Analysis.Services.CompanyMaster.ProxyPoolService>();
builder.Services.AddSingleton<MCAROC_Analysis.Services.CompanyMaster.ITwoTierCaptchaSolverService, MCAROC_Analysis.Services.CompanyMaster.TwoTierCaptchaSolverService>();
builder.Services.AddSingleton<MCAROC_Analysis.Services.CompanyMaster.ISafeArchiveExtractor, MCAROC_Analysis.Services.CompanyMaster.SafeArchiveExtractor>();
builder.Services.AddScoped<MCAROC_Analysis.Services.CompanyMaster.ISyncLockLease, MCAROC_Analysis.Services.CompanyMaster.DistributedAppLockLease>();
builder.Services.AddScoped<MCAROC_Analysis.Services.CompanyMaster.ICompanyMasterDeltaService, MCAROC_Analysis.Services.CompanyMaster.CompanyMasterDeltaService>();
builder.Services.AddHostedService<MCAROC_Analysis.Services.CompanyMaster.CompanyMasterSyncWorker>();

// #164 internal calculation-audit access gate — a feature-scoped cookie scheme, deliberately NOT the
// application's default authentication scheme (AddAuthentication() with no scheme name argument). Every
// existing endpoint in this app stays exactly as unauthenticated as it is today; only
// [Authorize(AuthenticationSchemes = "InternalReviewer")] on CalculationAuditController is affected.
builder.Services.AddAuthentication()
    .AddCookie(AnalystAccessConstants.AuthenticationScheme, o =>
    {
        o.LoginPath = "/analyst/login";
        o.Cookie.Name = "mcaroc_analyst_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    })
    .AddCookie("InternalReviewer", o =>
    {
        o.LoginPath = "/internal/login";
        o.Cookie.Name = "mcaroc_internal_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AnalystAccessConstants.Policy, policy =>
    {
        policy.AuthenticationSchemes.Add(AnalystAccessConstants.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireRole(AnalystAccessConstants.Role);
    });
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("InternalLogin", httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0
        }));
    options.AddPolicy("AnalystLogin", httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0
        }));
});

var app = builder.Build();

if (analystOperationalCommand)
{
    Environment.ExitCode = string.Equals(args[0], AnalystProvisioningCommand.Argument, StringComparison.Ordinal)
        ? await AnalystProvisioningCommand.RunAsync(app.Services, args)
        : await AnalystAssignmentCommand.RunAsync(app.Services, args);
    return;
}

MCAROC_Analysis.Services.Registry.FileRegistrySnapshotStore.ValidatePreflight(app.Services, app.Environment);

// Register the bundled Fraunces / IBM Plex fonts with QuestPDF so the dossier renders identically
// regardless of what fonts the host machine has installed.
DossierFonts.Register(app.Environment.WebRootPath);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

var staticFileContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticFileContentTypeProvider.Mappings[".mjs"] = "text/javascript";
staticFileContentTypeProvider.Mappings[".wasm"] = "application/wasm";
staticFileContentTypeProvider.Mappings[".bcmap"] = "application/octet-stream";
staticFileContentTypeProvider.Mappings[".pfb"] = "application/x-font-type1";
staticFileContentTypeProvider.Mappings[".ttf"] = "font/ttf";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticFileContentTypeProvider
});

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();

// docs/pipeline-automation-plan.md §5.5 — breaker state for every tracked integration, plus the oldest
// still-Queued auto-fetch job's age (a large value here, with the reference-tool breaker Healthy, points at
// a lost enqueue rather than an upstream outage). Anonymous: no sensitive data, meant for an external
// monitor to poll without its own credential.
app.MapGet("/health", async (AppDbContext db, IIntegrationHealthService health, CancellationToken ct) =>
{
    var integrations = new List<object>();
    foreach (var name in Enum.GetValues<IntegrationName>())
    {
        var row = await health.GetAsync(name, ct);
        integrations.Add(new
        {
            name = name.ToString(),
            state = (row?.State ?? IntegrationHealthState.Healthy).ToString(),
            consecutiveFailures = row?.ConsecutiveFailures ?? 0,
            lastSuccessUtc = row?.LastSuccessUtc,
            lastError = row?.LastError,
            openedUtc = row?.OpenedUtc,
            nextProbeUtc = row?.NextProbeUtc
        });
    }

    var oldestQueuedAutoFetchUtc = await db.AutoFetchJobs
        .Where(j => j.Status == AutoFetchJobStatus.Queued)
        .OrderBy(j => j.CreatedUtc)
        .Select(j => (DateTime?)j.CreatedUtc)
        .FirstOrDefaultAsync(ct);

    return Results.Ok(new
    {
        utcNow = DateTime.UtcNow,
        integrations,
        autoFetch = new
        {
            oldestQueuedJobCreatedUtc = oldestQueuedAutoFetchUtc,
            oldestQueuedJobAgeSeconds = oldestQueuedAutoFetchUtc is { } t ? (DateTime.UtcNow - t).TotalSeconds : (double?)null
        }
    });
}).AllowAnonymous();

app.Run();
