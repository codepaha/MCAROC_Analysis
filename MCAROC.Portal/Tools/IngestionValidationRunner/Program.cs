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
    ["Company", "Profile", "Directors", "Assoc.", "Sharehold.", "FinYears", "Charges", "ChgEvents", "MSME", "GST", "EPFO", "Auditor", "Litig.", "Warnings", "Errors"]
];

var companyDirs = Directory.GetDirectories(rootPath).OrderBy(d => d).ToList();
foreach (var dir in companyDirs)
{
    var files = Directory.GetFiles(dir, "*.xls");
    var chargeFile = files.FirstOrDefault(f => Path.GetFileName(f).Contains("charge", StringComparison.OrdinalIgnoreCase));
    var rocFile = files.FirstOrDefault(f => f != chargeFile);

    var companyName = Path.GetFileName(dir);
    if (rocFile is null)
    {
        rows.Add([companyName, "NO ROC FILE", "", "", "", "", "", "", "", "", "", "", "", "", "1"]);
        continue;
    }

    var warnings = 0;
    var errors = 0;
    void Tally<T>(ParseResult<T> r) { warnings += r.Warnings.Count; errors += r.Errors.Count; }

    try
    {
        var rocWorkbook = reader.ReadWorkbook(rocFile);
        var chargeWorkbook = chargeFile is not null ? reader.ReadWorkbook(chargeFile) : null;

        var companySheet = SheetAliases.Find(rocWorkbook, SheetAliases.CompanyProfile);
        var companyResult = companySheet is not null ? CompanyProfileParser.Parse(companySheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.CompanyProfile>();
        Tally(companyResult);

        var directorsSheet = SheetAliases.Find(rocWorkbook, SheetAliases.Directors);
        var directorsResult = directorsSheet is not null ? DirectorsParser.Parse(directorsSheet, 0, 0, null) : new ParseResult<MCAROC_Analysis.Data.Entities.Director>();
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
        rows.Add([companyName, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}", "", "", "", "", "", "", "", "", "", "", "", "", "1"]);
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
Console.WriteLine($"{companyDirs.Count} companies processed. Total warnings: {totalWarnings}. Total errors: {totalErrors}.");

return totalErrors > 0 ? 1 : 0;
