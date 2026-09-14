using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public static class D3BaselineGenerator
{
    public static void GenerateFixture(string zipPath, string rocPath, string outputPath)
    {
        var reader = new ExcelSheetReader();
        var rocWorkbook = reader.ReadWorkbook(rocPath);

        var standaloneSheet = rocWorkbook.FirstOrDefault(s => s.Name.Equals("Standalone Financial Data", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Standalone Financial Data sheet not found");
        var consolidatedSheet = rocWorkbook.FirstOrDefault(s => s.Name.Equals("Consolidated Financial Data", StringComparison.OrdinalIgnoreCase))
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

        using var zipStream = File.OpenRead(zipPath);
        var result = CoastalFinancialLinkService.Execute(zipStream, allYears, allFacts, combinedCatalog);

        var orderedEntries = result.Entries
            .OrderBy(e => e.OuterEntryFullPath, StringComparer.Ordinal)
            .ThenBy(e => e.NestedEntryRelativePath, StringComparer.Ordinal)
            .ToList();

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(orderedEntries, jsonOptions);

        // Enforce LF line endings
        var lfJson = json.Replace("\r\n", "\n").Replace("\r", "\n");

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, lfJson, new UTF8Encoding(false));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lfJson));
        var hashHex = Convert.ToHexString(hash);
        Console.WriteLine($"Generated D3 baseline: {outputPath}");
        Console.WriteLine($"Total Entries: {orderedEntries.Count}");
        Console.WriteLine($"SHA-256: {hashHex}");
    }
}
