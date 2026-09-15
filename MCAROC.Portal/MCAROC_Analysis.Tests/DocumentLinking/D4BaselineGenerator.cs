using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public static class D4BaselineGenerator
{
    public static void GenerateFixture(string zipPath, string rocPath, string chargePath, string outputPath)
    {
        var reader = new ExcelSheetReader();

        // Charge side (mirrors CoastalChargeLinkTests.LoadCoastalWorkbooks).
        var rocWorkbookForCharges = reader.ReadWorkbook(rocPath);
        var chargeWorkbook = reader.ReadWorkbook(chargePath);
        var chargeParseResult = ChargesParser.Parse(
            rocWorkbook: rocWorkbookForCharges,
            chargeWorkbook: chargeWorkbook,
            chargeWorkbookIdentityMatches: true,
            requestId: 1,
            ingestionRunId: 1,
            rocSourceDocumentId: 1,
            chargeSourceDocumentId: 2);
        var charges = chargeParseResult.Items;
        var events = charges.SelectMany(c => c.Events).ToList();

        using var chargeZipStream = File.OpenRead(zipPath);
        var chargeResult = CoastalChargeLinkService.Execute(chargeZipStream, charges, events);

        // Financial side (mirrors D3BaselineGenerator/CoastalFinancialLinkTests.LoadCoastalFinancialData).
        var rocWorkbookForFinancials = reader.ReadWorkbook(rocPath);
        var standaloneSheet = rocWorkbookForFinancials.FirstOrDefault(s => s.Name.Equals("Standalone Financial Data", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Standalone Financial Data sheet not found");
        var consolidatedSheet = rocWorkbookForFinancials.FirstOrDefault(s => s.Name.Equals("Consolidated Financial Data", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Consolidated Financial Data sheet not found");

        var standaloneResult = StandaloneFinancialDataParser.Parse(
            standaloneSheet, 0, 0, null, out var standaloneFacts, out var standaloneCatalog, FinancialBasis.Standalone);
        var consolidatedResult = StandaloneFinancialDataParser.Parse(
            consolidatedSheet, 0, 0, null, out var consolidatedFacts, out var consolidatedCatalog, FinancialBasis.Consolidated);

        var allYears = new List<FinancialYearData>();
        allYears.AddRange(standaloneResult.Items);
        allYears.AddRange(consolidatedResult.Items);

        var allFacts = new List<FinancialFact>();
        allFacts.AddRange(standaloneFacts);
        allFacts.AddRange(consolidatedFacts);

        var combinedCatalog = new FinancialLinkTargetCatalog();
        combinedCatalog.AddRange(standaloneCatalog.AllTargets);
        combinedCatalog.AddRange(consolidatedCatalog.AllTargets);

        using var financialZipStream = File.OpenRead(zipPath);
        var financialResult = CoastalFinancialLinkService.Execute(financialZipStream, allYears, allFacts, combinedCatalog);

        Console.WriteLine($"D2 raw — Duplicate:{chargeResult.ManifestDuplicateBypassedCount} OutOfScope:{chargeResult.UnlinkedOutOfScopeCount} AutoAccepted:{chargeResult.AutoAcceptedCount} PendingReview:{chargeResult.PendingReviewCount} NoCandidate:{chargeResult.UnlinkedNoCandidateCount}");
        Console.WriteLine($"D3 raw — Duplicate:{financialResult.ManifestDuplicateCount} OutOfScope:{financialResult.OutOfScopeCount} AutoAccepted:{financialResult.AutoAcceptedCount} PendingReview:{financialResult.PendingReviewCount} NoCandidate:{financialResult.UnlinkedNoCandidateCount}");

        // D4: reconcile the two already-computed results.
        var reconciliationResult = CoastalPilotReconciliationService.Execute(chargeResult, financialResult);

        var orderedEntries = reconciliationResult.Entries
            .OrderBy(e => e.OuterEntryFullPath, StringComparer.Ordinal)
            .ThenBy(e => e.NestedEntryRelativePath, StringComparer.Ordinal)
            .ToList();

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(orderedEntries, jsonOptions);
        var lfJson = json.Replace("\r\n", "\n").Replace("\r", "\n");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, lfJson, new UTF8Encoding(false));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lfJson));
        var hashHex = Convert.ToHexString(hash);

        Console.WriteLine($"Generated D4 baseline: {outputPath}");
        Console.WriteLine($"Total Entries: {orderedEntries.Count}");
        Console.WriteLine($"Cross-domain collisions: {reconciliationResult.CrossDomainCollisions.Count}");
        Console.WriteLine($"Duplicate: {reconciliationResult.DuplicateCount}");
        Console.WriteLine($"LinkedAsCharge: {reconciliationResult.LinkedAsChargeCount}");
        Console.WriteLine($"LinkedAsFinancial: {reconciliationResult.LinkedAsFinancialCount}");
        Console.WriteLine($"PendingReview: {reconciliationResult.PendingReviewCount}");
        Console.WriteLine($"UnlinkedNoCandidate: {reconciliationResult.UnlinkedNoCandidateCount}");
        Console.WriteLine($"UnlinkedOutOfScope: {reconciliationResult.UnlinkedOutOfScopeCount}");
        Console.WriteLine($"SHA-256: {hashHex}");
    }
}
