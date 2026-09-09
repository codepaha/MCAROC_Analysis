using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
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
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
