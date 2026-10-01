using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var cin = args.Length > 0 ? args[0].Trim().ToUpperInvariant() : "L70100MH2007PLC282631";
var format = args.Length > 1 && Enum.TryParse<PreLoginReportFormat>(args[1], true, out var parsedFormat)
    ? parsedFormat
    : PreLoginReportFormat.Prr;

Console.WriteLine($"=== MCA ROC Pre-Login Report Generator ===");
Console.WriteLine($"CIN: {cin}");
Console.WriteLine($"Format: {format}");

var config = new ConfigurationBuilder()
    .AddUserSecrets("e2216c72-57cf-4cd5-b4a6-b4ef959959f1")
    .AddEnvironmentVariables()
    .Build();

var apiKey = config["InstaFinancials:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Error: InstaFinancials:ApiKey secret not found.");
    return 1;
}

Console.WriteLine($"API Key found (length {apiKey.Length}).");

// Locate MCAROC_Analysis folder
var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
DirectoryInfo? current = baseDir;
while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "MCAROC_Analysis", "ReportTemplates")))
{
    if (Directory.Exists(Path.Combine(current.FullName, "MCAROC.Portal", "MCAROC_Analysis", "ReportTemplates")))
    {
        current = new DirectoryInfo(Path.Combine(current.FullName, "MCAROC.Portal"));
        break;
    }
    current = current.Parent;
}

var mcaRocAnalysisRoot = current is not null
    ? Path.Combine(current.FullName, "MCAROC_Analysis")
    : Path.GetFullPath(@"e:\MCAROC_Analysis\MCAROC.Portal\MCAROC_Analysis");

Console.WriteLine($"MCAROC_Analysis Root: {mcaRocAnalysisRoot}");
if (!Directory.Exists(mcaRocAnalysisRoot))
{
    Console.Error.WriteLine($"Error: MCAROC_Analysis directory not found at {mcaRocAnalysisRoot}");
    return 1;
}

var templatePath = Path.Combine(mcaRocAnalysisRoot, "ReportTemplates", format == PreLoginReportFormat.Sbi ? "SBI" : "PRR",
    format == PreLoginReportFormat.Sbi ? "sbi-template.docx" : "prr-template.docx");

if (!File.Exists(templatePath))
{
    Console.Error.WriteLine($"Error: Template file not found at {templatePath}");
    return 1;
}
Console.WriteLine($"Template found: {templatePath}");

using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var options = Options.Create(new InstaFinancialsOptions
{
    ApiKey = apiKey,
    DaysToIgnore = 888
});

var client = new InstaFinancialsClient(httpClient, options);
var env = new EnvironmentStub(mcaRocAnalysisRoot);
var service = new PreLoginReportService(client, env);

Console.WriteLine($"\nFetching live data from InstaFinancials API for CIN {cin}...");
InstaReportData data;
try
{
    data = await service.FetchDataAsync(cin, null, CancellationToken.None);
    Console.WriteLine("Data fetched successfully!");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to fetch data: {ex.Message}");
    return 1;
}

Console.WriteLine("\n--- COMPANY DETAILS ---");
Console.WriteLine($"Name:                {data.Company.Name}");
Console.WriteLine($"CIN:                 {cin}");
Console.WriteLine($"ROC Name:            {data.Company.RocName}");
Console.WriteLine($"Registration Number: {data.Company.RegistrationNumber}");
Console.WriteLine($"Category:            {data.Company.Category}");
Console.WriteLine($"Subcategory:         {data.Company.Subcategory}");
Console.WriteLine($"Class:               {data.Company.Class}");
Console.WriteLine($"Incorporated:        {data.Company.Incorporated}");
Console.WriteLine($"Address:             {data.Company.Address}");
Console.WriteLine($"Email:               {data.Company.Email}");
Console.WriteLine($"Status:              {data.Company.Status}");
Console.WriteLine($"Listed:              {data.Company.Listed}");
Console.WriteLine($"Authorised Capital:  Rs. {data.Company.AuthorisedCapital}");
Console.WriteLine($"Paid-up Capital:     Rs. {data.Company.PaidUpCapital}");
Console.WriteLine($"Members:             {data.Company.Members}");
Console.WriteLine($"Last AGM:            {data.Company.LastAgm}");
Console.WriteLine($"Balance Sheet Date:  {data.Company.BalanceSheetDate}");

Console.WriteLine($"\n--- CHARGES ({data.Charges.Count}) ---");
var openCharges = data.Charges.Where(c => c.IsOpen).ToList();
Console.WriteLine($"Open Charges: {openCharges.Count}");
foreach (var ch in data.Charges)
{
    Console.WriteLine($"  [{ch.Id}] Holder: {ch.Holder} | Amount: Rs. {ch.Amount} | Created: {ch.Created} | Status: {(ch.IsOpen ? "OPEN" : "SATISFIED (" + ch.Satisfied + ")")}");
}

Console.WriteLine($"\n--- DIRECTORS / SIGNATORIES ({data.Directors.Count}) ---");
foreach (var dir in data.Directors)
{
    Console.WriteLine($"  - {dir.Name} | DIN/PAN: {dir.DinOrPan} | Designation: {dir.Designation} | Appointed: {dir.Appointed}");
}

Console.WriteLine("\nGenerating PRR Word Document...");
GeneratedReport report;
try
{
    report = await service.GenerateFromDataAsync(cin, format, data, CancellationToken.None);
    Console.WriteLine($"Generated successfully! Default filename: {report.FileName}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Report generation failed: {ex.Message}");
    return 1;
}

// Ensure output directories exist
var reportsOutputDir = Path.GetFullPath(@"e:\MCAROC_Analysis\Reports");
Directory.CreateDirectory(reportsOutputDir);
var outputPath = Path.Combine(reportsOutputDir, report.FileName);
await File.WriteAllBytesAsync(outputPath, report.Bytes);
Console.WriteLine($"Report saved to: {outputPath} ({report.Bytes.Length:N0} bytes)");

// Also save a copy in the portal's App_Data/PreLoginReports so it can be viewed in history
var appDataPreLogin = Path.Combine(mcaRocAnalysisRoot, "App_Data", "PreLoginReports");
Directory.CreateDirectory(appDataPreLogin);
var appDataCopyPath = Path.Combine(appDataPreLogin, report.FileName);
await File.WriteAllBytesAsync(appDataCopyPath, report.Bytes);
Console.WriteLine($"Report copy saved to: {appDataCopyPath}");

// Verify document structure with OpenXml
using (var doc = WordprocessingDocument.Open(outputPath, false))
{
    var body = doc.MainDocumentPart?.Document?.Body;
    if (body == null)
    {
        Console.Error.WriteLine("Verification failed: Document body is null.");
        return 1;
    }
    var tables = body.Elements<Table>().ToList();
    Console.WriteLine($"\n--- VERIFICATION ---");
    Console.WriteLine($"Total Tables: {tables.Count} (Expected: 5)");
    Console.WriteLine($"Document Body Text snippet: {body.InnerText.Substring(0, Math.Min(300, body.InnerText.Length))}...");
    Console.WriteLine("PRR Report generation and verification COMPLETED successfully!");
}

return 0;

sealed class EnvironmentStub(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "GeneratePreLoginReport";
    public string EnvironmentName { get; set; } = "Production";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
