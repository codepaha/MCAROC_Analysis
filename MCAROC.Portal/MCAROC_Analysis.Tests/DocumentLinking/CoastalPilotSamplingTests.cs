using System.Security.Cryptography;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

/// <summary>
/// Deliverable 5: the auditable reviewer sample assembled from D2/D3's already-computed results.
/// Verifies the three floors from issue #188 — 100% of PendingReview (26 = 14 charge + 12
/// financial, a hard floor not a sample), >=20 AutoAccepted stratified across charge match modes
/// and financial basis, >=20 Unlinked* — plus the evidence contract on every record and full
/// determinism (same input, same sample, every time).
/// </summary>
public class CoastalPilotSamplingTests
{
    private const string ZipPath = @"E:\Downloads\Coastal data\COASTAL PROJECTS LIMITED Documents.zip";
    private const string RocPath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982.xls";
    private const string ChargePath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982-charge.xls";

    private const string CommittedBaselineSha256 = "C059E611FBBB6F7CE5F47603CDB1F01537B77CC9A490D41A7B4A55B02502C875";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static CoastalPilotSamplingTests()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static string GetFixturePath()
    {
        var sourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "coastal_d5_sample.json"));
        if (File.Exists(sourceDir)) return sourceDir;

        var localBin = Path.Combine(AppContext.BaseDirectory, "Fixtures", "coastal_d5_sample.json");
        return localBin;
    }

    private static List<CoastalPilotSampleRecord> LoadBaselineRecords()
    {
        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture not found at {fixturePath}");
        var json = File.ReadAllText(fixturePath);
        var records = JsonSerializer.Deserialize<List<CoastalPilotSampleRecord>>(json, JsonOptions);
        Assert.NotNull(records);
        return records;
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

    private static CoastalPilotSamplingResult ExecuteLive()
    {
        var (charges, events) = LoadCoastalWorkbooks();
        using var chargeZip = File.OpenRead(ZipPath);
        var chargeResult = CoastalChargeLinkService.Execute(chargeZip, charges, events);

        var (years, facts, catalog) = LoadCoastalFinancialData();
        using var financialZip = File.OpenRead(ZipPath);
        var financialResult = CoastalFinancialLinkService.Execute(financialZip, years, facts, catalog);

        return CoastalPilotSamplingService.Execute(chargeResult, financialResult);
    }

    [Fact]
    public void BaselineFixture_MatchesCommittedHashAndFloors()
    {
        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture file not found at {fixturePath}");

        var bytes = File.ReadAllBytes(fixturePath);
        var normalized = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        var hashHex = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        Assert.Equal(CommittedBaselineSha256, hashHex, ignoreCase: true);

        var records = LoadBaselineRecords();

        // Hard floor: 100% of every PendingReview item, both domains — 14 charge + 12 financial = 26.
        var pendingCharge = records.Count(r => r.Domain == SampleDomain.Charge && r.InferredOutcome == "PendingReview");
        var pendingFinancial = records.Count(r => r.Domain == SampleDomain.Financial && r.InferredOutcome == "PendingReview");
        Assert.Equal(14, pendingCharge);
        Assert.Equal(12, pendingFinancial);
        Assert.Equal(26, pendingCharge + pendingFinancial);

        // >=20 AutoAccepted, and both stratification axes are represented: at least two distinct
        // charge match-mode strata and both financial bases actually appear in the sample.
        var autoAccepted = records.Where(r => r.InferredOutcome == "AutoAccepted").ToList();
        Assert.True(autoAccepted.Count >= CoastalPilotSamplingService.MinAutoAcceptedSample,
            $"Expected at least {CoastalPilotSamplingService.MinAutoAcceptedSample} AutoAccepted samples, got {autoAccepted.Count}.");
        var chargeAutoStrata = autoAccepted.Where(r => r.Domain == SampleDomain.Charge).Select(r => r.InferredReason).Distinct().ToList();
        Assert.True(chargeAutoStrata.Count >= 2, $"Expected charge AutoAccepted samples to span multiple match modes, got: {string.Join(", ", chargeAutoStrata)}");
        var financialAutoStrata = autoAccepted.Where(r => r.Domain == SampleDomain.Financial).Select(r => r.StratumKey).Distinct().ToList();
        Assert.Contains("Financial:Standalone", financialAutoStrata);
        Assert.Contains("Financial:Consolidated", financialAutoStrata);

        // >=20 Unlinked* (out-of-scope + no-candidate).
        var unlinked = records.Count(r => r.InferredOutcome is "UnlinkedNoCandidate" or "UnlinkedOutOfScope");
        Assert.True(unlinked >= CoastalPilotSamplingService.MinUnlinkedSample,
            $"Expected at least {CoastalPilotSamplingService.MinUnlinkedSample} unlinked samples, got {unlinked}.");
    }

    [Fact]
    public void EveryRecord_HasTheFullEvidenceContract()
    {
        var records = LoadBaselineRecords();
        Assert.NotEmpty(records);

        foreach (var r in records)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Sha256Hex), $"{r.OuterEntryFullPath}/{r.NestedEntryRelativePath}: missing Sha256Hex");
            Assert.False(string.IsNullOrWhiteSpace(r.OuterEntryFullPath), "missing OuterEntryFullPath");
            Assert.False(string.IsNullOrWhiteSpace(r.NestedEntryRelativePath), "missing NestedEntryRelativePath");
            Assert.False(string.IsNullOrWhiteSpace(r.InferredOutcome), $"{r.NestedEntryRelativePath}: missing InferredOutcome");
            Assert.False(string.IsNullOrWhiteSpace(r.InferredReason), $"{r.NestedEntryRelativePath}: missing InferredReason");
            Assert.False(string.IsNullOrWhiteSpace(r.EvidenceSnippet), $"{r.NestedEntryRelativePath}: missing EvidenceSnippet");
            Assert.False(string.IsNullOrWhiteSpace(r.EvidenceJson), $"{r.NestedEntryRelativePath}: missing EvidenceJson");
            Assert.Equal(ReviewerDecision.Pending, r.ReviewerDecision); // starts unadjudicated
        }

        // Financial AutoAccepted records did find a concrete target — those always carry a page or a
        // workbook location. (PendingReview financial records don't get this guarantee: e.g.
        // "MissingBasisEvidence" means literally that — no usable target exists to point to, which is
        // exactly why it needs a human reviewer. EvidenceSnippet above still covers that case.)
        foreach (var r in records.Where(r => r.Domain == SampleDomain.Financial && r.InferredOutcome == "AutoAccepted"))
            Assert.True(r.EvidencePageNumber is not null || r.EvidenceSheetName is not null,
                $"{r.NestedEntryRelativePath}: AutoAccepted financial record has neither a PDF page nor a workbook target");
    }

    [Fact]
    public void ToJson_RoundTripsCleanly()
    {
        var records = LoadBaselineRecords();
        var result = new CoastalPilotSamplingResult(records);

        var json = result.ToJson();
        var reparsed = JsonSerializer.Deserialize<List<CoastalPilotSampleRecord>>(json, JsonOptions);

        Assert.NotNull(reparsed);
        Assert.Equal(records.Count, reparsed.Count);
    }

    [SkippableFact]
    public void Execute_MatchesBaselineFixtureExactly()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal fixture files not found.");

        var result = ExecuteLive();
        var ordered = result.Records
            .OrderBy(r => r.OuterEntryFullPath, StringComparer.Ordinal)
            .ThenBy(r => r.NestedEntryRelativePath, StringComparer.Ordinal)
            .ToList();

        var baseline = LoadBaselineRecords();
        Assert.Equal(baseline.Count, ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var actual = ordered[i];
            var expected = baseline[i];

            Assert.Equal(expected.OuterEntryFullPath, actual.OuterEntryFullPath);
            Assert.Equal(expected.NestedEntryRelativePath, actual.NestedEntryRelativePath);
            Assert.Equal(expected.Sha256Hex, actual.Sha256Hex);
            Assert.Equal(expected.Domain, actual.Domain);
            Assert.Equal(expected.StratumKey, actual.StratumKey);
            Assert.Equal(expected.InferredOutcome, actual.InferredOutcome);
            Assert.Equal(expected.InferredReason, actual.InferredReason);
        }
    }

    [SkippableFact]
    public void Execute_IsDeterministicAcrossRuns()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal fixture files not found.");

        var run1 = ExecuteLive();
        var run2 = ExecuteLive();

        Assert.Equal(run1.Records.Count, run2.Records.Count);
        var keys1 = run1.Records.Select(r => (r.OuterEntryFullPath, r.NestedEntryRelativePath)).OrderBy(k => k).ToList();
        var keys2 = run2.Records.Select(r => (r.OuterEntryFullPath, r.NestedEntryRelativePath)).OrderBy(k => k).ToList();
        Assert.Equal(keys1, keys2);
    }
}
