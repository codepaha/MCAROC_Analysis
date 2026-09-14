using System.Security.Cryptography;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;
using Xunit;

namespace MCAROC_Analysis.Tests.DocumentLinking;

public class CoastalChargeLinkTests
{
    private const string ZipPath = @"E:\Downloads\Coastal data\COASTAL PROJECTS LIMITED Documents.zip";
    private const string RocPath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982.xls";
    private const string ChargePath = @"E:\Downloads\Coastal data\U45203OR1995PLC003982-charge.xls";

    private const string CommittedBaselineSha256 = "BD1381111BF64CDA15B3FB7E35398EE8FB2405D5904CFF94B77EEE929D1F8542";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    static CoastalChargeLinkTests()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static string GetFixturePath()
    {
        var localBin = Path.Combine(AppContext.BaseDirectory, "Fixtures", "coastal_d2_baseline.json");
        if (File.Exists(localBin)) return localBin;

        var sourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "coastal_d2_baseline.json"));
        if (File.Exists(sourceDir)) return sourceDir;

        return localBin;
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

    [SkippableFact]
    public void OuterArchiveSha256_MatchesKnownGood()
    {
        Skip.If(!File.Exists(ZipPath), "Coastal fixture ZIP not present on this machine.");

        using var stream = File.OpenRead(ZipPath);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        var hashHex = Convert.ToHexString(hashBytes);

        Assert.Equal("1DABC81A46C44C68AA864DEB5A91E446663CDA61B30CA903BD2CC593DD87C1FA", hashHex, ignoreCase: true);
    }

    [SkippableFact]
    public void WorkbookParsedCounts_MatchExpectedGrounding()
    {
        Skip.If(!File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal workbook files not found.");

        var (charges, events) = LoadCoastalWorkbooks();

        // 213 charges: 163 Open, 50 Satisfied
        Assert.Equal(213, charges.Count);
        Assert.Equal(163, charges.Count(c => c.ChargeStatus == "Open"));
        Assert.Equal(50, charges.Count(c => c.ChargeStatus == "Closed" || c.ChargeStatus == "Satisfied"));

        // 337 events: 213 Creation, 74 Modification, 50 Satisfaction
        Assert.Equal(337, events.Count);
        Assert.Equal(213, events.Count(e => e.EventType == ChargeEventType.Creation));
        Assert.Equal(74, events.Count(e => e.EventType == ChargeEventType.Modification));
        Assert.Equal(50, events.Count(e => e.EventType == ChargeEventType.Satisfaction));
    }

    [Fact]
    public void BaselineFixture_MatchesCommittedHashAndInvariants()
    {
        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture file not found at {fixturePath}");

        // 1. Assert independently recorded SHA-256 hash (LF-normalized for cross-platform determinism)
        var bytes = File.ReadAllBytes(fixturePath);
        var normalized = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        var hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        var hashHex = Convert.ToHexString(hashBytes);
        Assert.Equal(CommittedBaselineSha256, hashHex, ignoreCase: true);

        // 2. Load and assert structural invariants
        var json = File.ReadAllText(fixturePath);
        var entries = JsonSerializer.Deserialize<List<CoastalChargeLinkResultEntry>>(json, JsonOptions);
        Assert.NotNull(entries);
        Assert.Equal(814, entries.Count);

        // 3. Strict ordinal sorting and no duplicate keys
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            var key = $"{entries[i].OuterEntryFullPath}|{entries[i].NestedEntryRelativePath}";
            Assert.True(seenKeys.Add(key), $"Duplicate key in baseline fixture: {key}");

            if (i > 0)
            {
                var prevKey = $"{entries[i - 1].OuterEntryFullPath}|{entries[i - 1].NestedEntryRelativePath}";
                var cmpOuter = string.Compare(entries[i - 1].OuterEntryFullPath, entries[i].OuterEntryFullPath, StringComparison.Ordinal);
                if (cmpOuter == 0)
                {
                    var cmpInner = string.Compare(entries[i - 1].NestedEntryRelativePath, entries[i].NestedEntryRelativePath, StringComparison.Ordinal);
                    Assert.True(cmpInner < 0, $"Entries out of ordinal order at index {i}: '{prevKey}' vs '{key}'");
                }
                else
                {
                    Assert.True(cmpOuter < 0, $"Outer entries out of ordinal order at index {i}: '{prevKey}' vs '{key}'");
                }
            }
        }

        // 4. Assert totals match baseline distribution
        int dupCount = entries.Count(e => e.Outcome == PilotLinkOutcome.ManifestDuplicateBypassed);
        int outOfScopeCount = entries.Count(e => e.Outcome == PilotLinkOutcome.UnlinkedOutOfScope);
        int autoCount = entries.Count(e => e.Outcome == PilotLinkOutcome.AutoAccepted);
        int reviewCount = entries.Count(e => e.Outcome == PilotLinkOutcome.PendingReview);
        int unlinkedCount = entries.Count(e => e.Outcome == PilotLinkOutcome.UnlinkedNoCandidate);

        Assert.Equal(169, dupCount);
        Assert.Equal(105, outOfScopeCount);
        Assert.Equal(160, autoCount);
        Assert.Equal(14, reviewCount);
        Assert.Equal(366, unlinkedCount);
        Assert.Equal(814, dupCount + outOfScopeCount + autoCount + reviewCount + unlinkedCount);

        // 5. Assert event identity invariants
        foreach (var autoEntry in entries.Where(e => e.Outcome == PilotLinkOutcome.AutoAccepted))
        {
            Assert.False(string.IsNullOrWhiteSpace(autoEntry.MatchedEventSerialNumber),
                $"AutoAccepted entry missing MatchedEventSerialNumber: {autoEntry.NestedEntryRelativePath}");
            Assert.NotNull(autoEntry.MatchedEventType);
            Assert.NotNull(autoEntry.MatchedRocChargeId);
            Assert.NotNull(autoEntry.MatchedRocChargeEventId);
        }
        foreach (var nonAuto in entries.Where(e => e.Outcome != PilotLinkOutcome.AutoAccepted))
        {
            Assert.Null(nonAuto.MatchedEventSerialNumber);
            Assert.Null(nonAuto.MatchedEventType);
        }
    }

    [SkippableFact]
    public void Execute_MatchesBaselineFixtureExactly()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal fixture files not found.");

        var (charges, events) = LoadCoastalWorkbooks();

        using var zipStream = File.OpenRead(ZipPath);
        var result = CoastalChargeLinkService.Execute(zipStream, charges, events);

        var fixturePath = GetFixturePath();
        Assert.True(File.Exists(fixturePath), $"Baseline fixture file not found at {fixturePath}");

        var json = File.ReadAllText(fixturePath);
        var baselineEntries = JsonSerializer.Deserialize<List<CoastalChargeLinkResultEntry>>(json, JsonOptions);
        Assert.NotNull(baselineEntries);
        Assert.Equal(baselineEntries.Count, result.Entries.Count);

        for (int i = 0; i < result.Entries.Count; i++)
        {
            var actual = result.Entries[i];
            var expected = baselineEntries[i];

            Assert.Equal(expected.OuterEntryFullPath, actual.OuterEntryFullPath);
            Assert.Equal(expected.NestedEntryRelativePath, actual.NestedEntryRelativePath);
            Assert.Equal(expected.Sha256Hex, actual.Sha256Hex);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.Reason, actual.Reason);
            Assert.Equal(expected.MatchedRocChargeId, actual.MatchedRocChargeId);
            Assert.Equal(expected.MatchedRocChargeEventId, actual.MatchedRocChargeEventId);
            Assert.Equal(expected.MatchedEventSerialNumber, actual.MatchedEventSerialNumber);
            Assert.Equal(expected.MatchedEventType, actual.MatchedEventType);
            Assert.Equal(expected.DateMatchMode, actual.DateMatchMode);
            Assert.Equal(expected.MatchFailureReason, actual.MatchFailureReason);
            Assert.Equal(expected.IsCanonical, actual.IsCanonical);
            Assert.Equal(expected.CanonicalOuterEntryFullPath, actual.CanonicalOuterEntryFullPath);
            Assert.Equal(expected.CanonicalNestedEntryRelativePath, actual.CanonicalNestedEntryRelativePath);

            // Compare deserialized normalized EvidenceJson
            using var docExpected = JsonDocument.Parse(expected.EvidenceJson);
            using var docActual = JsonDocument.Parse(actual.EvidenceJson);
            Assert.Equal(docExpected.RootElement.ToString(), docActual.RootElement.ToString());
        }
    }

    [SkippableFact]
    public void PendingReview_MatchesExpectedDateMismatchesAndContradictions()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal fixture files not found.");

        var (charges, events) = LoadCoastalWorkbooks();

        using var zipStream = File.OpenRead(ZipPath);
        var result = CoastalChargeLinkService.Execute(zipStream, charges, events);

        var reviewEntries = result.Entries.Where(e => e.Outcome == PilotLinkOutcome.PendingReview).ToList();
        Assert.Equal(14, reviewEntries.Count);

        var dateMismatches = reviewEntries.Where(e => e.Reason == PilotLinkReason.DateMismatch).ToList();
        var dateContradictions = reviewEntries.Where(e => e.Reason == PilotLinkReason.DateContradiction).ToList();

        Assert.Equal(10, dateMismatches.Count);
        Assert.Equal(4, dateContradictions.Count);

        // Exhaustive assertion of all 14 PendingReview entries in exact ordinal sorted order
        (string Outer, string Nested, PilotLinkReason Reason)[] expectedReviewEntries =
        [
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70912_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70912_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/ee0ce4b44acb7f00276dd4494bec1b70v1-Form CHG-1-050315-110814-ChargeId-10215822.pdf", PilotLinkReason.DateContradiction),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70916_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70916_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/72537cb1efb1a36686723d8867ef50d3v1-Form 8-210712-ChargeId-10365759.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70917_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70917_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/756bdb5ad9edc1696f46094cf3dbb13fv1-Form 8-270911-ChargeId-10307966.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70927_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70927_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/715292c61e2799fd200d720521d44a3dv1-Form 8-260810-300610-ChargeId-10234873.pdf", PilotLinkReason.DateContradiction),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70927_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70927_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/f8a9cc65ec91010a41210959a1c2b8edv1-Form 17-011110-180910-ChargeId-10063252.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70927_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70927_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/fb3a5de14c793f992cad97b025732054v1-Form 17-191010-180907-ChargeId-10063252.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70928_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70928_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/cbbc6e6caac8272496c1dc80f98db69bv1-Form 8-300410-260410-ChargeId-10158037.pdf", PilotLinkReason.DateContradiction),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70928_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70928_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/d6e43786704b7bf513b6287edb431069v1-Form 8-050510-190310-ChargeId-10215822.pdf", PilotLinkReason.DateContradiction),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70933_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70933_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/3d6ca100d50a9cd9d4f716f22ee2362cv1-Form 8-190707-ChargeId-10063253.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70933_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70933_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/b725b6a83c0763aba58c561f94816b7cv1-Form 8-100108-ChargeId-10081297.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70933_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70933_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/bbdb71804d7674dfc7a1fe49023a3bd8v1-Form 8-201207-ChargeId-10078342.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70933_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70933_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/eca3917700791845f877db659021fe79v1-Form 8-190707-ChargeId-10063252.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70933_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70933_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/edf38de8252cac778ebc9c85fa0666f2v1-Form 8-220408-ChargeId-10097112.pdf", PilotLinkReason.DateMismatch),
            ("COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/70933_COASTAL_PROJECTS_U45203OR1995PLC003982.zip", "70933_COASTAL_PROJECTS_U45203OR1995PLC003982/Charge Documents/f7495b0c6aa716119b21f12d02a346b7v1-Form 8-151107-ChargeId-10153553.pdf", PilotLinkReason.DateMismatch)
        ];

        Assert.Equal(expectedReviewEntries.Length, reviewEntries.Count);
        for (int i = 0; i < expectedReviewEntries.Length; i++)
        {
            Assert.Equal(expectedReviewEntries[i].Outer, reviewEntries[i].OuterEntryFullPath);
            Assert.Equal(expectedReviewEntries[i].Nested, reviewEntries[i].NestedEntryRelativePath);
            Assert.Equal(expectedReviewEntries[i].Reason, reviewEntries[i].Reason);
        }
    }

    [Fact]
    public void CorroborationConflict_UnitTests()
    {
        var matcher = new ChargeCompositeKeyMatcher();

        var charge = new RocCharge
        {
            ChargeId = 100,
            RocChargeNumber = "12345",
            CurrentAmount = 50.0m,
            LatestChargeHolderNormalized = "STATE BANK OF INDIA"
        };

        var chargeEvent = new RocChargeEvent
        {
            ChargeEventId = 200,
            RocChargeId = 100,
            RocCharge = charge,
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2020, 1, 15),
            FilingDate = new DateOnly(2020, 2, 1),
            ChargeAmount = 50.0m,
            HolderNameNormalized = "STATE BANK OF INDIA"
        };
        charge.Events.Add(chargeEvent);

        // 1. Conflicting amount against event amount
        var candidateAmountConflict = new ChargeDocumentCandidate
        {
            ChargeId = "12345",
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2020, 1, 15),
            FilingDate = new DateOnly(2020, 2, 1),
            Amount = 999.0m // Contradicts 50.0m
        };
        var resAmountConflict = matcher.Match(candidateAmountConflict, [charge], [chargeEvent]);
        Assert.False(resAmountConflict.IsMatched);
        Assert.Equal(ChargeMatchFailureReason.ConflictingCorroboration, resAmountConflict.FailureReasonCode);

        // 2. Conflicting holder name
        var candidateHolderConflict = new ChargeDocumentCandidate
        {
            ChargeId = "12345",
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2020, 1, 15),
            FilingDate = new DateOnly(2020, 2, 1),
            HolderName = "ICICI BANK" // Contradicts STATE BANK OF INDIA
        };
        var resHolderConflict = matcher.Match(candidateHolderConflict, [charge], [chargeEvent]);
        Assert.False(resHolderConflict.IsMatched);
        Assert.Equal(ChargeMatchFailureReason.ConflictingCorroboration, resHolderConflict.FailureReasonCode);

        // 3. Matching corroboration
        var candidateMatching = new ChargeDocumentCandidate
        {
            ChargeId = "12345",
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2020, 1, 15),
            FilingDate = new DateOnly(2020, 2, 1),
            Amount = 50.0m,
            HolderName = "STATE BANK"
        };
        var resMatching = matcher.Match(candidateMatching, [charge], [chargeEvent]);
        Assert.True(resMatching.IsMatched);
        Assert.Equal(ChargeDateMatchMode.BothDatesMatched, resMatching.DateMatchMode);
    }

    [Fact]
    public void EventTypeMismatch_UnitTests()
    {
        var matcher = new ChargeCompositeKeyMatcher();

        var charge = new RocCharge
        {
            ChargeId = 100,
            RocChargeNumber = "12345"
        };

        var creationEvent = new RocChargeEvent
        {
            ChargeEventId = 200,
            RocChargeId = 100,
            RocCharge = charge,
            EventType = ChargeEventType.Creation,
            EventDate = new DateOnly(2020, 1, 15),
            FilingDate = new DateOnly(2020, 2, 1)
        };
        charge.Events.Add(creationEvent);

        // Candidate requests Satisfaction, but the charge only has Creation on that date
        var candidateSatisfaction = new ChargeDocumentCandidate
        {
            ChargeId = "12345",
            EventType = ChargeEventType.Satisfaction,
            EventDate = new DateOnly(2020, 1, 15),
            FilingDate = new DateOnly(2020, 2, 1)
        };

        var result = matcher.Match(candidateSatisfaction, [charge], [creationEvent]);
        Assert.False(result.IsMatched);
        Assert.Equal(ChargeMatchFailureReason.EventTypeMismatch, result.FailureReasonCode);
    }

    [Fact]
    public void MalformedDate_RoutesToPendingReview_WithInvalidDateEvidence()
    {
        var entry = new CoastalManifestEntry
        {
            OuterEntryFullPath = "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/test.zip",
            NestedZipFileName = "test.zip",
            NestedEntryRelativePath = "Charge Documents/Form 8-310211-ChargeId-10063252.pdf", // Feb 31 is invalid calendar date
            UncompressedByteLength = 1000,
            Sha256Hex = "fakehash",
            IsCanonical = true,
            CanonicalOuterEntryFullPath = "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/test.zip",
            CanonicalNestedEntryRelativePath = "Charge Documents/Form 8-310211-ChargeId-10063252.pdf",
            CanonicalSha256Hex = "fakehash"
        };

        var candidate = ChargeCandidateExtractor.Extract(entry);
        Assert.True(candidate.HasMalformedDate);
        Assert.Equal("310211", candidate.MalformedDateRawToken);

        // Simulate linking pass routing
        var mockInventory = new CoastalInventoryResult([], [entry]);
        using var emptyStream = new MemoryStream();
        var linkResult = CoastalChargeLinkService.Execute(emptyStream, [], [], mockInventory);

        var resultEntry = Assert.Single(linkResult.Entries);
        Assert.Equal(PilotLinkOutcome.PendingReview, resultEntry.Outcome);
        Assert.Equal(PilotLinkReason.InvalidDateEvidence, resultEntry.Reason);
    }

    [Fact]
    public void GenericFileHandling_MixedVsNonChargeFolder()
    {
        // 1. Generic file in mixed outer folder ("Charge Documents Financial Documets") falls back to Charge -> MissingChargeId
        var mixedFolderEntry = new CoastalManifestEntry
        {
            OuterEntryFullPath = "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/test.zip",
            NestedZipFileName = "test.zip",
            NestedEntryRelativePath = "Charge Documents/Optional Attachment-(1).pdf",
            UncompressedByteLength = 1000,
            Sha256Hex = "fakehash1",
            IsCanonical = true,
            CanonicalOuterEntryFullPath = "COASTAL PROJECTS LIMITED Documents/Charge Documents Financial Documets/test.zip",
            CanonicalNestedEntryRelativePath = "Charge Documents/Optional Attachment-(1).pdf",
            CanonicalSha256Hex = "fakehash1"
        };

        var mixedCandidate = ChargeCandidateExtractor.Extract(mixedFolderEntry);
        Assert.Equal(FilingCategory.Charge, mixedCandidate.Category);
        Assert.Null(mixedCandidate.ExtractedChargeId);

        var mockInvMixed = new CoastalInventoryResult([], [mixedFolderEntry]);
        using var stream1 = new MemoryStream();
        var resMixed = CoastalChargeLinkService.Execute(stream1, [], [], mockInvMixed);
        var entryMixed = Assert.Single(resMixed.Entries);
        Assert.Equal(PilotLinkOutcome.UnlinkedNoCandidate, entryMixed.Outcome);
        Assert.Equal(PilotLinkReason.MissingChargeId, entryMixed.Reason);

        // 2. Generic file in non-charge outer folder ("Incorporation and Other Documents") falls back to Constitutional -> NonChargeDocument
        var nonChargeFolderEntry = new CoastalManifestEntry
        {
            OuterEntryFullPath = "COASTAL PROJECTS LIMITED Documents/Incorporation and Other Documents/test.zip",
            NestedZipFileName = "test.zip",
            NestedEntryRelativePath = "Incorporation Documents/Optional Attachment-(1).pdf",
            UncompressedByteLength = 1000,
            Sha256Hex = "fakehash2",
            IsCanonical = true,
            CanonicalOuterEntryFullPath = "COASTAL PROJECTS LIMITED Documents/Incorporation and Other Documents/test.zip",
            CanonicalNestedEntryRelativePath = "Incorporation Documents/Optional Attachment-(1).pdf",
            CanonicalSha256Hex = "fakehash2"
        };

        var nonChargeCandidate = ChargeCandidateExtractor.Extract(nonChargeFolderEntry);
        Assert.NotEqual(FilingCategory.Charge, nonChargeCandidate.Category);

        var mockInvNonCharge = new CoastalInventoryResult([], [nonChargeFolderEntry]);
        using var stream2 = new MemoryStream();
        var resNonCharge = CoastalChargeLinkService.Execute(stream2, [], [], mockInvNonCharge);
        var entryNonCharge = Assert.Single(resNonCharge.Entries);
        Assert.Equal(PilotLinkOutcome.UnlinkedOutOfScope, entryNonCharge.Outcome);
        Assert.Equal(PilotLinkReason.NonChargeDocument, entryNonCharge.Reason);
    }

    [SkippableFact]
    public void Repeatability_TwoPassesProduceByteIdenticalResults()
    {
        Skip.If(!File.Exists(ZipPath) || !File.Exists(RocPath) || !File.Exists(ChargePath), "Coastal fixture files not found.");

        var (charges1, events1) = LoadCoastalWorkbooks();
        using var zipStream1 = File.OpenRead(ZipPath);
        var result1 = CoastalChargeLinkService.Execute(zipStream1, charges1, events1);
        var json1 = JsonSerializer.Serialize(result1.Entries, JsonOptions);

        var (charges2, events2) = LoadCoastalWorkbooks();
        using var zipStream2 = File.OpenRead(ZipPath);
        var result2 = CoastalChargeLinkService.Execute(zipStream2, charges2, events2);
        var json2 = JsonSerializer.Serialize(result2.Entries, JsonOptions);

        Assert.Equal(json1, json2);
    }
}
