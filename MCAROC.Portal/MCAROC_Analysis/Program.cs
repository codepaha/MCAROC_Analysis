using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.Dashboard;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // legacy .xls encodings

const long MaxUploadBytes = 2_000_000_000; // matches ArchiveSafetyLimits.MaxArchiveSizeBytes — the real sample corpus is ~700MB

var builder = WebApplication.CreateBuilder(args);

// The default multipart/Kestrel body-size limits (128MB / effectively Kestrel's own default) are far below
// the size of a real MCA Filings archive (~700MB) — both need raising for the New Search upload to work.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
});

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<InstaFinancialsClient>(client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.Configure<InstaFinancialsOptions>(builder.Configuration.GetSection(InstaFinancialsOptions.SectionName));
builder.Services.AddScoped<PreLoginReportService>();
builder.Services.AddSingleton<PreLoginReportQueue>();
builder.Services.AddScoped<PreLoginReportJobService>();
builder.Services.AddHostedService<PreLoginReportWorker>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<IExcelSheetReader, ExcelSheetReader>();
builder.Services.AddScoped<FileValidationService>();
builder.Services.AddScoped<IngestionOrchestrator>();

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
    sp.GetRequiredService<ILogger<FilingBatchProcessor>>()));
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
builder.Services.AddScoped<AnalysisOrchestrator>();
builder.Services.AddHostedService<AnalysisWorker>();

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
builder.Services.AddScoped<RetrievalContextBuilder>();
builder.Services.AddScoped<ChatService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
