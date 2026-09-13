// Dev-only utility: runs the Excel parsers (not the full DB-backed orchestrator, to avoid touching a
// real database or needing SQL Server available) against every company folder in the real sample dataset,
// and prints a coverage table. Never commits/reads the proprietary sample files into the repo — the path
// is supplied as an argument, pointing at wherever they live on disk locally.
//
// Usage: dotnet run --project Tools/IngestionValidationRunner -- "E:\Downloads\Vition REPORTS\ROC REPORTS"

using System.Text;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // legacy .xls encodings

var rootPath = args.Length > 0 ? args[0] : @"E:\Downloads\Vition REPORTS\ROC REPORTS";
if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"Sample folder not found: {rootPath}");
    return 1;
}

var reader = new ExcelSheetReader();
List<string[]> rows =
[
    ["Company", "Profile", "Directors", "Officers", "Assoc.", "Sharehold.", "FinYears", "Charges", "ChgEvents", "MSME", "GST", "EPFO", "Auditor", "Litig.", "Warnings", "Errors"]
];

var allWarnings = new List<CompanyIssue>();
var allErrors = new List<CompanyIssue>();

var companyDirs = Directory.GetDirectories(rootPath).OrderBy(d => d).ToList();
foreach (var dir in companyDirs)
{
    var files = Directory.GetFiles(dir, "*.xls");
    var chargeFile = files.FirstOrDefault(f => Path.GetFileName(f).Contains("charge", StringComparison.OrdinalIgnoreCase));
    var rocFile = files.FirstOrDefault(f => f != chargeFile);

    var companyName = Path.GetFileName(dir);
    if (rocFile is null)
    {
        rows.Add([companyName, "NO ROC FILE", "", "", "", "", "", "", "", "", "", "", "", "", "", "1"]);
        continue;
    }

    var warnings = 0;
    var errors = 0;
    void Tally<T>(ParseResult<T> r)
    {
        warnings += r.Warnings.Count;
        errors += r.Errors.Count;
        foreach (var w in r.Warnings) allWarnings.Add(new(companyName, w));
        foreach (var e in r.Errors) allErrors.Add(new(companyName, e));
    }

    try
    {
        var rocWorkbook = reader.ReadWorkbook(rocFile);
        var chargeWorkbook = chargeFile is not null ? reader.ReadWorkbook(chargeFile) : null;

        var companySheet = SheetAliases.Find(rocWorkbook, SheetAliases.CompanyProfile);
        var companyResult = companySheet is not null ? CompanyProfileParser.Parse(companySheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.CompanyProfile>();
        Tally(companyResult);

        var directorsSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Directors);
        List<MCAROC_Analysis.Data.Entities.CompanyOfficer> officers = [];
        var directorsResult = directorsSheet is not null ? DirectorsParser.Parse(directorsSheet, 0, 0, null, out officers) : new ParseResult<MCAROC_Analysis.Data.Entities.Director>();
        Tally(directorsResult);

        var otherDirSheet = SheetAliases.Find(rocWorkbook, SheetAliases.OtherDirectorships);
        var assocResult = otherDirSheet is not null ? OtherDirectorshipsParser.Parse(otherDirSheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.DirectorAssociation>();
        Tally(assocResult);

        var directorShSheet = SheetAliases.Find(rocWorkbook, SheetAliases.DirectorShareholding);
        var majorShSheet = SheetAliases.Find(rocWorkbook, SheetAliases.MajorShareholding);
        var shResult = ShareholdingParser.Parse(directorShSheet, majorShSheet, 0, 0, null, null);
        Tally(shResult);

        var finSheet = SheetAliases.Find(rocWorkbook, SheetAliases.StandaloneFinancialData);
        var finResult = finSheet is not null ? StandaloneFinancialDataParser.Parse(finSheet, 0, 0, null, out _) : new ParseResult<MCAROC_Analysis.Data.Entities.FinancialYearData>();
        Tally(finResult);

        var chargesResult = ChargesParser.Parse(rocWorkbook, chargeWorkbook, chargeWorkbook is not null, 0, 0, null, null);
        Tally(chargesResult);
        var chargeEventCount = chargesResult.Items.Sum(c => c.Events.Count);

        var msmeSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Msme);
        var msmeResult = msmeSheet is not null ? MsmeParser.Parse(msmeSheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.MsmePayment>();
        Tally(msmeResult);

        var gstSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Gst);
        var gstAnnexSheet = SheetAliases.Find(rocWorkbook, SheetAliases.GstAnnexure);
        var gstOutput = GstParser.Parse(gstSheet, gstAnnexSheet, 0, 0, null);
        Tally(gstOutput.Registrations);
        Tally(gstOutput.Filings);

        var epfoSheet = SheetAliases.Find(rocWorkbook, SheetAliases.EpfoAnnexure);
        var epfoResult = epfoSheet is not null ? EpfoParser.Parse(epfoSheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.EpfoContribution>();
        Tally(epfoResult);

        var auditorsSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Auditors);
        var auditorsResult = auditorsSheet is not null ? AuditorsParser.Parse(auditorsSheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.AuditorObservation>();
        Tally(auditorsResult);

        var legalSheet = SheetAliases.Find(rocWorkbook, SheetAliases.LegalHistory);
        var legalResult = legalSheet is not null ? LegalHistoryParser.Parse(legalSheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.Litigation>();
        Tally(legalResult);

        rows.Add([
            companyName,
            companyResult.Items.Count > 0 ? "OK" : "MISSING",
            directorsResult.Items.Count.ToString(),
            officers.Count.ToString(),
            assocResult.Items.Count.ToString(),
            shResult.Items.Count.ToString(),
            finResult.Items.Count.ToString(),
            chargesResult.Items.Count.ToString(),
            chargeEventCount.ToString(),
            msmeResult.Items.Count.ToString(),
            gstOutput.Registrations.Items.Count.ToString(),
            epfoResult.Items.Count.ToString(),
            auditorsResult.Items.Count.ToString(),
            legalResult.Items.Count.ToString(),
            warnings.ToString(),
            errors.ToString()
        ]);
    }
    catch (Exception ex)
    {
        rows.Add([companyName, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}", "", "", "", "", "", "", "", "", "", "", "", "", "", "1"]);
    }
}

var widths = Enumerable.Range(0, rows[0].Length)
    .Select(i => rows.Max(r => r[i].Length))
    .ToArray();

foreach (var row in rows)
{
    Console.WriteLine(string.Join(" | ", row.Select((cell, i) => cell.PadRight(widths[i]))));
}

var totalErrors = rows.Skip(1).Sum(r => int.TryParse(r[^1], out var e) ? e : 1);
var totalWarnings = rows.Skip(1).Sum(r => int.TryParse(r[^2], out var w) ? w : 0);
Console.WriteLine();
if (allWarnings.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("================================================================================");
    Console.WriteLine("WARNING BREAKDOWN BY CATEGORY");
    Console.WriteLine("================================================================================");

    var grouped = allWarnings
        .GroupBy(w => new { w.Issue.ParserName, w.Issue.IssueCode, Field = w.Issue.FieldName ?? "(none)" })
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key.ParserName)
        .ToList();

    List<string[]> warnSummaryRows =
    [
        ["Parser", "IssueCode", "Field", "Count", "Companies", "Sample RawValue", "Sample Message"]
    ];

    foreach (var g in grouped)
    {
        var sample = g.First();
        var distinctCompanies = g.Select(x => x.Company).Distinct().Count();
        var sampleRaw = sample.Issue.RawValue ?? "";
        if (sampleRaw.Length > 25) sampleRaw = sampleRaw[..22] + "...";
        var sampleMsg = sample.Issue.Message;
        if (sampleMsg.Length > 60) sampleMsg = sampleMsg[..57] + "...";

        warnSummaryRows.Add([
            g.Key.ParserName,
            g.Key.IssueCode,
            g.Key.Field,
            g.Count().ToString(),
            distinctCompanies.ToString(),
            sampleRaw,
            sampleMsg
        ]);
    }

    var warnWidths = Enumerable.Range(0, warnSummaryRows[0].Length)
        .Select(i => warnSummaryRows.Max(r => r[i].Length))
        .ToArray();

    foreach (var row in warnSummaryRows)
    {
        Console.WriteLine(string.Join(" | ", row.Select((cell, i) => cell.PadRight(warnWidths[i]))));
    }

    Console.WriteLine();
    Console.WriteLine("================================================================================");
    Console.WriteLine("DETAILED BREAKDOWN BY GROUP (with companies & messages)");
    Console.WriteLine("================================================================================");
    foreach (var g in grouped)
    {
        var distinctCompanies = g.Select(x => x.Company).Distinct().ToList();
        var distinctMessages = g.Select(x => x.Issue.Message).Distinct().Take(5).ToList();
        var sampleRawValues = g.Select(x => x.Issue.RawValue).Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(5).ToList();

        Console.WriteLine($"\n[{g.Key.ParserName}] {g.Key.IssueCode} (Field: {g.Key.Field}) -> {g.Count()} occurrences across {distinctCompanies.Count} companies");
        Console.WriteLine($"  Companies: {string.Join(", ", distinctCompanies)}");
        Console.WriteLine($"  Messages: {string.Join(" | ", distinctMessages)}");
        if (sampleRawValues.Count > 0)
        {
            Console.WriteLine($"  Raw values: {string.Join(", ", sampleRawValues)}");
        }
    }

    var jsonPath = Path.Combine(AppContext.BaseDirectory, "warnings-report.json");
    File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(allWarnings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nFull warnings JSON written to: {jsonPath}");
}

Console.WriteLine();
Console.WriteLine($"{companyDirs.Count} companies processed. Total warnings: {totalWarnings}. Total errors: {totalErrors}.");

return totalErrors > 0 ? 1 : 0;

record CompanyIssue(string Company, ParseIssue Issue);
