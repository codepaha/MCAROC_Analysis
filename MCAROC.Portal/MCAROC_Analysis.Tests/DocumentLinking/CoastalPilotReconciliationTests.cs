using System.Security.Cryptography;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

/// <summary>
/// Deliverable 4: reconciles Deliverable 2's (charge) and Deliverable 3's (financial) already-
/// computed results over the real Coastal corpus into one unified view — proves the corpus-balance
/// invariant (every one of the 814 manifest entries lands in exactly one unified bucket) and the
/// zero-cross-domain-collision invariant (no document accepted as both a charge link and a
/// financial link). Mirrors CoastalChargeLinkTests/CoastalFinancialLinkTests' pattern exactly:
/// [SkippableFact] for anything touching the real local corpus, plain [Fact] for anything only
/// validating the committed JSON fixture (so CI's Linux job, which has no local corpus, still gets
/// real coverage against the fixture while every real-corpus assertion skips cleanly there).
/// </summary>
public class CoastalPilotReconciliationTests
{
    private const string ZipPath = @"E:\Downloads\Coastal data\COASTAL PROJECTS LIMITED Documents.zip";
    private const string RocPath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982.xls";
    private const string ChargePath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982-charge.xls";

    private const string CommittedBaselineSha256 = "619A18B753799A8C42530DE0464D9D65EB1A27ACFD1E6004DB821153CD45F27E";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static CoastalPilotReconciliationTests()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static string GetFixturePath()
    {
        var sourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "coastal_d4_baseline.json"));
        if (File.Exists(sourceDir)) return sourceDir;

        var localBin = Path.Combine(AppContext.BaseDirectory, "Fixtures", "coastal_d4_baseline.json");
        return localBin;
    }

    private static List<CoastalPilotReconciliationEntry> LoadBaselineEntries()
    {
        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture not found at {fixturePath}");
        var json = File.ReadAllText(fixturePath);
        var entries = JsonSerializer.Deserialize<List<CoastalPilotReconciliationEntry>>(json, JsonOptions);
        Assert.NotNull(entries);
        return entries;
    }

    private static (IReadOnlyList<RocCharge> Charges, IReadOnlyList<RocChargeEvent> Events) LoadCoastalWorkbooks()
    {
        var reader = new ExcelSheetReader();
        var rocWorkbook = reader.ReadWorkbook(RocPath);
        var chargeWorkbook = reader.ReadWorkbook(ChargePath);

        var parseResult = ChargesParser.Parse(
            rocWorkbook: rocWorkbook,
            chargeWorkbook: chargeWorkbook,
            chargeWorkbookIdentityMatches: true,
            requestId: 1,
            ingestionRunId: 1,
            rocSourceDocumentId: 1,
            chargeSourceDocumentId: 2);

        var charges = parseResult.Items;
        var events = charges.SelectMany(c => c.Events).ToList();
        return (charges, events);
    }

    private static (List<FinancialYearData> Years, List<FinancialFact> Facts, FinancialLinkTargetCatalog Catalog) LoadCoastalFinancialData()
    {
        var reader = new ExcelSheetReader();
        var rocWorkbook = reader.ReadWorkbook(RocPath);

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

        return (allYears, allFacts, combinedCatalog);
    }

    private static CoastalPilotReconciliationResult ExecuteLive()
    {
        var (charges, events) = LoadCoastalWorkbooks();
        using var chargeZip = File.OpenRead(ZipPath);
        var chargeResult = CoastalChargeLinkService.Execute(chargeZip, charges, events);

        var (years, facts, catalog) = LoadCoastalFinancialData();
        using var financialZip = File.OpenRead(ZipPath);
        var financialResult = CoastalFinancialLinkService.Execute(financialZip, years, facts, catalog);

        return CoastalPilotReconciliationService.Execute(chargeResult, financialResult);
    }

    [Fact]
    public void BaselineFixture_MatchesCommittedHashAndCorpusBalanceInvariant()
    {
        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture file not found at {fixturePath}");

        var bytes = File.ReadAllBytes(fixturePath);
        var normalized = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        var hashHex = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        Assert.Equal(CommittedBaselineSha256, hashHex, ignoreCase: true);

        var entries = LoadBaselineEntries();

        // The corpus-balance invariant: every one of the 814 manifest entries appears in exactly
        // one unified outcome bucket. Real numbers from the Coastal corpus (verified 2026-09-15):
        // 169 Duplicate + 160 LinkedAsCharge + 12 LinkedAsFinancial + 26 PendingReview +
        // 372 UnlinkedNoCandidate + 75 UnlinkedOutOfScope = 814.
        Assert.Equal(814, entries.Count);
        Assert.Equal(169, entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.Duplicate));
        Assert.Equal(160, entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.LinkedAsCharge));
        Assert.Equal(12, entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.LinkedAsFinancial));
        Assert.Equal(26, entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.PendingReview));
        Assert.Equal(372, entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.UnlinkedNoCandidate));
        Assert.Equal(75, entries.Count(e => e.UnifiedOutcome == PilotUnifiedOutcome.UnlinkedOutOfScope));

        // PendingReview (26) is exactly D2's 14 + D3's 12 with zero overlap — no single document
        // needs review from both domains at once.
        var pending = entries.Where(e => e.UnifiedOutcome == PilotUnifiedOutcome.PendingReview).ToList();
        Assert.Equal(14, pending.Count(e => e.ChargeOutcome == PilotLinkOutcome.PendingReview));
        Assert.Equal(12, pending.Count(e => e.FinancialOutcome == PilotFinancialLinkOutcome.PendingReview));
    }

    [SkippableFact]
    public void Execute_ZeroCrossDomainCollisions()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal fixture files not found.");

        var result = ExecuteLive();

        Assert.False(result.HasCrossDomainCollisions,
            "A document was accepted as both a charge link and a financial link — one of D2/D3 disagrees about what this document is.");
        Assert.Empty(result.CrossDomainCollisions);
    }

    [SkippableFact]
    public void Execute_MatchesBaselineFixtureExactly()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal fixture files not found.");

        var result = ExecuteLive();
        var ordered = result.Entries
            .OrderBy(e => e.OuterEntryFullPath, StringComparer.Ordinal)
            .ThenBy(e => e.NestedEntryRelativePath, StringComparer.Ordinal)
            .ToList();

        var baseline = LoadBaselineEntries();
        Assert.Equal(baseline.Count, ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var actual = ordered[i];
            var expected = baseline[i];

            Assert.Equal(expected.OuterEntryFullPath, actual.OuterEntryFullPath);
            Assert.Equal(expected.NestedEntryRelativePath, actual.NestedEntryRelativePath);
            Assert.Equal(expected.Sha256Hex, actual.Sha256Hex);
            Assert.Equal(expected.UnifiedOutcome, actual.UnifiedOutcome);
            Assert.Equal(expected.ChargeOutcome, actual.ChargeOutcome);
            Assert.Equal(expected.ChargeReason, actual.ChargeReason);
            Assert.Equal(expected.FinancialOutcome, actual.FinancialOutcome);
            Assert.Equal(expected.FinancialReason, actual.FinancialReason);
        }
    }

    [Fact]
    public void ToMarkdown_ReportsTotalsAndZeroCollisions()
    {
        var entries = LoadBaselineEntries();
        var result = new CoastalPilotReconciliationResult(entries, []);

        var markdown = result.ToMarkdown();

        Assert.Contains("Total PDFs: 814", markdown);
        Assert.Contains("Cross-domain collisions: 0", markdown);
        Assert.Contains("| Duplicate | 169 |", markdown);
        Assert.Contains("| LinkedAsCharge | 160 |", markdown);
        Assert.Contains("| LinkedAsFinancial | 12 |", markdown);
        Assert.Contains("| PendingReview | 26 |", markdown);
    }

    [Fact]
    public void Execute_ThrowsWhenPassesCoverDifferentManifests()
    {
        var chargeEntry = new CoastalChargeLinkResultEntry
        {
            OuterEntryFullPath = "a.zip", NestedEntryRelativePath = "a.pdf", Sha256Hex = "x",
            Outcome = PilotLinkOutcome.UnlinkedOutOfScope, Reason = PilotLinkReason.NonChargeDocument,
            DateMatchMode = ChargeDateMatchMode.None, IsCanonical = true,
            CanonicalOuterEntryFullPath = "a.zip", CanonicalNestedEntryRelativePath = "a.pdf",
            EvidenceJson = "{}"
        };
        var chargeResult = new CoastalChargeLinkResult([chargeEntry]);
        var financialResult = new CoastalFinancialLinkResult { Entries = [] };

        Assert.Throws<InvalidOperationException>(() => CoastalPilotReconciliationService.Execute(chargeResult, financialResult));
    }
}
